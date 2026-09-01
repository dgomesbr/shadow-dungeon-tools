using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PerfPatches;

/// <summary>
/// FrameTimeOverlay + BenchmarkMode. No game patches - this is the measurement harness the
/// other modules are judged against, so it must itself be allocation-free on the hot path:
/// the only steady-state allocations are the 4 Hz stats-string rebuild (and only while the
/// window is visible) and the one-shot CSV write when a benchmark completes.
/// </summary>
internal static class OverlayModule
{
    private const int WindowId = 49314;              // registry: 49265/49277/49309-49313 taken
    private const string PatchName = "FrameTimeOverlay";
    private const float StatsRebuildInterval = 0.25f; // 4 Hz
    private const float BenchmarkSeconds = 60f;
    // 60 s at up to 1000 fps (240 KB). Sized for uncapped/high-refresh setups so a capture
    // never ends early and silently shortens the measured window.
    private const int BenchmarkCapacity = 60000;

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<bool> _benchShowOverlay;

    // True once the frame hooks are registered. The Mods menu can be built before or after this
    // module initializes (plugin load order is not guaranteed), and without the hooks a started
    // benchmark would never tick to completion - so every menu action is gated on this.
    private static bool _installed;

    // ---- overlay state -------------------------------------------------------------------
    private static readonly float[] Ring = new float[512];
    private static int _ringIndex;
    private static int _ringCount;
    // Scratch for the 1%-low sort. Sorting a copy at 4 Hz is cheap (512 floats) and
    // Array.Sort on a float[] range is in-place, so no per-rebuild heap traffic.
    private static readonly float[] SortScratch = new float[512];

    private static bool _visible;
    private static float _nextRebuildAt;
    private static string _statsText = "collecting...";
    private static readonly StringBuilder Sb = new StringBuilder(256);
    private static Rect _windowRect = new Rect(10f, 10f, 240f, 118f);
    // Cached delegate: passing the method group to GUI.Window would allocate a new
    // GUI.WindowFunction every OnGUI event.
    private static readonly GUI.WindowFunction DrawWindowFunc = DrawWindow;

    // GC-rate anchor: rate is extrapolated to collections/min from a sliding window that is
    // re-anchored every 30 s so the figure reacts to patch toggles within a benchmark run.
    private static int _gcAnchorCount;
    private static float _gcAnchorTime;

    // ---- benchmark state -----------------------------------------------------------------
    private static readonly float[] Bench = new float[BenchmarkCapacity];
    private static bool _benchActive;
    private static int _benchIndex;
    private static float _benchStartTime;
    private static int _benchGen0Start;
    private static int _benchGen1Start;
    private static int _benchGen2Start;
    private static float _benchNextProgressAt;
    private static bool _benchOverlayShown;   // recorded in the CSV: GC figures include overlay cost
    private static bool _benchRestoreHidden;  // overlay was hidden before we forced it on

    internal static void Init(ConfigFile config, Harmony harmony)
    {
        _enabled = config.Bind(PatchName, "Enabled", true,
            "Frame-time overlay and benchmark capture. Applies no game patches and changes no " +
            "behavior; the overlay stays hidden until the 'FPS Overlay' row in the Mods menu is " +
            "clicked, so the steady-state cost when hidden is one ring-buffer write per frame. " +
            "Risk: none.");
        _benchShowOverlay = config.Bind(PatchName, "ShowOverlayDuringBenchmark", true,
            "Force the overlay visible while a benchmark captures, so you can see the countdown " +
            "and know it is running. The overlay itself allocates a little while visible, so its " +
            "cost is included in the run's GC figures - keep this setting the SAME across runs " +
            "you intend to compare (the CSV records which mode was used). Set false for the " +
            "cleanest possible GC numbers, but then the capture runs invisibly and only the log " +
            "reports progress.");

        if (!_enabled.Value)
        {
            return;
        }

        // No harmony targets to verify, but keep the per-patch fail-soft contract: a broken
        // hook registration must not take down the other modules.
        try
        {
            PerfCore.OnUpdate(PatchName, OnUpdate);
            PerfCore.OnGui(PatchName, OnGui);
            _gcAnchorCount = GC.CollectionCount(0);
            _gcAnchorTime = Time.realtimeSinceStartup;
            _installed = true;
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(PatchName + " not installed: " + ex.Message);
        }
    }

    // ---- Mods-menu surface ----------------------------------------------------------------
    // These replace the former F8 / Shift+F8 hotkeys. ModMenuProvider is the only caller.

    /// <summary>True when the module's frame hooks are live (config Enabled + hooks registered).</summary>
    internal static bool IsAvailable
    {
        get { return _installed; }
    }

    /// <summary>Overlay window visibility. Setting it true forces an immediate stats rebuild,
    /// exactly as the old toggle hotkey did.</summary>
    internal static bool IsVisible
    {
        get { return _visible; }
        set
        {
            if (_visible == value)
            {
                return;
            }
            _visible = value;
            if (_visible)
            {
                _nextRebuildAt = 0f; // force an immediate stats rebuild
            }
        }
    }

    /// <summary>True while a benchmark capture is running.</summary>
    internal static bool IsBenchmarking
    {
        get { return _benchActive; }
    }

    /// <summary>Whole seconds left in the running capture (0 when idle), rounded up so the
    /// countdown never shows 0 while frames are still being recorded.</summary>
    internal static int BenchmarkSecondsRemaining
    {
        get
        {
            if (!_benchActive)
            {
                return 0;
            }
            float remaining = BenchmarkSeconds - (Time.realtimeSinceStartup - _benchStartTime);
            if (remaining <= 0f)
            {
                return 0;
            }
            int whole = (int)Math.Ceiling(remaining);
            return whole > (int)BenchmarkSeconds ? (int)BenchmarkSeconds : whole;
        }
    }

    /// <summary>
    /// Starts a capture, or cancels the running one. Keeps the 3-second accidental-cancel guard
    /// the Shift+F8 handler had: a click within 3 s of the start is treated as "did that work?"
    /// and only logs, so a double-click cannot silently discard a fresh run.
    /// </summary>
    internal static void StartOrCancelBenchmark()
    {
        if (!_installed)
        {
            // Without the Update hook a started capture would never finish - refuse instead.
            PerfCore.Log.LogWarning(PatchName + " is disabled in the config - benchmark not started.");
            return;
        }

        if (!_benchActive)
        {
            StartBenchmark();
            return;
        }

        float elapsed = Time.realtimeSinceStartup - _benchStartTime;
        if (elapsed < 3f)
        {
            PerfCore.Log.LogInfo("Benchmark already running (" + elapsed.ToString("F1")
                + "s elapsed of " + BenchmarkSeconds + "s) - ignoring repeat press. "
                + "Press again after 3s to cancel.");
            return;
        }

        CancelBenchmark();
        PerfCore.Log.LogWarning("Benchmark CANCELLED before finishing - no CSV written.");
    }

    private static void OnUpdate()
    {
        float dt = Time.unscaledDeltaTime;

        Ring[_ringIndex] = dt;
        _ringIndex = (_ringIndex + 1) & (Ring.Length - 1);
        if (_ringCount < Ring.Length)
        {
            _ringCount++;
        }

        if (_benchActive)
        {
            if (_benchIndex < BenchmarkCapacity)
            {
                Bench[_benchIndex++] = dt;
            }
            float benchElapsed = Time.realtimeSinceStartup - _benchStartTime;
            if (Time.realtimeSinceStartup >= _benchNextProgressAt && benchElapsed < BenchmarkSeconds)
            {
                _benchNextProgressAt = Time.realtimeSinceStartup + 15f;
                PerfCore.Log.LogInfo("Benchmark running: " + benchElapsed.ToString("F0") + "s / "
                    + BenchmarkSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s, "
                    + _benchIndex + " frames captured.");
            }
            if (benchElapsed >= BenchmarkSeconds || _benchIndex >= BenchmarkCapacity)
            {
                FinishBenchmark();
            }
        }

        // No input handling here any more: the overlay toggle and the benchmark start/cancel are
        // driven exclusively from the Mods menu (see ModMenuProvider) through IsVisible and
        // StartOrCancelBenchmark.

        // Stats rebuild only while the window is actually shown - see OnGui for why a running
        // benchmark must not generate garbage of its own.
        if (_visible && Time.realtimeSinceStartup >= _nextRebuildAt)
        {
            _nextRebuildAt = Time.realtimeSinceStartup + StatsRebuildInterval;
            RebuildStats();
        }
    }

    private static void RebuildStats()
    {
        int n = _ringCount;
        if (n == 0)
        {
            return;
        }

        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float v = Ring[i];
            SortScratch[i] = v;
            sum += v;
        }
        float avgMs = sum / n * 1000f;
        float avgFps = sum > 0f ? n / sum : 0f;

        // 1% low FPS = 1 / mean of the worst 1% frame times (ascending sort, take the tail).
        Array.Sort(SortScratch, 0, n);
        int worst = n / 100;
        if (worst < 1)
        {
            worst = 1;
        }
        float worstSum = 0f;
        for (int i = n - worst; i < n; i++)
        {
            worstSum += SortScratch[i];
        }
        float onePercentLow = worstSum > 0f ? worst / worstSum : 0f;

        float now = Time.realtimeSinceStartup;
        int gen0 = GC.CollectionCount(0);
        float elapsed = now - _gcAnchorTime;
        float gcPerMin = elapsed > 0.5f ? (gen0 - _gcAnchorCount) / elapsed * 60f : 0f;
        if (elapsed >= 30f)
        {
            _gcAnchorCount = gen0;
            _gcAnchorTime = now;
        }

        Sb.Length = 0;
        Sb.Append("FPS  ").Append(avgFps.ToString("F1", CultureInfo.InvariantCulture))
          .Append("   1% low ").Append(onePercentLow.ToString("F1", CultureInfo.InvariantCulture))
          .Append("\nFrame ").Append(avgMs.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms")
          .Append("\nGC gen0 ").Append(gcPerMin.ToString("F1", CultureInfo.InvariantCulture)).Append("/min");
        if (_benchActive)
        {
            float remaining = BenchmarkSeconds - (now - _benchStartTime);
            if (remaining < 0f)
            {
                remaining = 0f;
            }
            Sb.Append("\nBENCH ").Append(remaining.ToString("F0", CultureInfo.InvariantCulture)).Append("s left");
        }
        _statsText = Sb.ToString();
    }

    private static void OnGui()
    {
        // Deliberately NOT forced on here during a benchmark: IMGUI per-event allocations and the
        // 4 Hz stats string would be counted in the gen0 figures the benchmark reports. Use the
        // 'FPS Overlay' row in the Mods menu (or ShowOverlayDuringBenchmark, which StartBenchmark
        // honours) to watch the countdown, at the cost of a biased GC count.
        if (!_visible)
        {
            return;
        }

        _windowRect = GUI.Window(WindowId, _windowRect, DrawWindowFunc, "PerfPatches");

        // Round-trip + clamp: keep the drag handle reachable even after resolution changes.
        if (_windowRect.x < 0f) _windowRect.x = 0f;
        if (_windowRect.y < 0f) _windowRect.y = 0f;
        float maxX = Screen.width - _windowRect.width;
        float maxY = Screen.height - _windowRect.height;
        if (maxX < 0f) maxX = 0f;
        if (maxY < 0f) maxY = 0f;
        if (_windowRect.x > maxX) _windowRect.x = maxX;
        if (_windowRect.y > maxY) _windowRect.y = maxY;
    }

    private static void DrawWindow(int id)
    {
        // Fixed-rect GUI.Label (Rect is a struct - no heap traffic), then DragWindow last so
        // the label never swallows the drag events.
        GUI.Label(new Rect(10f, 22f, 220f, 90f), _statsText);
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }

    private static void StartBenchmark()
    {
        _benchActive = true;
        _benchIndex = 0;
        _benchStartTime = Time.realtimeSinceStartup;
        _benchGen0Start = GC.CollectionCount(0);
        _benchGen1Start = GC.CollectionCount(1);
        _benchGen2Start = GC.CollectionCount(2);
        _nextRebuildAt = 0f;
        _benchNextProgressAt = _benchStartTime + 15f;
        _benchRestoreHidden = false;
        if (_benchShowOverlay.Value && !_visible)
        {
            _visible = true;             // give the capture visible feedback
            _benchRestoreHidden = true;  // and put it back afterwards
        }
        _benchOverlayShown = _visible;
        PerfCore.Log.LogInfo("Benchmark started: capturing " +
            BenchmarkSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s of frame times"
            + (_benchOverlayShown ? " (overlay visible - its cost is in the GC figures)" : " (overlay hidden)")
            + ". Play normally; the CSV lands in BepInEx/plugins/PerfBench/.");
    }

    private static void CancelBenchmark()
    {
        _benchActive = false;
        if (_benchRestoreHidden)
        {
            _visible = false;
            _benchRestoreHidden = false;
        }
    }

    private static void FinishBenchmark()
    {
        _benchActive = false;
        if (_benchRestoreHidden)
        {
            _visible = false;
            _benchRestoreHidden = false;
        }
        int n = _benchIndex;
        if (n < 2)
        {
            PerfCore.Log.LogWarning("Benchmark aborted: no frames captured.");
            return;
        }

        float durationSec = Time.realtimeSinceStartup - _benchStartTime;
        int gen0 = GC.CollectionCount(0) - _benchGen0Start;
        int gen1 = GC.CollectionCount(1) - _benchGen1Start;
        int gen2 = GC.CollectionCount(2) - _benchGen2Start;
        float gen0PerMin = durationSec > 0f ? gen0 / durationSec * 60f : 0f;

        // Completion path may allocate freely - it runs once. Build the CSV in capture order
        // first, THEN sort Bench in place for percentiles (capture is over, order is disposable).
        try
        {
            string dir = Path.Combine(Paths.PluginPath, "PerfBench");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");

            var csv = new StringBuilder(n * 8 + 512);
            csv.AppendLine("frame,ms");
            for (int i = 0; i < n; i++)
            {
                csv.Append(i).Append(',')
                   .Append((Bench[i] * 1000f).ToString("F3", CultureInfo.InvariantCulture))
                   .AppendLine();
            }

            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                sum += Bench[i];
            }
            Array.Sort(Bench, 0, n);
            float avgMs = sum / n * 1000f;
            float medianMs = Percentile(n, 0.50f) * 1000f;
            float p95Ms = Percentile(n, 0.95f) * 1000f;
            float p99Ms = Percentile(n, 0.99f) * 1000f;

            int worst = n / 100;
            if (worst < 1)
            {
                worst = 1;
            }
            float worstSum = 0f;
            for (int i = n - worst; i < n; i++)
            {
                worstSum += Bench[i];
            }
            float onePercentLowFps = worstSum > 0f ? worst / worstSum : 0f;

            string summary = string.Format(CultureInfo.InvariantCulture,
                "summary,frames={0},duration_s={1:F2},avg_ms={2:F3},median_ms={3:F3},p95_ms={4:F3},p99_ms={5:F3},low1pct_fps={6:F1},gen0_per_min={7:F1},gen0={8},gen1={9},gen2={10},overlay_visible={11},vsync={12},target_fps={13}",
                n, durationSec, avgMs, medianMs, p95Ms, p99Ms, onePercentLowFps, gen0PerMin, gen0, gen1, gen2,
                // vsync/frame cap are recorded because a capped run hides every CPU-side gain:
                // if vSyncCount > 0 the frame rate is pinned to the display and A/B runs look identical.
                _benchOverlayShown, QualitySettings.vSyncCount, Application.targetFrameRate);
            csv.AppendLine(summary);

            File.WriteAllText(path, csv.ToString());
            PerfCore.Log.LogInfo("Benchmark complete -> " + path);
            PerfCore.Log.LogInfo(summary);
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogError("Benchmark write failed (run discarded): " + ex);
        }
    }

    /// <summary>Nearest-rank percentile over the already-sorted Bench[0..n).</summary>
    private static float Percentile(int n, float p)
    {
        int rank = (int)Math.Ceiling(p * n) - 1;
        if (rank < 0)
        {
            rank = 0;
        }
        if (rank >= n)
        {
            rank = n - 1;
        }
        return Bench[rank];
    }
}

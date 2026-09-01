using System;
using System.Globalization;

namespace PerfPatches;

/// <summary>
/// Rows contributed to the shared floating "Mods" menu (the single point of interaction that
/// replaced this plugin's F8 / Shift+F8 hotkeys). Discovered by the menu plugin through
/// reflection on the exact type name PerfPatches.ModMenuProvider, so the signature below must
/// not change: a rename silently drops these rows instead of failing loudly.
///
/// Contract: each row is new object[] { string id, Func&lt;string&gt; label, Func&lt;bool&gt; state,
/// Action onClick, Func&lt;string&gt; description }. The trailing description (hover tooltip text)
/// is optional in the contract but supplied for both rows here. Nothing here may ever throw -
/// the menu draws every plugin's rows in one IMGUI pass, so a single bad delegate would take the
/// whole menu down.
/// </summary>
public static class ModMenuProvider
{
    private const string OverlayLabel = "FPS Overlay";
    private const string BenchIdleLabel = "Run Benchmark";
    private const string BenchFallbackLabel = "Benchmark";

    // The benchmark label changes once a second while capturing. label() is called every frame
    // by the menu, so the formatted string is cached per whole second: this module is the harness
    // the other patches are measured against and must not add its own GC traffic to a running
    // capture.
    private static int _cachedSeconds = -1;
    private static string _cachedBenchLabel = BenchIdleLabel;

    public static object[][] GetMenuItems()
    {
        try
        {
            return new object[][]
            {
                new object[]
                {
                    "perf.overlay",
                    (Func<string>)OverlayRowLabel,
                    (Func<bool>)OverlayRowState,
                    (Action)OverlayRowClick,
                    (Func<string>)OverlayRowDescription
                },
                new object[]
                {
                    "perf.bench",
                    (Func<string>)BenchRowLabel,
                    (Func<bool>)BenchRowState,
                    (Action)BenchRowClick,
                    (Func<string>)BenchRowDescription
                }
            };
        }
        catch
        {
            return new object[0][];
        }
    }

    // ---- FPS overlay row ---------------------------------------------------------------------

    private static string OverlayRowLabel()
    {
        return OverlayLabel;
    }

    private static bool OverlayRowState()
    {
        try
        {
            return OverlayModule.IsVisible;
        }
        catch
        {
            return false;
        }
    }

    private static string OverlayRowDescription()
    {
        try
        {
            if (!OverlayModule.IsAvailable)
            {
                return "The frame-time overlay is unavailable: the module is switched off in this plugin's config, so this row does nothing.";
            }
            return "Shows a frame-time overlay with average FPS, 1% low, frame milliseconds and GC collections per minute. Costs a little performance while visible.";
        }
        catch
        {
            return "Shows a frame-time overlay with average FPS, 1% low, frame milliseconds and GC collections per minute.";
        }
    }

    private static void OverlayRowClick()
    {
        try
        {
            // When the module is disabled in the config its OnGui hook is not registered, so a
            // "visible" overlay would never actually draw - leave the row inert instead of
            // lighting up for nothing.
            if (!OverlayModule.IsAvailable)
            {
                return;
            }
            OverlayModule.IsVisible = !OverlayModule.IsVisible;
        }
        catch
        {
            // Menu clicks are fire-and-forget: swallow rather than break the menu.
        }
    }

    // ---- benchmark row -----------------------------------------------------------------------

    private static string BenchRowLabel()
    {
        try
        {
            if (!OverlayModule.IsBenchmarking)
            {
                _cachedSeconds = -1;
                return BenchIdleLabel;
            }
            int remaining = OverlayModule.BenchmarkSecondsRemaining;
            if (remaining != _cachedSeconds)
            {
                _cachedSeconds = remaining;
                _cachedBenchLabel = "Bench " + remaining.ToString(CultureInfo.InvariantCulture) + "s left";
            }
            return _cachedBenchLabel;
        }
        catch
        {
            return BenchFallbackLabel;
        }
    }

    // Static strings only (no per-frame concatenation): the benchmark is the harness the other
    // patches are measured against, so the tooltip must not add GC traffic to a running capture.
    private static string BenchRowDescription()
    {
        try
        {
            if (OverlayModule.IsBenchmarking)
            {
                return "A 60-second frame-time capture is running; click again to cancel it. The CSV lands under BepInEx/plugins/PerfBench when it finishes.";
            }
            return "Captures 60 seconds of frame times to a CSV under BepInEx/plugins/PerfBench with average, median, p95, p99 and 1% low. Vsync hides differences.";
        }
        catch
        {
            return "Captures 60 seconds of frame times to a CSV under BepInEx/plugins/PerfBench for before/after comparisons.";
        }
    }

    private static bool BenchRowState()
    {
        try
        {
            return OverlayModule.IsBenchmarking;
        }
        catch
        {
            return false;
        }
    }

    private static void BenchRowClick()
    {
        try
        {
            // Start when idle; when already running this cancels, but only after the module's
            // 3-second accidental-cancel guard has elapsed (a click inside that window just logs).
            OverlayModule.StartOrCancelBenchmark();
        }
        catch
        {
        }
    }
}

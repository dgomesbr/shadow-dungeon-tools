using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FinkFramework.Runtime.Singleton;
using HarmonyLib;
using UnityEngine;

namespace CombatDpsMeter;

/// <summary>
/// Real in-level DPS meter. The game's own DPSManager.RecordDamage is hard-gated to the
/// HomeScene training dummy (it early-outs unless SceneManager.GetActiveScene().name ==
/// "HomeScene" AND enemy.IsDpsTarget), so it can never show dungeon DPS. This plugin
/// measures independently by postfixing every Enemy damage-intake method and reading the
/// actual HP removed (post-mitigation) via a prefix/postfix HP snapshot.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.dpsmeter";
    public const string PluginName = "Combat DPS Meter";
    public const string PluginVersion = "1.0.0";

    // IMGUI window id (reserved range 49300-49399 for custom plugins): 49309.
    private const int WindowId = 49309;
    // 32768 12-byte samples (~384 KB): dense AoE-DoT fights produce 100-400 events/sec, and the
    // buffer must be able to hold a full 120 s window without silently shrinking it.
    private const int BufferSize = 32768; // power of two, so we can mask instead of modulo
    private const int BufferMask = BufferSize - 1;
    private const float DisplayRefreshInterval = 0.25f;
    private const string PlayerKey = "Player";
    private const string DotKey = "DoT";
    private const string CompanionFallbackKey = "Companion";

    internal static ManualLogSource Log;

    private static ConfigEntry<KeyboardShortcut> ToggleHotkey;
    private static ConfigEntry<float> WindowSeconds;

    // ---- ring buffer (allocation-free after warmup) -------------------------------------
    private struct Sample
    {
        public float Time;
        public float Amount;
        public int SourceId;
    }

    private static readonly Sample[] Samples = new Sample[BufferSize];
    private static int _head;  // next write slot
    private static int _count; // valid samples in buffer

    // Per-source running sums, indexed by sourceId. Grows (allocates) only the first time a
    // brand new source name is seen; steady-state recording never allocates.
    private static float[] _sums = new float[16];
    private static readonly List<string> SourceNames = new List<string>(16);
    private static readonly Dictionary<string, int> SourceIds = new Dictionary<string, int>(16, StringComparer.Ordinal);

    private static bool _recordFault; // set once if a hook ever throws; stops recording, never spams

    // ---- cached display strings (rebuilt at 4 Hz while the window is open) --------------
    private string _totalText = "0";
    private string _peakText = "0";
    private string _windowText = "Window: 10s";
    private string[] _rowText = new string[16];
    private int _rowCount;
    private int[] _order = new int[16];
    private float[] _dpsScratch = new float[16];
    private float _peakDps;
    private float _nextRefreshAt;
    private string _hotkeyHint = "";

    private bool _visible;
    private Rect _rect = new Rect(40f, 80f, 300f, 0f);
    private GUIStyle _bigStyle;

    private Harmony _harmony;
    private bool _hooksOk;
    private ACTbar _lastBar; // level-change detection: scene-scoped singleton reference changes per level

    private void Awake()
    {
        Log = base.Logger;

        ToggleHotkey = base.Config.Bind("Window", "ToggleHotkey",
            new KeyboardShortcut(KeyCode.F9),
            "Shows/hides the DPS meter window.");
        WindowSeconds = base.Config.Bind("Meter", "RollingWindowSeconds", 10f,
            new ConfigDescription(
                "Length of the rolling DPS window in seconds. Damage older than this is dropped. Clamped to 1-120.",
                new AcceptableValueRange<float>(1f, 120f)));

        _hotkeyHint = "Toggle: " + ToggleHotkey.Value;

        _harmony = new Harmony(PluginGuid);
        try
        {
            PatchDamageIntake();
        }
        catch (Exception ex)
        {
            _hooksOk = false;
            Log.LogWarning("Combat DPS Meter: failed to hook Enemy damage intake, meter disabled: " + ex.Message);
        }

        Log.LogInfo(_hooksOk
            ? "Combat DPS Meter ready. Press " + ToggleHotkey.Value + " in a level."
            : "Combat DPS Meter is DISABLED (hooks unavailable).");
    }

    private void PatchDamageIntake()
    {
        // Verified against decompiled Enemy.cs: these are the only public methods that
        // subtract enemy HP from player/companion offense.
        MethodInfo takeDamage = AccessTools.Method(typeof(Enemy), nameof(Enemy.TakeDamage), new[]
        {
            typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
            typeof(float), typeof(DamageType), typeof(int), typeof(PlayerManager), typeof(Companion),
            typeof(SkillOBJ_DT_SP)
        });
        MethodInfo takeDot = AccessTools.Method(typeof(Enemy), nameof(Enemy.TakeDotDamage),
            new[] { typeof(DamageType), typeof(float), typeof(float) });
        MethodInfo takeDirect = AccessTools.Method(typeof(Enemy), nameof(Enemy.TakeDirectDamage),
            new[] { typeof(float), typeof(DamageType) });
        MethodInfo takeCutJump = AccessTools.Method(typeof(Enemy), nameof(Enemy.TakeCutJumpDamage),
            new[] { typeof(DamageType), typeof(float) });

        if (takeDamage == null)
        {
            Log.LogWarning("Combat DPS Meter: Enemy.TakeDamage signature not found - meter disabled.");
            return;
        }

        HarmonyMethod snapshot = new HarmonyMethod(typeof(Plugin), nameof(HpSnapshotPrefix));
        _harmony.Patch(takeDamage, snapshot, new HarmonyMethod(typeof(Plugin), nameof(TakeDamagePostfix)));
        _hooksOk = true;

        if (takeDot != null)
        {
            _harmony.Patch(takeDot, snapshot, new HarmonyMethod(typeof(Plugin), nameof(TakeDotDamagePostfix)));
        }
        else
        {
            Log.LogWarning("Combat DPS Meter: Enemy.TakeDotDamage not found - DoT damage will not be tracked.");
        }
        if (takeDirect != null)
        {
            _harmony.Patch(takeDirect, snapshot, new HarmonyMethod(typeof(Plugin), nameof(PlayerSourcedPostfix)));
        }
        else
        {
            Log.LogWarning("Combat DPS Meter: Enemy.TakeDirectDamage not found - crit-transfer damage will not be tracked.");
        }
        if (takeCutJump != null)
        {
            // Every TakeCutJumpDamage call site in the game is a DOT proc (DOT_MG.TakeDot:
            // CutJump_Rate, LayerPRC, FrozenCut), so it belongs in the DoT bucket, not Player.
            _harmony.Patch(takeCutJump, snapshot, new HarmonyMethod(typeof(Plugin), nameof(DotSourcedPostfix)));
        }
        else
        {
            Log.LogWarning("Combat DPS Meter: Enemy.TakeCutJumpDamage not found - jump percent damage will not be tracked.");
        }
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    // ---- Harmony callbacks (per-hit hot path: allocation-free after warmup) -------------

    private static void HpSnapshotPrefix(Enemy __instance, ref float __state)
    {
        try
        {
            EnemyStat hs = __instance.HealthStat;
            // ReferenceEquals: we only read plain fields through the getter, safe even if the
            // Unity object is destroyed, and it skips the expensive Unity == operator.
            __state = ReferenceEquals(hs, null) ? float.NaN : hs.CurrentValue;
        }
        catch (Exception ex)
        {
            __state = float.NaN;
            FaultOnce(ex);
        }
    }

    private static void TakeDamagePostfix(Enemy __instance, int indexType, Companion cp, float __state)
    {
        try
        {
            float applied = AppliedDelta(__instance, __state);
            if (applied <= 0f)
            {
                return;
            }
            // Same discriminator the game uses (indexType == 1 && cp != null => companion hit).
            string key = PlayerKey;
            if (indexType == 1 && !ReferenceEquals(cp, null))
            {
                key = cp.Name; // set to dt_cp.skillName at spawn (SK_FSQ_comp)
                if (string.IsNullOrEmpty(key))
                {
                    key = CompanionFallbackKey;
                }
            }
            Record(Time.time, applied, key);
        }
        catch (Exception ex)
        {
            FaultOnce(ex);
        }
    }

    private static void TakeDotDamagePostfix(Enemy __instance, float __state)
    {
        try
        {
            float applied = AppliedDelta(__instance, __state);
            if (applied > 0f)
            {
                // Enemy.TakeDotDamage carries no attacker parameter (the game even calls
                // ClearLastDamageCompanion there), so all DoT ticks share one bucket.
                Record(Time.time, applied, DotKey);
            }
        }
        catch (Exception ex)
        {
            FaultOnce(ex);
        }
    }

    private static void PlayerSourcedPostfix(Enemy __instance, float __state)
    {
        try
        {
            float applied = AppliedDelta(__instance, __state);
            if (applied > 0f)
            {
                Record(Time.time, applied, PlayerKey);
            }
        }
        catch (Exception ex)
        {
            FaultOnce(ex);
        }
    }

    private static void DotSourcedPostfix(Enemy __instance, float __state)
    {
        try
        {
            float applied = AppliedDelta(__instance, __state);
            if (applied > 0f)
            {
                Record(Time.time, applied, DotKey);
            }
        }
        catch (Exception ex)
        {
            FaultOnce(ex);
        }
    }

    private static float AppliedDelta(Enemy enemy, float hpBefore)
    {
        if (float.IsNaN(hpBefore))
        {
            return 0f;
        }
        EnemyStat hs = enemy.HealthStat;
        if (ReferenceEquals(hs, null))
        {
            return 0f;
        }
        return hpBefore - hs.CurrentValue;
    }

    private static void Record(float time, float amount, string key)
    {
        if (_recordFault)
        {
            return;
        }
        int id;
        if (!SourceIds.TryGetValue(key, out id))
        {
            id = SourceNames.Count;
            SourceIds.Add(key, id);
            SourceNames.Add(key);
            if (id >= _sums.Length)
            {
                Array.Resize(ref _sums, _sums.Length * 2);
            }
        }
        if (_count == BufferSize)
        {
            EvictOldest();
        }
        int idx = _head;
        Samples[idx].Time = time;
        Samples[idx].Amount = amount;
        Samples[idx].SourceId = id;
        _head = (idx + 1) & BufferMask;
        _count++;
        _sums[id] += amount;
    }

    private static void EvictOldest()
    {
        int tail = (_head - _count + BufferSize) & BufferMask;
        int id = Samples[tail].SourceId;
        _sums[id] -= Samples[tail].Amount;
        if (_sums[id] < 0f)
        {
            _sums[id] = 0f; // float drift guard
        }
        _count--;
    }

    private static void FaultOnce(Exception ex)
    {
        if (!_recordFault)
        {
            _recordFault = true;
            Log.LogError("Combat DPS Meter: recording disabled after hook error: " + ex);
        }
    }

    // ---- frame loop ----------------------------------------------------------------------

    private void Update()
    {
        try
        {
            // Not KeyboardShortcut.IsDown(): that rejects the press while any other key is held
            // (e.g. WASD movement), which makes a combat overlay untogglable in practice.
            if (HotkeyPressed(ToggleHotkey.Value))
            {
                _visible = !_visible;
            }

            // Auto-reset on level change: ACTbar is a scene-scoped SingletonMonoScope that is
            // recreated per level, so a reference change (including to/from null) means a new run.
            ACTbar bar = SingletonMonoScope<ACTbar>.HasInstance ? SingletonMonoScope<ACTbar>.Instance : null;
            if (!ReferenceEquals(bar, _lastBar))
            {
                _lastBar = bar;
                ResetMeter();
            }

            float window = WindowSeconds.Value;
            if (window < 1f)
            {
                window = 1f;
            }
            else if (window > 120f)
            {
                window = 120f;
            }

            // Evict samples that fell out of the rolling window (uses Time.time, matching the
            // record timestamps, so a paused game freezes the meter instead of draining it).
            float now = Time.time;
            float cutoff = now - window;
            while (_count > 0)
            {
                int tail = (_head - _count + BufferSize) & BufferMask;
                if (Samples[tail].Time >= cutoff)
                {
                    break;
                }
                EvictOldest();
            }

            if (_visible && Time.unscaledTime >= _nextRefreshAt)
            {
                _nextRefreshAt = Time.unscaledTime + DisplayRefreshInterval;
                RefreshDisplay(now, window);
            }
        }
        catch (Exception ex)
        {
            FaultOnce(ex);
        }
    }

    private void RefreshDisplay(float now, float window)
    {
        int sources = SourceNames.Count;
        if (_dpsScratch.Length < sources)
        {
            int newSize = _dpsScratch.Length * 2;
            while (newSize < sources)
            {
                newSize *= 2;
            }
            Array.Resize(ref _dpsScratch, newSize);
            Array.Resize(ref _order, newSize);
            Array.Resize(ref _rowText, newSize);
        }

        // Effective denominator: full window once the fight is long enough, otherwise the age
        // of the oldest sample (min 1s) so the first seconds of a fight are not underestimated.
        float denom = window;
        if (_count > 0)
        {
            int tail = (_head - _count + BufferSize) & BufferMask;
            denom = Mathf.Clamp(now - Samples[tail].Time, 1f, window);
        }

        // When the ring buffer is saturated the oldest samples were evicted early, so the real
        // averaging span is shorter than configured - surface that instead of lying.
        _windowText = _count == BufferSize && denom < window - 0.5f
            ? "Window: " + Mathf.RoundToInt(window) + "s (effective " + Mathf.RoundToInt(denom) + "s)"
            : "Window: " + Mathf.RoundToInt(window) + "s";

        float total = 0f;
        for (int i = 0; i < sources; i++)
        {
            _dpsScratch[i] = _sums[i] / denom;
            total += _dpsScratch[i];
        }
        _totalText = DamgeTextManager.FormatDamageNumber(total);
        if (total > _peakDps)
        {
            _peakDps = total;
            _peakText = DamgeTextManager.FormatDamageNumber(_peakDps);
        }

        int n = 0;
        for (int i = 0; i < sources; i++)
        {
            if (_sums[i] > 0f)
            {
                _order[n++] = i;
            }
        }
        // Insertion sort by DPS descending; n is tiny (player + a handful of companions).
        for (int i = 1; i < n; i++)
        {
            int v = _order[i];
            int j = i - 1;
            while (j >= 0 && _dpsScratch[_order[j]] < _dpsScratch[v])
            {
                _order[j + 1] = _order[j];
                j--;
            }
            _order[j + 1] = v;
        }
        _rowCount = n;
        for (int i = 0; i < n; i++)
        {
            int id = _order[i];
            float share = total > 0f ? _dpsScratch[id] / total * 100f : 0f;
            _rowText[i] = SourceNames[id] + " - " + DamgeTextManager.FormatDamageNumber(_dpsScratch[id])
                + " - " + share.ToString("0.0") + "%";
        }
    }

    // Main key pressed this frame + all configured modifiers held; unlike
    // KeyboardShortcut.IsDown() it does NOT fail when unrelated keys are held.
    private static bool HotkeyPressed(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
        {
            return false;
        }
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!Input.GetKey(modifier))
            {
                return false;
            }
        }
        return true;
    }

    private void ResetMeter()
    {
        _head = 0;
        _count = 0;
        Array.Clear(_sums, 0, _sums.Length);
        // SourceIds/SourceNames are intentionally kept so re-registering after a reset stays
        // allocation-free; sources with a zero sum are simply not displayed.
        _rowCount = 0;
        _peakDps = 0f;
        _totalText = "0";
        _peakText = "0";
        _nextRefreshAt = 0f;
    }

    // ---- IMGUI ----------------------------------------------------------------------------

    private void OnGUI()
    {
        if (!_visible)
        {
            return;
        }
        try
        {
            if (_bigStyle == null)
            {
                _bigStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Bold
                };
            }
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "Combat DPS Meter");
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }
        catch (Exception ex)
        {
            FaultOnce(ex);
        }
    }

    private void DrawWindow(int id)
    {
        GUILayout.BeginVertical(GUILayout.MinWidth(280f));
        if (!_hooksOk)
        {
            GUILayout.Label("Damage hooks unavailable - meter disabled.");
        }
        else if (_recordFault)
        {
            GUILayout.Label("Recording stopped after an error (see log).");
        }

        GUILayout.Label(_totalText + " DPS", _bigStyle);
        GUILayout.Label("Peak: " + _peakText + "  |  " + _windowText);

        if (_rowCount == 0)
        {
            GUILayout.Label(SingletonMonoScope<ACTbar>.HasInstance
                ? "No damage in window."
                : "Not in a level - enter a dungeon first.");
        }
        else
        {
            for (int i = 0; i < _rowCount; i++)
            {
                GUILayout.Label(_rowText[i]);
            }
        }

        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Reset", GUILayout.Width(90f)))
        {
            ResetMeter();
        }
        GUILayout.Label(_hotkeyHint);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();

        GUI.DragWindow(); // last, so the controls above stay clickable
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Data.RuntimeData.Skills.CompSkill;
using HarmonyLib;
using Lean.Pool;
using UnityEngine;

namespace VfxReducer;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.vfxreducer";
    public const string PluginName = "VFX Reducer";
    public const string PluginVersion = "1.0.0";

    internal enum VfxMode
    {
        Off = 0,
        Reduced = 1,
        Minimal = 2
    }

    // IMGUI window id (reserved range 49300-49399 for our plugins): 49312.
    private const int ToastWindowId = 49312;
    private const float ToastSeconds = 1.5f;

    internal static ManualLogSource Log;
    internal static ConfigEntry<int> ParticleBudgetPercent;
    internal static ConfigEntry<bool> MinimalAlsoDisablesTrails;
    internal static ConfigEntry<KeyboardShortcut> CycleModeHotkey;

    // Current mode. Session state only; every game launch starts at Off.
    internal static VfxMode Mode = VfxMode.Off;

    // Registry of markers on currently-active (spawned) pooled objects.
    // Markers self-register in OnEnable and self-remove in OnDisable, so a
    // mode change can sweep everything alive right now. HashSet<T> foreach
    // uses a struct enumerator - no allocation.
    internal static readonly HashSet<VfxClampMarker> LiveMarkers = new HashSet<VfxClampMarker>();

    // Preallocated scratch buffers for the spawn postfix (main thread only).
    // Unity's List-based GetComponentsInChildren clears the list itself.
    private static readonly List<ParticleSystem> PsBuffer = new List<ParticleSystem>(64);
    private static readonly List<TrailRenderer> TrailBuffer = new List<TrailRenderer>(16);

    // >0 while execution is inside one of the patched player-cast Gun methods.
    // Gun is the player's own weapon controller (scene singleton), so this
    // gate guarantees we only ever touch player skill / companion spawns and
    // never enemy effects or telegraphs, even though LeanPool.Spawn is shared.
    private static int _gunScopeDepth;

    private static bool _patched;
    private static bool _runtimeDisabled;
    private static bool _spawnErrorLogged;

    private Harmony _harmony;
    private GUI.WindowFunction _drawToast;
    private Rect _toastRect = new Rect(0f, 80f, 320f, 52f);
    private float _toastUntil = -1f;
    private string _toastText = "";

    private void Awake()
    {
        Log = base.Logger;

        ParticleBudgetPercent = Config.Bind("Clamping", "ParticleBudgetPercent", 40,
            new ConfigDescription(
                "Percentage (10-100) of each particle system's original maxParticles and emission rate kept in Reduced mode. " +
                "Minimal mode uses the same maxParticles budget but always drops the emission rate to 10% of the original. " +
                "Changing this while in-game re-applies to all currently alive clamped effects.",
                new AcceptableValueRange<int>(10, 100)));
        MinimalAlsoDisablesTrails = Config.Bind("Clamping", "MinimalAlsoDisablesTrails", true,
            "When true, Minimal mode also disables TrailRenderer components on player skill/companion objects. " +
            "Off and Reduced modes always restore trails to their original state.");
        CycleModeHotkey = Config.Bind("Hotkeys", "CycleModeHotkey", new KeyboardShortcut(KeyCode.F11),
            "Cycles the VFX clamp mode: Off -> Reduced -> Minimal -> Off. Shows a short on-screen toast with the new mode.");

        ParticleBudgetPercent.SettingChanged += OnClampSettingChanged;
        MinimalAlsoDisablesTrails.SettingChanged += OnClampSettingChanged;

        _drawToast = DrawToast;
        _harmony = new Harmony(PluginGuid);

        try
        {
            ApplyPatches();
        }
        catch (Exception ex)
        {
            _patched = false;
            Log.LogError("VFX Reducer failed to install its patches and is disabled: " + ex);
        }

        if (_patched)
        {
            Log.LogInfo("VFX Reducer loaded. Press " + CycleModeHotkey.Value + " to cycle Off / Reduced / Minimal.");
        }
    }

    private void ApplyPatches()
    {
        // The generic LeanPool.Spawn<T> overloads all funnel into this
        // GameObject overload, and every Gun.cs spawn site calls it directly
        // (SKPB.SK_FX / CP_OBJ / CP_FX and GetSkillPrefab are GameObject[]),
        // so one postfix covers every player-path pooled spawn.
        MethodInfo leanPoolSpawn = AccessTools.Method(typeof(LeanPool), nameof(LeanPool.Spawn),
            new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(bool) });

        // Every Gun method that contains a LeanPool.Spawn call site
        // (verified against decompiled Gun.cs):
        //   MGCattack/SQSattack/ARCattack/DEADattack - per-class skill VFX (SKPB.SK_FX)
        //   CreatSP                                  - the skill object itself (GetSkillPrefab)
        //   CreatCP(skill, out data)                 - companion object (SKPB.CP_OBJ); also
        //                                              covers CreatCP() and SpawnCompanionInstant
        //   Summon(bool)                             - summon cast VFX (SKPB.CP_FX)
        //   ACTprefabFS                              - weapon base-attack proc object (ACT.ATprefab)
        // CastDirect is covered transitively: it only dispatches into
        // CastCurrentSampleByPlayerType (the four attack methods) and Summon.
        MethodInfo[] gunMethods =
        {
            AccessTools.Method(typeof(Gun), "MGCattack"),
            AccessTools.Method(typeof(Gun), "SQSattack"),
            AccessTools.Method(typeof(Gun), "ARCattack"),
            AccessTools.Method(typeof(Gun), "DEADattack"),
            AccessTools.Method(typeof(Gun), "CreatSP"),
            AccessTools.Method(typeof(Gun), "CreatCP",
                new[] { typeof(ACTListSkillBT), typeof(CompanionRuntimeData).MakeByRefType() }),
            AccessTools.Method(typeof(Gun), "Summon", new[] { typeof(bool) }),
            AccessTools.Method(typeof(Gun), "ACTprefabFS", new[] { typeof(SkillOBJ_DT_SP), typeof(Vector3) }),
        };

        if (leanPoolSpawn == null)
        {
            Log.LogWarning("VFX Reducer: LeanPool.Spawn(GameObject, Vector3, Quaternion, Transform, bool) not found - feature disabled.");
            return;
        }
        for (int i = 0; i < gunMethods.Length; i++)
        {
            if (gunMethods[i] == null)
            {
                Log.LogWarning("VFX Reducer: a Gun spawn method could not be resolved (index " + i + ") - feature disabled.");
                return;
            }
        }

        HarmonyMethod scopeEnter = new HarmonyMethod(typeof(Plugin), nameof(GunScopeEnter));
        HarmonyMethod scopeFinalizer = new HarmonyMethod(typeof(Plugin), nameof(GunScopeFinalizer));
        for (int i = 0; i < gunMethods.Length; i++)
        {
            _harmony.Patch(gunMethods[i], prefix: scopeEnter, finalizer: scopeFinalizer);
        }
        _harmony.Patch(leanPoolSpawn, postfix: new HarmonyMethod(typeof(Plugin), nameof(LeanPoolSpawnPostfix)));
        _patched = true;
    }

    private void OnDestroy()
    {
        if (ParticleBudgetPercent != null)
        {
            ParticleBudgetPercent.SettingChanged -= OnClampSettingChanged;
        }
        if (MinimalAlsoDisablesTrails != null)
        {
            MinimalAlsoDisablesTrails.SettingChanged -= OnClampSettingChanged;
        }
        _harmony?.UnpatchSelf();
        LiveMarkers.Clear();
    }

    private void Update()
    {
        // Not KeyboardShortcut.IsDown(): that rejects the press while any other key is held
        // (e.g. WASD movement), which makes a combat hotkey unusable in practice.
        if (HotkeyPressed(CycleModeHotkey.Value))
        {
            if (!_patched || _runtimeDisabled)
            {
                ShowToast("VFX Reducer is disabled (see BepInEx log)");
                return;
            }
            CycleMode();
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

    private void CycleMode()
    {
        try
        {
            Mode = Mode == VfxMode.Minimal ? VfxMode.Off : Mode + 1;
            ReapplyToLiveMarkers();
            switch (Mode)
            {
                case VfxMode.Off:
                    ShowToast("VFX: Off (full effects)");
                    break;
                case VfxMode.Reduced:
                    ShowToast("VFX: Reduced (" + ParticleBudgetPercent.Value + "% particle budget)");
                    break;
                default:
                    ShowToast(MinimalAlsoDisablesTrails.Value
                        ? "VFX: Minimal (10% emission, trails off)"
                        : "VFX: Minimal (10% emission)");
                    break;
            }
            Log.LogInfo("VFX Reducer mode: " + _toastText);
        }
        catch (Exception ex)
        {
            Log.LogError(ex);
        }
    }

    private void OnClampSettingChanged(object sender, EventArgs e)
    {
        try
        {
            if (Mode != VfxMode.Off)
            {
                ReapplyToLiveMarkers();
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex);
        }
    }

    // Sweeps every marker that is alive right now so a mode change takes
    // effect immediately, not just for newly spawned objects.
    private static void ReapplyToLiveMarkers()
    {
        int budget = ParticleBudgetPercent.Value;
        bool trailsOff = MinimalAlsoDisablesTrails.Value;
        foreach (VfxClampMarker marker in LiveMarkers)
        {
            if (marker && marker.Initialized)
            {
                marker.Apply(Mode, budget, trailsOff);
            }
        }
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastUntil = Time.unscaledTime + ToastSeconds;
        _toastRect.x = (Screen.width - _toastRect.width) * 0.5f;
        _toastRect.y = 80f;
    }

    private void OnGUI()
    {
        if (Time.unscaledTime >= _toastUntil)
        {
            return;
        }
        _toastRect = GUI.Window(ToastWindowId, _toastRect, _drawToast, PluginName);
        _toastRect.x = Mathf.Clamp(_toastRect.x, 0f, Mathf.Max(0f, Screen.width - _toastRect.width));
        _toastRect.y = Mathf.Clamp(_toastRect.y, 0f, Mathf.Max(0f, Screen.height - _toastRect.height));
    }

    private void DrawToast(int id)
    {
        // Pure toast: no controls, not draggable.
        GUI.Label(new Rect(10f, 24f, _toastRect.width - 20f, 24f), _toastText);
    }

    // ---- Harmony callbacks (all static, hot path: allocation-free after warmup) ----

    private static void GunScopeEnter()
    {
        _gunScopeDepth++;
    }

    // Finalizer (not Postfix) so the depth counter unwinds even if the game
    // method throws. Void finalizers do not swallow the exception.
    private static void GunScopeFinalizer()
    {
        if (_gunScopeDepth > 0)
        {
            _gunScopeDepth--;
        }
    }

    private static void LeanPoolSpawnPostfix(GameObject __result)
    {
        if (_runtimeDisabled || __result == null)
        {
            return;
        }
        try
        {
            VfxClampMarker marker = __result.GetComponent<VfxClampMarker>();
            if (marker == null)
            {
                // Marker CREATION is gated to player-cast Gun scope so enemy
                // effects and telegraphs are never touched.
                if (_gunScopeDepth <= 0 || Mode == VfxMode.Off)
                {
                    return;
                }
                __result.GetComponentsInChildren(true, PsBuffer);
                if (PsBuffer.Count == 0)
                {
                    return; // not a particle effect - ignore
                }
                __result.GetComponentsInChildren(true, TrailBuffer);
                marker = __result.AddComponent<VfxClampMarker>();
                marker.Capture(PsBuffer, TrailBuffer);
            }
            // A marker proves this clone belongs to a player-path pool, so
            // re-apply (or restore, when Off) on EVERY spawn - including
            // out-of-scope respawns from shared pools (level-state restore,
            // companion trigger casts). Otherwise a clone clamped in scope
            // would keep stale settings when the pool serves it elsewhere.
            marker.Apply(Mode, ParticleBudgetPercent.Value, MinimalAlsoDisablesTrails.Value);
        }
        catch (Exception ex)
        {
            _runtimeDisabled = true; // fail soft: never throw per spawn again
            if (!_spawnErrorLogged)
            {
                _spawnErrorLogged = true;
                Log.LogError("VFX Reducer disabled after spawn-postfix error: " + ex);
            }
            // Degrade to vanilla visuals instead of leaving stale clamps in force.
            try
            {
                Mode = VfxMode.Off;
                ReapplyToLiveMarkers();
            }
            catch
            {
                // best effort only
            }
        }
    }
}

// Attached once per pooled clone that contains at least one ParticleSystem.
// Stores the clone's original values so any mode (including Off) can be
// re-applied idempotently on every pooled respawn.
public sealed class VfxClampMarker : MonoBehaviour
{
    private ParticleSystem[] _systems;
    private int[] _origMaxParticles;
    private float[] _origRateMultipliers;
    private TrailRenderer[] _trails;
    private bool[] _origTrailEnabled;

    internal bool Initialized { get; private set; }

    // Called exactly once, right after AddComponent. The one-time array
    // allocations here are the warmup cost per pooled clone.
    internal void Capture(List<ParticleSystem> systems, List<TrailRenderer> trails)
    {
        int n = systems.Count;
        _systems = new ParticleSystem[n];
        _origMaxParticles = new int[n];
        _origRateMultipliers = new float[n];
        for (int i = 0; i < n; i++)
        {
            ParticleSystem ps = systems[i];
            _systems[i] = ps;
            _origMaxParticles[i] = ps.main.maxParticles;
            _origRateMultipliers[i] = ps.emission.rateOverTimeMultiplier;
        }

        int t = trails.Count;
        _trails = new TrailRenderer[t];
        _origTrailEnabled = new bool[t];
        for (int i = 0; i < t; i++)
        {
            _trails[i] = trails[i];
            _origTrailEnabled[i] = trails[i].enabled;
        }
        Initialized = true;
    }

    internal void Apply(Plugin.VfxMode mode, int budgetPercent, bool minimalDisablesTrails)
    {
        if (!Initialized)
        {
            return;
        }
        for (int i = 0; i < _systems.Length; i++)
        {
            ParticleSystem ps = _systems[i];
            if (!ps)
            {
                continue;
            }
            ParticleSystem.MainModule main = ps.main;
            ParticleSystem.EmissionModule emission = ps.emission;
            if (mode == Plugin.VfxMode.Off)
            {
                main.maxParticles = _origMaxParticles[i];
                emission.rateOverTimeMultiplier = _origRateMultipliers[i];
            }
            else
            {
                main.maxParticles = Mathf.Max(4, (int)((long)_origMaxParticles[i] * budgetPercent / 100));
                emission.rateOverTimeMultiplier = mode == Plugin.VfxMode.Minimal
                    ? _origRateMultipliers[i] * 0.1f
                    : _origRateMultipliers[i] * (budgetPercent / 100f);
            }
        }

        bool disableTrails = mode == Plugin.VfxMode.Minimal && minimalDisablesTrails;
        for (int i = 0; i < _trails.Length; i++)
        {
            TrailRenderer trail = _trails[i];
            if (!trail)
            {
                continue;
            }
            trail.enabled = !disableTrails && _origTrailEnabled[i];
        }
    }

    private void OnEnable()
    {
        Plugin.LiveMarkers.Add(this);
    }

    private void OnDisable()
    {
        Plugin.LiveMarkers.Remove(this);
    }
}

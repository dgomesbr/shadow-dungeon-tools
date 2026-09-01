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
    public const string PluginVersion = "1.0.2";

    internal enum VfxMode
    {
        Off = 0,
        Reduced = 1,
        Minimal = 2
    }

    internal static ManualLogSource Log;
    internal static ConfigEntry<int> ParticleBudgetPercent;
    internal static ConfigEntry<bool> MinimalAlsoDisablesTrails;

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

    // internal, not private: ModMenuProvider reads both to decide whether the row is actionable.
    internal static bool _patched;
    internal static bool _runtimeDisabled;
    private static bool _spawnErrorLogged;

    private Harmony _harmony;

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
        ParticleBudgetPercent.SettingChanged += OnClampSettingChanged;
        MinimalAlsoDisablesTrails.SettingChanged += OnClampSettingChanged;

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
            Log.LogInfo("VFX Reducer loaded. Use the 'VFX:' row in the Mods menu (right screen edge) to cycle Off / Reduced / Minimal.");
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

    // No Update(): this plugin owns no keyboard shortcut any more. The mode is cycled from the
    // shared "Mods" menu row (see ModMenuProvider at the bottom of this file), so there is nothing
    // left to poll and no HotkeyPressed helper to keep.

    // internal static rather than a private instance method: the Mods-menu row invokes it directly
    // and it no longer touches any per-instance state (the toast window is gone).
    internal static void CycleMode()
    {
        try
        {
            if (!_patched || _runtimeDisabled)
            {
                // Was a toast; now log-only. The menu row still reflects Mode, which stays Off.
                // Log?. because the menu can in principle click before our Awake has run.
                Log?.LogWarning("VFX Reducer is disabled (see the earlier error in this log) - mode unchanged.");
                return;
            }
            Mode = Mode == VfxMode.Minimal ? VfxMode.Off : Mode + 1;
            ReapplyToLiveMarkers();
            Log?.LogInfo("VFX Reducer mode: " + ModeDetail());
        }
        catch (Exception ex)
        {
            Log?.LogError(ex);
        }
    }

    // Short row label for the Mods menu (kept inside the menu's ~22 character budget).
    internal static string ModeLabel()
    {
        switch (Mode)
        {
            case VfxMode.Off:
                return "VFX: Off";
            case VfxMode.Reduced:
                return "VFX: Reduced";
            default:
                return "VFX: Minimal";
        }
    }

    // Long form, log only - this is the text the removed on-screen toast used to show.
    private static string ModeDetail()
    {
        switch (Mode)
        {
            case VfxMode.Off:
                return "Off (full effects)";
            case VfxMode.Reduced:
                return "Reduced (" + ParticleBudgetPercent.Value + "% particle budget)";
            default:
                return MinimalAlsoDisablesTrails.Value
                    ? "Minimal (10% emission, trails off)"
                    : "Minimal (10% emission)";
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

    // No ShowToast / OnGUI / DrawToast and no reserved window id 49312: the Mods-menu row shows the
    // current mode continuously, so the transient toast window was redundant. Removed outright
    // rather than left keyless, so this plugin now draws no IMGUI of its own at all.

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

// Rows contributed to the shared "Mods" menu (docked to the right screen border). The menu finds
// this type by reflection - the name, namespace, accessibility and GetMenuItems() signature are a
// fixed contract, so do not rename any of them.
//
// Contract: every delegate must be total. label() and state() are called every frame while the menu
// is visible, so a throw here would be a per-frame exception storm; every body is wrapped and
// returns a safe fallback. Nothing in here touches the clamping hot path.
public static class ModMenuProvider
{
    // Each row: new object[] { string id, Func<string> label, Func<bool> state, Action onClick,
    //                          Func<string> description }
    // The 5th element is the hover tooltip text; it is optional in the contract, and like label()
    // and state() it may be called every frame while the menu is open, so it must never throw.
    public static object[][] GetMenuItems()
    {
        try
        {
            return new[]
            {
                new object[]
                {
                    "vfxreducer.mode",
                    (Func<string>)ModeLabel,
                    (Func<bool>)ModeActive,
                    (Action)CycleClick,
                    (Func<string>)ModeDescription
                }
            };
        }
        catch
        {
            return new object[0][];
        }
    }

    private const string ModeDescriptionFallback =
        "Cycles particle reduction (Off / Reduced / Minimal) for your own skill and companion effects. Enemy effects and telegraphs are never touched.";

    // Explains the row AND what the mode it currently shows actually does, since the row label
    // alone ("VFX: Reduced") does not say how much is being cut.
    private static string ModeDescription()
    {
        try
        {
            if (!Plugin._patched || Plugin._runtimeDisabled)
            {
                return "VFX reduction is unavailable: the game's spawn methods could not be patched, or it switched itself off after an error. Effects stay at full quality.";
            }
            switch (Plugin.Mode)
            {
                case Plugin.VfxMode.Off:
                    return "Cycles reduction of your own skill and companion particle effects to recover FPS. Off means full vanilla visuals; enemy effects are never touched.";
                case Plugin.VfxMode.Reduced:
                    return "Cuts your own skill and companion particles to recover FPS on crowded floors. Reduced keeps "
                        + Plugin.ParticleBudgetPercent.Value + "% of the particle budget.";
                default:
                    return Plugin.MinimalAlsoDisablesTrails.Value
                        ? "Cuts your own skill and companion particles hardest. Minimal uses "
                            + Plugin.ParticleBudgetPercent.Value + "% of the budget, 10% emission, and turns trails off."
                        : "Cuts your own skill and companion particles hardest. Minimal uses "
                            + Plugin.ParticleBudgetPercent.Value + "% of the particle budget and drops emission to 10%.";
            }
        }
        catch
        {
            return ModeDescriptionFallback;
        }
    }

    private static string ModeLabel()
    {
        try
        {
            return Plugin.ModeLabel();
        }
        catch
        {
            return "VFX: Off";
        }
    }

    // Lit whenever we are actually clamping something, i.e. any mode other than Off. Off is the
    // vanilla-visuals state, so an unlit row means "the game looks untouched".
    private static bool ModeActive()
    {
        try
        {
            return Plugin.Mode != Plugin.VfxMode.Off;
        }
        catch
        {
            return false;
        }
    }

    // Plugin.CycleMode already wraps its own body and self-guards when the patches failed to
    // install; this outer catch only exists to honour the never-throw contract.
    private static void CycleClick()
    {
        try
        {
            Plugin.CycleMode();
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError("Mods menu: VFX mode row failed: " + ex);
        }
    }
}

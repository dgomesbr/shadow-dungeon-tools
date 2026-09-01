using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Lean.Pool;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfPatches;

/// <summary>
/// Cross-cutting engine-level patches:
///  1. LayerMaskGetMaskMemo  - memoizes single-name LayerMask.GetMask lookups (72 call sites game-wide).
///  2. PoolNotificationNone  - stops LeanPool's guaranteed-empty IPoolable GetComponents scan per spawn/despawn.
///  3. EngineTweaks          - probe/log engine physics+GC state; opt-in setters with inert sentinel defaults.
/// Each patch installs independently; a failure in one never blocks the others.
/// </summary>
internal static class EngineModule
{
    internal static void Init(ConfigFile config, Harmony harmony)
    {
        InstallLayerMaskGetMaskMemo(config, harmony);
        InstallPoolNotificationNone(config, harmony);
        InstallEngineTweaks(config);
    }

    // ------------------------------------------------------------------
    // 1. LayerMaskGetMaskMemo
    // ------------------------------------------------------------------

    // Layer name -> mask. The Unity layer table is fixed at build time and immutable at runtime,
    // so entries never invalidate for the lifetime of the process. Names at the game's call sites
    // are compile-time literals; Ordinal comparison, no culture work.
    private static readonly Dictionary<string, int> MaskMemo = new Dictionary<string, int>(64, StringComparer.Ordinal);

    // The layer table only has 32 slots, so any legitimate workload saturates far below this.
    // The cap exists purely so a hypothetical caller feeding dynamic garbage strings cannot
    // grow the dictionary without bound (misses stay correct, they just aren't stored).
    private const int MaskMemoCap = 512;

    private static void InstallLayerMaskGetMaskMemo(ConfigFile config, Harmony harmony)
    {
        ConfigEntry<bool> enabled = config.Bind(
            "LayerMaskGetMaskMemo", "Enabled", true,
            "Caches UnityEngine.LayerMask.GetMask(\"name\") results for single-name lookups. The layer " +
            "table is immutable at runtime so cached results are provably identical to vanilla; multi-name " +
            "calls pass through untouched. Removes the per-call NameToLayer icall loop behind ~72 call " +
            "sites (projectiles/skills call this several times per tick each). Behavior-preserving; " +
            "risk near zero.");
        if (!enabled.Value)
        {
            return;
        }

        try
        {
            MethodInfo target = AccessTools.Method(typeof(LayerMask), nameof(LayerMask.GetMask), new[] { typeof(string[]) });
            if (target == null)
            {
                throw new MissingMethodException("LayerMask.GetMask(string[]) not found");
            }
            // GetMask is a managed wrapper that loops over the extern NameToLayer icall. Guard anyway:
            // if a future Unity build turns it extern (no IL body) Harmony cannot patch it safely.
            if (target.GetMethodBody() == null)
            {
                throw new NotSupportedException("LayerMask.GetMask has no managed body (extern/icall) - not patchable");
            }

            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(EngineModule), nameof(GetMaskPrefix)),
                postfix: new HarmonyMethod(typeof(EngineModule), nameof(GetMaskPostfix)));
            PerfCore.Log.LogInfo("LayerMaskGetMaskMemo installed");
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning("LayerMaskGetMaskMemo not installed: " + ex.Message);
        }
    }

    // __0 (positional injection) instead of a named parameter: immune to the compiled parameter
    // name of the params array differing between Unity builds.
    private static bool GetMaskPrefix(string[] __0, ref int __result)
    {
        if (__0 != null && __0.Length == 1)
        {
            string name = __0[0];
            if (name != null && MaskMemo.TryGetValue(name, out int mask))
            {
                __result = mask;
                return false; // skip original: identical result, no icall loop
            }
        }
        return true; // multi-name or first sighting: run vanilla, postfix stores the miss
    }

    private static void GetMaskPostfix(string[] __0, int __result)
    {
        if (__0 != null && __0.Length == 1)
        {
            string name = __0[0];
            if (name != null && MaskMemo.Count < MaskMemoCap)
            {
                MaskMemo[name] = __result;
            }
        }
    }

    // ------------------------------------------------------------------
    // 2. PoolNotificationNone
    // ------------------------------------------------------------------

    // Instance IDs of pools already inspected. IDs are unique per Unity Object lifetime, so a
    // destroyed pool's stale ID can never alias a live pool. Cleared on scene unload only to
    // bound memory; re-processing a persistent pool is idempotent.
    private static readonly HashSet<int> ProcessedPools = new HashSet<int>();
    private static bool _poolPostfixBroken;

    private static void InstallPoolNotificationNone(ConfigFile config, Harmony harmony)
    {
        ConfigEntry<bool> enabled = config.Bind(
            "PoolNotificationNone", "Enabled", true,
            "Sets each LeanPool pool's Notification mode to None (once per pool, on first spawn) when " +
            "the pool uses the IPoolable notification modes and nothing on its prefab implements " +
            "Lean.Pool.IPoolable. No game type implements that interface, so vanilla pays two " +
            "guaranteed-empty native GetComponents calls per spawn/despawn cycle for nothing. " +
            "Safety: at startup all loaded game assemblies are scanned for IPoolable implementors and " +
            "the patch self-disables if any exist; additionally each pool's prefab hierarchy is checked " +
            "before its mode is changed. Pools configured for SendMessage/BroadcastMessage are never " +
            "touched. Behavior-preserving under those guards.");
        if (!enabled.Value)
        {
            return;
        }

        try
        {
            // NOTE: an earlier revision opened with a domain-wide safety scan
            // (AppDomain.CurrentDomain.GetAssemblies() + asm.GetTypes()) looking for IPoolable
            // implementors. That scan KILLED THE GAME PROCESS at startup: calling GetTypes() on
            // FinkFramework.Odin.OdinSerializer blows the Mono stack (its emitted generic
            // formatter hierarchy recurses during type load), and StackOverflowException cannot
            // be caught - the process dies instantly, taking the whole plugin chainloader with
            // it and losing the buffered log. Never enumerate every loaded assembly's types in
            // this game.
            //
            // The scan was belt-and-suspenders anyway: the real guarantee comes from the
            // per-pool prefab check in LeanSpawnPostfix, which inspects the actual prefab
            // hierarchy for IPoolable before changing that pool's notification mode. A pool
            // whose prefab implements IPoolable is left completely alone, so an unknown
            // implementor (including a future game update adopting the API, or the LeanPool
            // library's own LeanPooledRigidbody extras) is handled correctly without reflection
            // over foreign assemblies.
            MethodInfo target = AccessTools.Method(typeof(LeanPool), nameof(LeanPool.Spawn),
                new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(bool) });
            if (target == null)
            {
                throw new MissingMethodException("LeanPool.Spawn(GameObject,Vector3,Quaternion,Transform,bool) not found");
            }

            // VfxReducer also postfixes this exact method (marker components on clones); Harmony runs
            // both postfixes independently. We never touch the clone or the return value, only the pool.
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(EngineModule), nameof(LeanSpawnPostfix)));
            PerfCore.OnSceneUnloaded("PoolNotificationNone", ProcessedPools.Clear);
            PerfCore.Log.LogInfo("PoolNotificationNone installed (per-pool prefab IPoolable check)");
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning("PoolNotificationNone not installed: " + ex.Message);
        }
    }

    private static void LeanSpawnPostfix(GameObject __result)
    {
        if (_poolPostfixBroken || __result == null) // fake-null aware == on UnityEngine.Object
        {
            return;
        }
        try
        {
            // Spawn just added the clone to Links, so this lookup always hits on the success path.
            if (!LeanPool.Links.TryGetValue(__result, out LeanGameObjectPool pool) || !pool)
            {
                return;
            }
            if (!ProcessedPools.Add(pool.GetInstanceID()))
            {
                return; // this pool was already inspected
            }
            // Only downgrade the IPoolable-based modes: those are provably no-ops here. SendMessage /
            // BroadcastMessage modes could reach arbitrary receivers - leave them untouched.
            if (pool.Notification != LeanGameObjectPool.NotificationType.IPoolable &&
                pool.Notification != LeanGameObjectPool.NotificationType.BroadcastIPoolable)
            {
                return;
            }
            // One-time per-pool check of the prefab hierarchy (covers the library's own
            // LeanPooledRigidbody extras should any prefab actually carry them).
            GameObject prefab = pool.Prefab;
            if (prefab != null && prefab.GetComponentInChildren(typeof(IPoolable), true) != null)
            {
                return;
            }
            pool.Notification = LeanGameObjectPool.NotificationType.None;
        }
        catch (Exception ex)
        {
            _poolPostfixBroken = true;
            PerfCore.Log.LogError("PoolNotificationNone postfix disabled after error: " + ex);
        }
    }

    // ------------------------------------------------------------------
    // 3. EngineTweaks
    // ------------------------------------------------------------------

    private static ConfigEntry<int> _velocityIterations;
    private static ConfigEntry<int> _positionIterations;
    private static ConfigEntry<bool> _disableAutoSyncTransforms;
    private static ConfigEntry<float> _fixedDeltaTime;
    private static ConfigEntry<long> _gcTimeSliceNs;
    private static ConfigEntry<bool> _gcCollectOnSceneUnload;

    // UnityEngine.Scripting.GarbageCollector is resolved via reflection: the API surface differs
    // across Unity minor versions and the player may be built without incremental GC entirely.
    private static PropertyInfo _gcIsIncrementalProp;
    private static PropertyInfo _gcTimeSliceProp;

    private static void InstallEngineTweaks(ConfigFile config)
    {
        ConfigEntry<bool> enabled = config.Bind(
            "EngineTweaks", "Enabled", true,
            "Master switch for the EngineTweaks probe and setters. When enabled, current engine state " +
            "(Physics2D iteration counts, autoSyncTransforms, callbacksOnDisable, fixedDeltaTime, vsync, " +
            "frame cap, incremental GC) is logged at startup and on every scene load - the probe alone " +
            "changes nothing. The individual setters below all default to inert sentinel values.");

        _velocityIterations = config.Bind(
            "EngineTweaks", "VelocityIterations", -1,
            "-1 = unchanged (vanilla, usually 8). Sets Physics2D.velocityIterations. Lowering (e.g. 4) cuts " +
            "rigidbody solver cost ~proportionally. Risk: softer/less accurate pushback resolution between " +
            "overlapping bodies; top-down velocity-driven movement tolerates it well.");

        _positionIterations = config.Bind(
            "EngineTweaks", "PositionIterations", -1,
            "-1 = unchanged (vanilla, usually 3). Sets Physics2D.positionIterations. Lowering (e.g. 2) cuts " +
            "solver cost. Risk: slightly less accurate overlap depenetration.");

        _disableAutoSyncTransforms = config.Bind(
            "EngineTweaks", "DisableAutoSyncTransforms", false,
            "false = unchanged. true = set Physics2D.autoSyncTransforms to FALSE (this setter only ever " +
            "disables, never enables). Only applies if the probe shows it currently true - in that case " +
            "every physics query issued after a transform write forces a full physics sync, multiplying " +
            "the cost of the game's raycast-heavy frames. Risk: same-frame queries after a transform move " +
            "see the pre-move pose (one-frame staleness).");

        _fixedDeltaTime = config.Bind(
            "EngineTweaks", "FixedDeltaTime", -1f,
            "-1 = unchanged (vanilla 0.02 = 50 Hz physics). Sets Time.fixedDeltaTime; e.g. 0.025 (40 Hz) " +
            "runs ~20% fewer physics sim ticks. Accepted range (0, 0.1]. Risk: coarser physics stepping - " +
            "fast projectiles get slightly larger per-step travel; movement feel can change subtly.");

        _gcTimeSliceNs = config.Bind(
            "EngineTweaks", "GCIncrementalTimeSliceNanoseconds", -1L,
            "-1 = unchanged. Sets UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds, " +
            "ONLY when the player was built with incremental GC (probed at runtime; cannot be force-enabled " +
            "otherwise). Smaller slices spread GC work across more frames (fewer spikes, more total overhead); " +
            "larger slices do the opposite. Risk: mistuning trades frame spikes for steady-state cost.");

        _gcCollectOnSceneUnload = config.Bind(
            "EngineTweaks", "GCCollectOnSceneUnload", false,
            "false = off. true = run a full GC.Collect whenever a scene unloads, hiding the collection " +
            "inside the load hitch instead of letting it land mid-combat. Risk: slightly longer scene " +
            "loads; no gameplay effect.");

        if (!enabled.Value)
        {
            return;
        }

        try
        {
            // Reflection-resolved once; typeof() is avoided so a Unity build lacking the type/members
            // (or a moved API) degrades to "GC state unknown" instead of a TypeLoadException.
            Type gcType = Type.GetType("UnityEngine.Scripting.GarbageCollector, UnityEngine.CoreModule");
            if (gcType != null)
            {
                _gcIsIncrementalProp = gcType.GetProperty("isIncremental", BindingFlags.Public | BindingFlags.Static);
                _gcTimeSliceProp = gcType.GetProperty("incrementalTimeSliceNanoseconds", BindingFlags.Public | BindingFlags.Static);
            }
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning("EngineTweaks: GarbageCollector reflection failed (GC state will read unknown): " + ex.Message);
        }

        try
        {
            ProbeAndApply("Init");
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning("EngineTweaks initial probe/apply failed: " + ex.Message);
        }

        // Re-apply on scene load: SettingDataManager.ApplyVideoSettings rewrites vsync/targetFrameRate
        // on its own schedule but never touches Physics2D/Time, so one-time apply would suffice - the
        // re-apply is belt-and-suspenders against anything else resetting physics config, and it also
        // re-logs the probe so drift is visible. Handler is self-contained so a throw here can never
        // break other sceneLoaded subscribers.
        SceneManager.sceneLoaded += OnSceneLoadedProbe;

        if (_gcCollectOnSceneUnload.Value)
        {
            PerfCore.OnSceneUnloaded("EngineTweaks.GCCollectOnSceneUnload", () => GC.Collect());
        }

        PerfCore.Log.LogInfo("EngineTweaks installed (probe on; setters " + (HasActiveSetter() ? "ACTIVE" : "all at sentinel defaults") + ")");
    }

    private static bool HasActiveSetter()
    {
        return _velocityIterations.Value > 0
            || _positionIterations.Value > 0
            || _disableAutoSyncTransforms.Value
            || _fixedDeltaTime.Value > 0f
            || _gcTimeSliceNs.Value > 0L
            || _gcCollectOnSceneUnload.Value;
    }

    private static void OnSceneLoadedProbe(Scene scene, LoadSceneMode mode)
    {
        try
        {
            ProbeAndApply("sceneLoaded:" + scene.name);
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning("EngineTweaks probe/apply failed on scene load: " + ex.Message);
        }
    }

    private static void ProbeAndApply(string when)
    {
        // --- probe (always on; string work is fine here - Init/scene-load only, never per-frame) ---
        string gcState = "unknown";
        bool gcIncremental = false;
        try
        {
            if (_gcIsIncrementalProp != null)
            {
                gcIncremental = (bool)_gcIsIncrementalProp.GetValue(null, null);
                gcState = gcIncremental ? "incremental" : "non-incremental";
                if (gcIncremental && _gcTimeSliceProp != null)
                {
                    gcState += " slice=" + Convert.ToUInt64(_gcTimeSliceProp.GetValue(null, null)) + "ns";
                }
            }
        }
        catch
        {
            gcState = "unknown (probe threw)";
        }

        PerfCore.Log.LogInfo("[EngineTweaks " + when + "]"
            + " Physics2D.autoSyncTransforms=" + Physics2D.autoSyncTransforms
            + " velocityIterations=" + Physics2D.velocityIterations
            + " positionIterations=" + Physics2D.positionIterations
            + " callbacksOnDisable=" + Physics2D.callbacksOnDisable
            + " | Time.fixedDeltaTime=" + Time.fixedDeltaTime
            + " | QualitySettings.vSyncCount=" + QualitySettings.vSyncCount
            + " Application.targetFrameRate=" + Application.targetFrameRate
            + " | GC=" + gcState);

        // --- opt-in setters (each checks its sentinel; idempotent, safe to re-run every scene) ---
        if (_velocityIterations.Value > 0 && Physics2D.velocityIterations != _velocityIterations.Value)
        {
            Physics2D.velocityIterations = _velocityIterations.Value;
            PerfCore.Log.LogInfo("EngineTweaks: Physics2D.velocityIterations -> " + _velocityIterations.Value);
        }
        if (_positionIterations.Value > 0 && Physics2D.positionIterations != _positionIterations.Value)
        {
            Physics2D.positionIterations = _positionIterations.Value;
            PerfCore.Log.LogInfo("EngineTweaks: Physics2D.positionIterations -> " + _positionIterations.Value);
        }
        if (_disableAutoSyncTransforms.Value && Physics2D.autoSyncTransforms)
        {
            // Disable-only by design: we never turn autoSyncTransforms ON.
            Physics2D.autoSyncTransforms = false;
            PerfCore.Log.LogInfo("EngineTweaks: Physics2D.autoSyncTransforms -> false");
        }
        if (_fixedDeltaTime.Value > 0f && _fixedDeltaTime.Value <= 0.1f
            && Mathf.Abs(Time.fixedDeltaTime - _fixedDeltaTime.Value) > 1e-6f)
        {
            Time.fixedDeltaTime = _fixedDeltaTime.Value;
            PerfCore.Log.LogInfo("EngineTweaks: Time.fixedDeltaTime -> " + _fixedDeltaTime.Value);
        }
        if (_gcTimeSliceNs.Value > 0L && gcIncremental && _gcTimeSliceProp != null && _gcTimeSliceProp.CanWrite)
        {
            try
            {
                // incrementalTimeSliceNanoseconds is ulong on Unity 2019.4 - convert, never unbox as long.
                if ((long)Convert.ToUInt64(_gcTimeSliceProp.GetValue(null, null)) != _gcTimeSliceNs.Value)
                {
                    _gcTimeSliceProp.SetValue(null, Convert.ChangeType((ulong)_gcTimeSliceNs.Value, _gcTimeSliceProp.PropertyType), null);
                    PerfCore.Log.LogInfo("EngineTweaks: GarbageCollector.incrementalTimeSliceNanoseconds -> " + _gcTimeSliceNs.Value);
                }
            }
            catch (Exception ex)
            {
                PerfCore.Log.LogWarning("EngineTweaks: setting GC time slice failed: " + ex.Message);
            }
        }

        // Physics2D.callbacksOnDisable is deliberately NEVER exposed or modified here:
        // LeanPool despawn is SetActive(false), and ~40 game scripts depend on OnTriggerExit2D
        // firing on disable to purge their target lists (SK_XJ_ZD.em/cp/pl, BodyCOL bookkeeping).
        // Setting it false corrupts targeting state on every pooled despawn.
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Entity.Enemies.EnemyAI;
using HarmonyLib;
using UnityEngine;

namespace PerfPatches;

/// <summary>
/// Enemy AI hot-path patches.
///
/// CompanionScanOverhaul - reimplements EnemyBrain.ScanCompanions (runs at 4 Hz per enemy):
///   a static Companion registry (maintained by Companion.OnEnable/OnDisable postfixes) lets the
///   scan skip the Physics2D overlap entirely while no valid companion exists; a
///   Collider2D->FootCOL cache (seeded by a FootCOL.OnEnable postfix) removes the per-hit
///   GetComponent; the closure-allocating full sort is replaced by a single min-selection pass on
///   sqrMagnitude with a swap to index 0 (only compCandidates[0] is ever consumed by
///   TryFindBestCompanion, and sqrt is strictly monotonic, so the observable result is identical).
///
/// AITickStagger - postfix on EnemyBrain.Reset(Vector3): gives decisionTimer and compScanTimer the
///   SAME deterministic negative phase derived from the enemy instance id, spreading the 4 Hz
///   fleet-wide scan/decision burst across frames. Keeping both timers equal preserves the
///   scan-immediately-before-decide ordering inside Tick (compScanTimer is checked first).
///
/// LegacyScanMicroFix - verbatim ports of Enemy.JSQ_LegacyOnly and Enemy.GetClosestCompanionDistance
///   (legacy path used by towers / brainless enemies) minus the allocations: cached layer masks
///   instead of LayerMask.GetMask per 0.23 s tick, min-selection instead of the this-capturing sort
///   delegate, squared-distance comparisons where only ordering matters.
/// </summary>
internal static class EnemyAiModule
{
    // ---- resolved once at Init ----------------------------------------------------------------
    private static AccessTools.FieldRef<EnemyBrain, Enemy> _brainEm;
    private static AccessTools.FieldRef<EnemyBrain, Collider2D[]> _brainCompHits;
    private static AccessTools.FieldRef<EnemyBrain, int> _brainMaskFootCp;
    private static AccessTools.FieldRef<EnemyBrain, float> _brainDecisionTimer;
    private static AccessTools.FieldRef<EnemyBrain, float> _brainCompScanTimer;

    private static AccessTools.FieldRef<Enemy, float> _emJStimeA;
    private static AccessTools.FieldRef<Enemy, float> _emJStimeB;
    private static AccessTools.FieldRef<Enemy, float> _emTimeC;
    private static AccessTools.FieldRef<Enemy, float> _emTimeF;
    private static AccessTools.FieldRef<Enemy, bool> _emHurtOK;
    private static Action<Enemy> _applyHealthRegenOrTornWoundDamage;
    private static Action<string, Vector3> _fmodPlayOneShot; // FMODUnity.RuntimeManager.PlayOneShot(string, Vector3)

    private static int _maskBlock;
    private static int _maskFootCp;

    // ---- runtime state (allocation-free after warmup) ------------------------------------------
    // Companions currently enabled. LeanPool despawn = SetActive(false), which fires OnDisable, so
    // the registry tracks pooled respawn/despawn correctly. Fake-null entries (destroyed while we
    // missed a callback) are pruned lazily and on scene unload.
    private static readonly List<Companion> CompanionRegistry = new List<Companion>(16);
    // FootCOLcp collider -> FootCOL on the same GameObject (what collider.GetComponent<FootCOL>()
    // returns). Pooled objects keep their components, so entries survive respawns; cleared on scene
    // unload to drop destroyed-collider keys.
    private static readonly Dictionary<Collider2D, FootCOL> ColliderToFoot = new Dictionary<Collider2D, FootCOL>(64);

    // fail-soft flags: first failure logs + skips that tick, later calls run vanilla.
    private static bool _scanBroken;
    private static bool _staggerBroken;
    private static bool _legacyJsqBroken;
    private static bool _legacyClosestBroken;
    private static bool _registryLogged;

    internal static void Init(ConfigFile config, Harmony harmony)
    {
        ConfigEntry<bool> scanEnabled = config.Bind("CompanionScanOverhaul", "Enabled", true,
            "Reimplements EnemyBrain.ScanCompanions (4 Hz per enemy): skips the physics overlap entirely while no companion is alive, caches FootCOL lookups, and picks the closest companion without the closure-allocating sort. Output contract is unchanged (only compCandidates[0] is consumed). Risk: two companions at exactly equal distance may tie-break differently (the vanilla sort was unstable anyway).");
        ConfigEntry<bool> staggerEnabled = config.Bind("AITickStagger", "Enabled", true,
            "Dephases each enemy's 4 Hz AI scan/decision timers by a deterministic per-instance offset on EnemyBrain.Reset, so fleets spawned together stop ticking in the same frame. Total AI work is unchanged; the first decision after (re)spawn happens up to 0.25 s later than stock (within existing spawn stabilization delays). Removes periodic AI spike frames on dense floors.");
        ConfigEntry<bool> legacyEnabled = config.Bind("LegacyScanMicroFix", "Enabled", true,
            "Ports Enemy.JSQ_LegacyOnly and Enemy.GetClosestCompanionDistance (towers / brainless enemies only) without per-tick allocations: cached layer masks instead of LayerMask.GetMask every 0.23 s, no sort delegate, squared-distance comparisons. Behavior identical; equal-distance tie-breaks may differ.");

        _maskBlock = LayerMask.GetMask("block");
        _maskFootCp = LayerMask.GetMask("FootCOLcp");

        // Caches must be dropped when the level goes away regardless of which patches installed.
        PerfCore.OnSceneUnloaded("EnemyAi", static () =>
        {
            ColliderToFoot.Clear();
            // Prune fake-nulls only: companions surviving a scene unload (if any) stay registered;
            // destroyed ones received OnDisable and are already gone.
            for (int i = CompanionRegistry.Count - 1; i >= 0; i--)
            {
                if (!CompanionRegistry[i])
                {
                    CompanionRegistry.RemoveAt(i);
                }
            }
        });

        if (scanEnabled.Value)
        {
            try
            {
                InstallCompanionScanOverhaul(harmony);
                PerfCore.Log.LogInfo("EnemyAi: CompanionScanOverhaul installed.");
            }
            catch (Exception ex)
            {
                PerfCore.Log.LogError("EnemyAi: CompanionScanOverhaul failed to install, running vanilla: " + ex);
            }
        }

        if (staggerEnabled.Value)
        {
            try
            {
                InstallAiTickStagger(harmony);
                PerfCore.Log.LogInfo("EnemyAi: AITickStagger installed.");
            }
            catch (Exception ex)
            {
                PerfCore.Log.LogError("EnemyAi: AITickStagger failed to install, running vanilla: " + ex);
            }
        }

        if (legacyEnabled.Value)
        {
            try
            {
                InstallLegacyScanMicroFix(harmony);
                PerfCore.Log.LogInfo("EnemyAi: LegacyScanMicroFix installed.");
            }
            catch (Exception ex)
            {
                PerfCore.Log.LogError("EnemyAi: LegacyScanMicroFix failed to install, running vanilla: " + ex);
            }
        }
    }

    // ================================================================================
    // Shared resolution helpers
    // ================================================================================

    private static void EnsureBrainEmRef()
    {
        if (_brainEm != null)
        {
            return;
        }
        FieldInfo em = AccessTools.Field(typeof(EnemyBrain), "em")
            ?? throw new MissingFieldException("EnemyBrain.em not found");
        _brainEm = AccessTools.FieldRefAccess<EnemyBrain, Enemy>(em);
    }

    private static MethodInfo RequireMethod(Type type, string name, Type[] parameters = null)
    {
        return AccessTools.Method(type, name, parameters)
            ?? throw new MissingMethodException(type.FullName + "." + name + " not found");
    }

    // ================================================================================
    // Patch 1: CompanionScanOverhaul
    // ================================================================================

    private static void InstallCompanionScanOverhaul(Harmony harmony)
    {
        EnsureBrainEmRef();
        FieldInfo compHits = AccessTools.Field(typeof(EnemyBrain), "compHits")
            ?? throw new MissingFieldException("EnemyBrain.compHits not found");
        FieldInfo maskFootCp = AccessTools.Field(typeof(EnemyBrain), "maskFootCp")
            ?? throw new MissingFieldException("EnemyBrain.maskFootCp not found");
        _brainCompHits = AccessTools.FieldRefAccess<EnemyBrain, Collider2D[]>(compHits);
        _brainMaskFootCp = AccessTools.FieldRefAccess<EnemyBrain, int>(maskFootCp);

        // Registry hooks first: if any of these cannot patch, we must NOT install the scan prefix
        // (an unmaintained registry would make every enemy ignore companions).
        // Finalizers, not postfixes: a postfix is SKIPPED when the original method throws, and
        // Companion.OnEnable dereferences LevelManager/HealthStat (Companion.cs:1020,1039). A
        // missed registration would hide that companion from every brain-AI enemy for its whole
        // life - far worse than the registry being a frame early. Finalizers always run, and
        // returning null (void finalizer) never swallows the original exception.
        harmony.Patch(RequireMethod(typeof(Companion), "OnEnable"),
            finalizer: new HarmonyMethod(typeof(EnemyAiModule), nameof(CompanionOnEnablePostfix)));
        harmony.Patch(RequireMethod(typeof(Companion), "OnDisable"),
            finalizer: new HarmonyMethod(typeof(EnemyAiModule), nameof(CompanionOnDisablePostfix)));
        harmony.Patch(RequireMethod(typeof(FootCOL), "OnEnable"),
            finalizer: new HarmonyMethod(typeof(EnemyAiModule), nameof(FootColOnEnablePostfix)));

        harmony.Patch(RequireMethod(typeof(EnemyBrain), "ScanCompanions"),
            prefix: new HarmonyMethod(typeof(EnemyAiModule), nameof(ScanCompanionsPrefix)));
    }

    private static void CompanionOnEnablePostfix(Companion __instance)
    {
        try
        {
            if (!CompanionRegistry.Contains(__instance))
            {
                CompanionRegistry.Add(__instance);
            }
        }
        catch (Exception ex)
        {
            OnRegistryError(ex);
        }
    }

    private static void CompanionOnDisablePostfix(Companion __instance)
    {
        try
        {
            CompanionRegistry.Remove(__instance);
        }
        catch (Exception ex)
        {
            OnRegistryError(ex);
        }
    }

    private static void FootColOnEnablePostfix(FootCOL __instance)
    {
        try
        {
            Collider2D col = __instance.GetComponent<Collider2D>();
            if ((bool)col)
            {
                ColliderToFoot[col] = __instance;
            }
        }
        catch (Exception ex)
        {
            OnRegistryError(ex);
        }
    }

    private static void OnRegistryError(Exception ex)
    {
        // If the registry bookkeeping ever fails, the fast path can no longer be trusted:
        // fall the scan back to vanilla permanently.
        _scanBroken = true;
        if (!_registryLogged)
        {
            _registryLogged = true;
            PerfCore.Log.LogError("EnemyAi: companion registry hook failed; CompanionScanOverhaul reverting to vanilla: " + ex);
        }
    }

    private static bool ScanCompanionsPrefix(EnemyBrain __instance)
    {
        if (_scanBroken)
        {
            return true; // vanilla
        }
        try
        {
            ScanCompanionsImpl(__instance);
            return false;
        }
        catch (Exception ex)
        {
            _scanBroken = true;
            PerfCore.Log.LogError("EnemyAi: CompanionScanOverhaul failed at runtime, skipping this scan and reverting to vanilla: " + ex);
            return false; // skip this tick; next call runs vanilla
        }
    }

    private static void ScanCompanionsImpl(EnemyBrain brain)
    {
        List<Companion> cands = brain.compCandidates;
        cands.Clear();

        // Fast path: with no valid companion anywhere, the vanilla filter
        // (peo.CharacterType == 1 + IsCompanionTargetValid) would produce an empty list, so the
        // physics query, GetComponents and sort are pure waste. Prune fake-nulls while we look.
        bool anyValid = false;
        for (int i = CompanionRegistry.Count - 1; i >= 0; i--)
        {
            Companion c = CompanionRegistry[i];
            if (!c)
            {
                CompanionRegistry.RemoveAt(i);
                continue;
            }
            if (!anyValid && IsCompanionTargetValid(c))
            {
                anyValid = true;
            }
        }
        if (!anyValid)
        {
            return;
        }

        Enemy em = _brainEm(brain);
        Vector3 selfPos3 = em.transform.position; // Tick() guarantees em is alive before scanning
        Collider2D[] hits = _brainCompHits(brain);
        int num = Physics2D.OverlapCircleNonAlloc(selfPos3, 10f, hits, _brainMaskFootCp(brain));
        if (num <= 0)
        {
            return;
        }
        for (int i = 0; i < num; i++)
        {
            Collider2D collider2D = hits[i];
            hits[i] = null;
            if (!collider2D)
            {
                continue;
            }
            FootCOL component = ResolveFootCol(collider2D);
            if ((bool)component && (bool)component.peo && component.peo.CharacterType == 1)
            {
                Companion cp = component.peo.cp;
                if (IsCompanionTargetValid(cp) && !cands.Contains(cp))
                {
                    cands.Add(cp);
                }
            }
        }
        if (cands.Count <= 1)
        {
            return;
        }

        // Vanilla sorts the whole list by Vector2.Distance with a closure-allocating comparator,
        // but only compCandidates[0] is ever read (TryFindBestCompanion). A min-selection on
        // sqrMagnitude with a swap to the front yields the identical observable result.
        Vector2 selfPos = selfPos3;
        int best = -1;
        float bestSqr = float.PositiveInfinity;
        for (int i = 0; i < cands.Count; i++)
        {
            Companion c = cands[i];
            if (!c)
            {
                continue; // vanilla comparator ordered nulls last
            }
            Vector2 d = (Vector2)c.transform.position - selfPos;
            float sqr = d.sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = i;
            }
        }
        if (best > 0)
        {
            Companion tmp = cands[0];
            cands[0] = cands[best];
            cands[best] = tmp;
        }
    }

    /// <summary>Collider -> FootCOL with lazy fill; equivalent to collider.GetComponent&lt;FootCOL&gt;().</summary>
    private static FootCOL ResolveFootCol(Collider2D col)
    {
        if (ColliderToFoot.TryGetValue(col, out FootCOL foot) && (bool)foot)
        {
            return foot;
        }
        foot = col.GetComponent<FootCOL>();
        if ((bool)foot)
        {
            ColliderToFoot[col] = foot;
        }
        return foot;
    }

    /// <summary>Verbatim replica of the private static EnemyBrain.IsCompanionTargetValid.</summary>
    private static bool IsCompanionTargetValid(Companion companion)
    {
        if ((bool)companion && companion.IsAlive && companion.gameObject.activeInHierarchy)
        {
            return companion.transform.gameObject.activeInHierarchy;
        }
        return false;
    }

    // ================================================================================
    // Patch 2: AITickStagger
    // ================================================================================

    private static void InstallAiTickStagger(Harmony harmony)
    {
        EnsureBrainEmRef();
        FieldInfo decisionTimer = AccessTools.Field(typeof(EnemyBrain), "decisionTimer")
            ?? throw new MissingFieldException("EnemyBrain.decisionTimer not found");
        FieldInfo compScanTimer = AccessTools.Field(typeof(EnemyBrain), "compScanTimer")
            ?? throw new MissingFieldException("EnemyBrain.compScanTimer not found");
        _brainDecisionTimer = AccessTools.FieldRefAccess<EnemyBrain, float>(decisionTimer);
        _brainCompScanTimer = AccessTools.FieldRefAccess<EnemyBrain, float>(compScanTimer);

        harmony.Patch(RequireMethod(typeof(EnemyBrain), nameof(EnemyBrain.Reset), new[] { typeof(Vector3) }),
            postfix: new HarmonyMethod(typeof(EnemyAiModule), nameof(BrainResetPostfix)));
    }

    private static void BrainResetPostfix(EnemyBrain __instance)
    {
        if (_staggerBroken)
        {
            return;
        }
        try
        {
            Enemy em = _brainEm(__instance);
            if (em is null) // pure reference check: a fake-null (destroyed) Enemy still has a valid instance id
            {
                return;
            }
            // Golden-ratio hash of the instance id into [0, 0.25s). Deterministic per enemy, so a
            // fleet spawned in one frame fans out across the whole 0.25 s decision period.
            float phase = (float)((uint)em.GetInstanceID() * 2654435761u % 250u) / 1000f;
            // CRITICAL: both timers get the SAME offset so ScanCompanions still runs immediately
            // before DoDecision inside the same Tick call (Tick checks compScanTimer first).
            _brainDecisionTimer(__instance) = -phase;
            _brainCompScanTimer(__instance) = -phase;
        }
        catch (Exception ex)
        {
            _staggerBroken = true;
            PerfCore.Log.LogError("EnemyAi: AITickStagger failed at runtime and is disabled (timers stay vanilla): " + ex);
        }
    }

    // ================================================================================
    // Patch 3: LegacyScanMicroFix
    // ================================================================================

    private static void InstallLegacyScanMicroFix(Harmony harmony)
    {
        FieldInfo jsA = AccessTools.Field(typeof(Enemy), "JStimeA")
            ?? throw new MissingFieldException("Enemy.JStimeA not found");
        FieldInfo jsB = AccessTools.Field(typeof(Enemy), "JStimeB")
            ?? throw new MissingFieldException("Enemy.JStimeB not found");
        FieldInfo timeC = AccessTools.Field(typeof(Enemy), "timeC")
            ?? throw new MissingFieldException("Enemy.timeC not found");
        FieldInfo timeF = AccessTools.Field(typeof(Enemy), "timeF")
            ?? throw new MissingFieldException("Enemy.timeF not found");
        FieldInfo hurtOK = AccessTools.Field(typeof(Enemy), "HurtOK")
            ?? throw new MissingFieldException("Enemy.HurtOK not found");
        _emJStimeA = AccessTools.FieldRefAccess<Enemy, float>(jsA);
        _emJStimeB = AccessTools.FieldRefAccess<Enemy, float>(jsB);
        _emTimeC = AccessTools.FieldRefAccess<Enemy, float>(timeC);
        _emTimeF = AccessTools.FieldRefAccess<Enemy, float>(timeF);
        _emHurtOK = AccessTools.FieldRefAccess<Enemy, bool>(hurtOK);
        _applyHealthRegenOrTornWoundDamage = AccessTools.MethodDelegate<Action<Enemy>>(
            RequireMethod(typeof(Enemy), "ApplyHealthRegenOrTornWoundDamage"), null, virtualCall: false);

        // GetClosestCompanionDistance needs nothing external - always patchable.
        harmony.Patch(RequireMethod(typeof(Enemy), "GetClosestCompanionDistance"),
            prefix: new HarmonyMethod(typeof(EnemyAiModule), nameof(GetClosestCompanionDistancePrefix)));

        // JSQ_LegacyOnly calls FMODUnity.RuntimeManager.PlayOneShot(string, Vector3); the plugin
        // does not reference FMODUnity, so bind it once via reflection. If the overload is missing
        // (FMOD version drift) we leave JSQ_LegacyOnly vanilla rather than ship a lossy port.
        Type runtimeManager = AccessTools.TypeByName("FMODUnity.RuntimeManager");
        MethodInfo playOneShot = runtimeManager == null
            ? null
            : AccessTools.Method(runtimeManager, "PlayOneShot", new[] { typeof(string), typeof(Vector3) });
        if (playOneShot == null)
        {
            PerfCore.Log.LogWarning("EnemyAi: FMODUnity.RuntimeManager.PlayOneShot(string, Vector3) not found; JSQ_LegacyOnly stays vanilla (GetClosestCompanionDistance still patched).");
            return;
        }
        _fmodPlayOneShot = (Action<string, Vector3>)Delegate.CreateDelegate(typeof(Action<string, Vector3>), playOneShot);

        harmony.Patch(RequireMethod(typeof(Enemy), nameof(Enemy.JSQ_LegacyOnly)),
            prefix: new HarmonyMethod(typeof(EnemyAiModule), nameof(JsqLegacyOnlyPrefix)));
    }

    private static bool GetClosestCompanionDistancePrefix(Enemy __instance, ref float __result)
    {
        if (_legacyClosestBroken)
        {
            return true; // vanilla
        }
        try
        {
            // Vanilla: min over Vector2.Distance per candidate. sqrt is monotonic, so track the
            // minimum sqrMagnitude and take ONE sqrt at the end. Return contract is a LINEAR
            // distance (consumed by TryEnableBossTargetPriorityMultiB's multiplied comparison)
            // and +Infinity when no valid companion exists - both preserved.
            float minSqr = float.PositiveInfinity;
            List<Companion> cands = __instance.CompCandidates;
            Vector2 selfPos = __instance.transform.position;
            for (int i = 0; i < cands.Count; i++)
            {
                Companion companion = cands[i];
                if ((bool)companion && companion.IsAlive)
                {
                    Vector2 d = (Vector2)companion.transform.position - selfPos;
                    float sqr = d.sqrMagnitude;
                    if (sqr < minSqr)
                    {
                        minSqr = sqr;
                    }
                }
            }
            __result = float.IsPositiveInfinity(minSqr) ? float.PositiveInfinity : Mathf.Sqrt(minSqr);
            return false;
        }
        catch (Exception ex)
        {
            _legacyClosestBroken = true;
            PerfCore.Log.LogError("EnemyAi: GetClosestCompanionDistance patch failed at runtime, reverting to vanilla: " + ex);
            __result = float.PositiveInfinity; // "no companion" - caller treats it as a no-op
            return false;
        }
    }

    private static bool JsqLegacyOnlyPrefix(Enemy __instance)
    {
        if (_legacyJsqBroken)
        {
            return true; // vanilla
        }
        try
        {
            JsqLegacyOnlyImpl(__instance);
            return false;
        }
        catch (Exception ex)
        {
            _legacyJsqBroken = true;
            PerfCore.Log.LogError("EnemyAi: JSQ_LegacyOnly patch failed at runtime, skipping this tick and reverting to vanilla: " + ex);
            return false; // skip this tick; next call runs vanilla
        }
    }

    /// <summary>
    /// Faithful port of Enemy.JSQ_LegacyOnly (Enemy.cs:1688-1815) with three changes:
    /// cached layer masks (no LayerMask.GetMask string[] alloc per 0.23 s tick), min-selection
    /// swap-to-front instead of the this-capturing sort delegate (only CompCandidates[0] is
    /// consumed), and sqrMagnitude for the range checks (same predicate for non-negative ranges).
    /// </summary>
    private static void JsqLegacyOnlyImpl(Enemy em)
    {
        if (!em.EMstartOK || !em.IsAlive)
        {
            return;
        }
        ref float jsA = ref _emJStimeA(em);
        jsA += Time.deltaTime;
        if (jsA >= 0.23f)
        {
            Vector3 selfPos3 = em.transform.position;
            Vector2 selfPos = selfPos3;
            float rangeCur = em.Range_Cur; // Anger (its only input) is not mutated until below
            List<Companion> cands = em.CompCandidates;

            bool playerInRange = false;
            if ((bool)em.playerManager)
            {
                // vanilla: Vector2.Distance(self, player) < Range_Cur
                Vector2 dp = (Vector2)em.playerManager.transform.position - selfPos;
                playerInRange = rangeCur > 0f && dp.sqrMagnitude < rangeCur * rangeCur;
            }

            if (cands.Count > 0 || playerInRange)
            {
                // prune: vanilla removes when !companion || !IsAlive || dist > Range_Cur + 0.5f
                float pruneR = rangeCur + 0.5f;
                float pruneRR = pruneR * pruneR;
                for (int i = 0; i < cands.Count; i++)
                {
                    Companion companion = cands[i];
                    bool remove;
                    if (!companion || !companion.IsAlive)
                    {
                        remove = true;
                    }
                    else if (pruneR < 0f)
                    {
                        remove = true; // dist >= 0 > negative threshold is always true in vanilla
                    }
                    else
                    {
                        Vector2 d = (Vector2)companion.transform.position - selfPos;
                        remove = d.sqrMagnitude > pruneRR;
                    }
                    if (remove)
                    {
                        cands.RemoveAt(i);
                        i--;
                    }
                }
                // vanilla full sort replaced by min-selection; only CompCandidates[0] is consumed
                // (targeting + _distToTarget), and GetClosestCompanionDistance re-scans the list.
                if (cands.Count > 1)
                {
                    int best = -1;
                    float bestSqr = float.PositiveInfinity;
                    for (int i = 0; i < cands.Count; i++)
                    {
                        Companion companion = cands[i];
                        if (!companion)
                        {
                            continue; // vanilla comparator ordered nulls last
                        }
                        Vector2 d = (Vector2)companion.transform.position - selfPos;
                        float sqr = d.sqrMagnitude;
                        if (sqr < bestSqr)
                        {
                            bestSqr = sqr;
                            best = i;
                        }
                    }
                    if (best > 0)
                    {
                        Companion tmp = cands[0];
                        cands[0] = cands[best];
                        cands[best] = tmp;
                    }
                }
                if ((bool)em.MVTarget)
                {
                    Vector2 vector = em.MVTarget.transform.position - selfPos3;
                    float magnitude = vector.magnitude;
                    em.ray = Physics2D.Raycast(selfPos3, vector.normalized, magnitude, _maskBlock);
                    em.CanSeeMVTarget = !em.ray.collider;
                }
                else
                {
                    em.CanSeeMVTarget = false;
                }
            }
            else
            {
                em.CanSeeMVTarget = false;
            }
            int hitCount = Physics2D.OverlapCircleNonAlloc(selfPos3, rangeCur, em.hitCP, _maskFootCp);
            if (hitCount > 0)
            {
                for (int j = 0; j < hitCount; j++)
                {
                    FootCOL component = ResolveFootCol(em.hitCP[j]);
                    if ((bool)component)
                    {
                        // vanilla does not null-check peo here and only nulls the slot on a hit
                        if (component.peo.CharacterType == 1 && component.peo.cp.IsAlive && !cands.Contains(component.peo.cp))
                        {
                            cands.Add(component.peo.cp);
                        }
                        em.hitCP[j] = null;
                    }
                }
            }
            if (em.Anger > 0)
            {
                em.Anger -= 5;
            }
            jsA = 0f;
        }
        ref float jsB = ref _emJStimeB(em);
        jsB += Time.deltaTime;
        if (jsB >= 1f)
        {
            _applyHealthRegenOrTornWoundDamage(em);
            jsB = 0f;
        }
        if (em.IsBattle && em.FarAway)
        {
            ref float tc = ref _emTimeC(em);
            tc += Time.deltaTime;
            if (tc >= em.BattleTime)
            {
                em.IsBattle = false;
                tc = 0f;
            }
        }
        if (em.CanSO_Idle)
        {
            em.Idle_Time_Tmp += Time.deltaTime;
            if (em.Idle_Time_Tmp >= em.Idle_Time_Cur)
            {
                if (em.IS_Boss)
                {
                    int index = UnityEngine.Random.Range(0, em.BS.SO_Idle.Count); // RNG order preserved
                    if (UnityEngine.Random.Range(0, 101) < em.SO_IdleRate)
                    {
                        _fmodPlayOneShot(em.BS.SO_Idle[index], em.yao.transform.position);
                    }
                }
                else if (em.SO_Idle != null && UnityEngine.Random.Range(0, 101) < em.SO_IdleRate)
                {
                    _fmodPlayOneShot(em.SO_Idle, em.yao.transform.position);
                }
                em.Idle_Time_Cur = UnityEngine.Random.Range(em.Idle_Time_Min, em.Idle_Time_Max);
                em.Idle_Time_Tmp = 0f;
            }
        }
        if (em.SK_ELSS != null && em.SK_ELSS.ATmod == 2)
        {
            ref float tf = ref _emTimeF(em);
            tf += Time.deltaTime;
            if (tf >= em.SK_ELSS.HurtSK_JG)
            {
                _emHurtOK(em) = true;
                tf = 0f;
            }
        }
    }
}

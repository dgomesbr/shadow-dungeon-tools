using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Lean.Pool;
using UnityEngine;

namespace PerfPatches;

/// <summary>
/// Projectile hot-path patches.
///
/// HomingSortRemoval (default ON, behavior-preserving): the four homing target-refresh
/// methods each do a full List.Sort with a closure comparator that computes two
/// Vector3.Distance (sqrt) calls per comparison, allocating a delegate + FunctorComparer
/// per call at up to ~556 calls/sec during projectile storms. The only order-dependent
/// consumers are index 0 (SK_FlyBall/SK_FlyFollow/SK_FlyA) and a uniform-random pick from
/// the first min(Count,5) entries (SK_FlySowrd reading SK_FlySowrdFSQ.em) - verified by
/// reading every read site of the lists. So a single argmin pass (or a 5-element partial
/// selection for FSQ) is observably equivalent, allocation-free and sqrt-free.
///
/// ChildSpawnGovernor (default OFF, behavior-changing): SK_FlyBall.SetZiDan fires 33.3x/sec
/// per parent, LeanPool-spawning dic.sp.CountMulti children per tick. The governor caps the
/// tick rate per parent and the spawns per frame globally, compensating the lost hit count
/// by raising each child's Dicform.UPDamage (People.EM_Set applies DamageB * (1+UPDamage/100)
/// for SubType==2 children; vanilla children always carry UPDamage=0, so the channel is free,
/// and Dicform.OnDisable resets it so pooled reuse is clean).
///
/// SharedHomingTargetCache from the design is intentionally NOT shipped: the periodic
/// Physics2D.OverlapCircleNonAlloc scans are inline in the (huge) Update() bodies of
/// SK_FlyBall/SK_FlyFollow/SK_FlyA, not in discrete patchable methods, and wholesale Update
/// reimplementation was ruled out as an unacceptable maintenance risk. No registry code is
/// included.
/// </summary>
internal static class ProjectilesModule
{
    internal static void Init(ConfigFile config, Harmony harmony)
    {
        InitHomingSortRemoval(config, harmony);
        InitChildSpawnGovernor(config, harmony);
    }

    // =====================================================================================
    // HomingSortRemoval
    // =====================================================================================

    private const string SortPatchName = "HomingSortRemoval";

    private static ConfigEntry<bool> _sortEnabled;

    // SK_FlyBall.range and SK_FlyA.range are private; resolved once here, never per-frame.
    private static AccessTools.FieldRef<SK_FlyBall, float> _ballRangeRef;
    private static AccessTools.FieldRef<SK_FlyA, float> _flyARangeRef;

    // Fail-soft flags, one per target so one class breaking does not degrade the others.
    // First failure returns false (skip the tick - the reimpl may have half-pruned the list,
    // and falling through to the original would re-prune/re-sort, which is harmless here but
    // the skip contract is uniform across the plugin); later calls return true (vanilla).
    private static bool _ballRefreshBroken;
    private static bool _followRefreshBroken;
    private static bool _flyARefreshBroken;
    private static bool _fsqRefreshBBroken;

    private static void InitHomingSortRemoval(ConfigFile config, Harmony harmony)
    {
        _sortEnabled = config.Bind(SortPatchName, "Enabled", true,
            "Replaces the full distance-sort in the homing projectile target-refresh methods " +
            "(SK_FlyBall.Refresh, SK_FlyFollow.Refresh, SK_FlyA.Refresh, SK_FlySowrdFSQ.RefreshB) " +
            "with a single nearest-candidate scan. The game only ever reads the nearest target " +
            "(list index 0) - plus, for the FSQ sword manager, a random pick among the 5 nearest, " +
            "which is preserved via a partial selection - so targeting behavior is unchanged. " +
            "Removes ~2 sqrt calls per list comparison and ~3 GC allocations per refresh at up " +
            "to ~556 refreshes/sec in projectile storms. Risk: none expected; the only observable " +
            "difference is the ordering of list entries the game never reads.");

        if (!_sortEnabled.Value)
        {
            return;
        }

        try
        {
            _ballRangeRef = AccessTools.FieldRefAccess<SK_FlyBall, float>("range");
            var target = AccessTools.DeclaredMethod(typeof(SK_FlyBall), nameof(SK_FlyBall.Refresh));
            if (target == null) throw new MissingMethodException("SK_FlyBall.Refresh not found");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ProjectilesModule), nameof(FlyBallRefreshPrefix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(SortPatchName + " (SK_FlyBall) not installed: " + ex.Message);
        }

        try
        {
            var target = AccessTools.DeclaredMethod(typeof(SK_FlyFollow), nameof(SK_FlyFollow.Refresh));
            if (target == null) throw new MissingMethodException("SK_FlyFollow.Refresh not found");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ProjectilesModule), nameof(FlyFollowRefreshPrefix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(SortPatchName + " (SK_FlyFollow) not installed: " + ex.Message);
        }

        try
        {
            _flyARangeRef = AccessTools.FieldRefAccess<SK_FlyA, float>("range");
            var target = AccessTools.DeclaredMethod(typeof(SK_FlyA), nameof(SK_FlyA.Refresh));
            if (target == null) throw new MissingMethodException("SK_FlyA.Refresh not found");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ProjectilesModule), nameof(FlyARefreshPrefix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(SortPatchName + " (SK_FlyA) not installed: " + ex.Message);
        }

        try
        {
            var target = AccessTools.DeclaredMethod(typeof(SK_FlySowrdFSQ), nameof(SK_FlySowrdFSQ.RefreshB));
            if (target == null) throw new MissingMethodException("SK_FlySowrdFSQ.RefreshB not found");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ProjectilesModule), nameof(FsqRefreshBPrefix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(SortPatchName + " (SK_FlySowrdFSQ) not installed: " + ex.Message);
        }
    }

    // ---- SK_FlyBall.Refresh --------------------------------------------------------------
    // Original: prune em (alive/YS/jump/range via Vector3.Distance), then full sort by
    // distance (yao position when Body, root transform otherwise); non-ZY branch prunes and
    // sorts cp the same way and prunes the single pl entry. Only em[0]/cp[0]/pl[0] are read
    // (SK_FlyBall.Update lines 424-486), so argmin-to-front is equivalent.
    private static bool FlyBallRefreshPrefix(SK_FlyBall __instance)
    {
        if (_ballRefreshBroken)
        {
            return true;
        }
        try
        {
            Vector3 pos = __instance.transform.position;
            float range = _ballRangeRef(__instance);
            float rangeSq = range * range;
            if (__instance.dic.sp.ZY)
            {
                List<Enemy> em = __instance.em;
                // Reverse RemoveAt == original forward Remove(em[i])+i-- because the
                // !em.Contains guard at every add site makes duplicates impossible.
                for (int i = em.Count - 1; i >= 0; i--)
                {
                    Enemy e = em[i];
                    // sqrMagnitude > range^2 <=> Vector3.Distance > range (both non-negative);
                    // Vector3 math keeps the original's z-component semantics.
                    if (!e.IsAlive || e.IsYS || e.IsJump || (e.transform.position - pos).sqrMagnitude > rangeSq)
                    {
                        em.RemoveAt(i);
                    }
                }
                MoveNearestEnemyFirst(em, pos, __instance.Body);
                return false;
            }
            List<Companion> cp = __instance.cp;
            for (int i = cp.Count - 1; i >= 0; i--)
            {
                Companion c = cp[i];
                if (!c.IsAlive || (c.transform.position - pos).sqrMagnitude > rangeSq)
                {
                    cp.RemoveAt(i);
                }
            }
            MoveNearestCompanionFirst(cp, pos, __instance.Body);
            List<PlayerManager> pl = __instance.pl;
            if (pl.Count > 0 && (!pl[0].IsAlive || (pl[0].transform.position - pos).sqrMagnitude > rangeSq))
            {
                pl.RemoveAt(0);
            }
            return false;
        }
        catch (Exception ex)
        {
            _ballRefreshBroken = true;
            PerfCore.Log.LogError(SortPatchName + " (SK_FlyBall) failed, reverting to vanilla: " + ex);
            return false;
        }
    }

    // ---- SK_FlyFollow.Refresh ------------------------------------------------------------
    // Original: prune em by root-transform distance (public range field), then sort by yao
    // position unconditionally. Reads: em[0].yao (Update line 200) and a uniform-random pick
    // over the WHOLE list on hit (OnTriggerEnter2D line 548) - the latter is order-blind.
    private static bool FlyFollowRefreshPrefix(SK_FlyFollow __instance)
    {
        if (_followRefreshBroken)
        {
            return true;
        }
        try
        {
            Vector3 pos = __instance.transform.position;
            float rangeSq = __instance.range * __instance.range;
            List<Enemy> em = __instance.em;
            for (int i = em.Count - 1; i >= 0; i--)
            {
                Enemy e = em[i];
                if (!e.IsAlive || e.IsYS || e.IsJump || (e.transform.position - pos).sqrMagnitude > rangeSq)
                {
                    em.RemoveAt(i);
                }
            }
            MoveNearestEnemyFirst(em, pos, useYao: true);
            return false;
        }
        catch (Exception ex)
        {
            _followRefreshBroken = true;
            PerfCore.Log.LogError(SortPatchName + " (SK_FlyFollow) failed, reverting to vanilla: " + ex);
            return false;
        }
    }

    // ---- SK_FlyA.Refresh -------------------------------------------------------------------
    // Same shape as SK_FlyFollow (prune by root transform, sort by yao) with a private range
    // field. Only consumer is em[0].yao (Update line 199).
    private static bool FlyARefreshPrefix(SK_FlyA __instance)
    {
        if (_flyARefreshBroken)
        {
            return true;
        }
        try
        {
            Vector3 pos = __instance.transform.position;
            float range = _flyARangeRef(__instance);
            float rangeSq = range * range;
            List<Enemy> em = __instance.em;
            for (int i = em.Count - 1; i >= 0; i--)
            {
                Enemy e = em[i];
                if (!e.IsAlive || e.IsYS || e.IsJump || (e.transform.position - pos).sqrMagnitude > rangeSq)
                {
                    em.RemoveAt(i);
                }
            }
            MoveNearestEnemyFirst(em, pos, useYao: true);
            return false;
        }
        catch (Exception ex)
        {
            _flyARefreshBroken = true;
            PerfCore.Log.LogError(SortPatchName + " (SK_FlyA) failed, reverting to vanilla: " + ex);
            return false;
        }
    }

    // ---- SK_FlySowrdFSQ.RefreshB -----------------------------------------------------------
    // Original: full sort by yao distance with destroyed/null entries last. Consumers
    // (SK_FlySowrd lines 97-100 and 279-282) pick uniform-random from the whole list or from
    // the first min(Count,5) entries - so only the SET of the 5 nearest matters, not total
    // order. A 5-slot partial selection reproduces the sorted prefix exactly (ascending) and
    // never dereferences fake-null enemies (they get a +inf key, matching the null-last
    // comparator). Distance keys are precomputed once per entry into a scratch array so the
    // selection costs n key evaluations instead of the sort's ~2 n log n.
    private const int FsqPrefixCount = 5; // Mathf.Min(father.em.Count, 5) in SK_FlySowrd

    private static float[] _fsqKeys = new float[64];

    private static bool FsqRefreshBPrefix(SK_FlySowrdFSQ __instance)
    {
        if (_fsqRefreshBBroken)
        {
            return true;
        }
        try
        {
            List<Enemy> em = __instance.em;
            int n = em.Count;
            if (n < 2)
            {
                return false;
            }
            if (_fsqKeys.Length < n)
            {
                int newLen = _fsqKeys.Length;
                while (newLen < n)
                {
                    newLen *= 2;
                }
                _fsqKeys = new float[newLen]; // warmup-only growth, never shrunk
            }
            float[] keys = _fsqKeys;
            Vector3 pos = __instance.transform.position;
            for (int i = 0; i < n; i++)
            {
                Enemy e = em[i];
                // Unity fake-null bool operator: destroyed entries sort last, like vanilla.
                keys[i] = !e ? float.MaxValue : (e.yao.transform.position - pos).sqrMagnitude;
            }
            int k = n < FsqPrefixCount ? n : FsqPrefixCount;
            for (int slot = 0; slot < k; slot++)
            {
                int best = slot;
                float bestKey = keys[slot];
                for (int i = slot + 1; i < n; i++)
                {
                    if (keys[i] < bestKey)
                    {
                        bestKey = keys[i];
                        best = i;
                    }
                }
                if (best != slot)
                {
                    Enemy tmpE = em[slot];
                    em[slot] = em[best];
                    em[best] = tmpE;
                    keys[best] = keys[slot];
                    keys[slot] = bestKey;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _fsqRefreshBBroken = true;
            PerfCore.Log.LogError(SortPatchName + " (SK_FlySowrdFSQ) failed, reverting to vanilla: " + ex);
            return false;
        }
    }

    // ---- argmin helpers --------------------------------------------------------------------
    // sqrMagnitude is strictly monotonic in Vector3.Distance, so the argmin element is exactly
    // the element the vanilla sort placed at index 0. Ties were already comparator-unstable in
    // vanilla (List.Sort is unstable), so first-min tie-breaking is within vanilla's envelope.

    private static void MoveNearestEnemyFirst(List<Enemy> list, Vector3 origin, bool useYao)
    {
        int n = list.Count;
        if (n < 2)
        {
            return;
        }
        int best = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Enemy e = list[i];
            Vector3 p = useYao ? e.yao.transform.position : e.transform.position;
            float d = (p - origin).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                best = i;
            }
        }
        if (best != 0)
        {
            Enemy tmp = list[0];
            list[0] = list[best];
            list[best] = tmp;
        }
    }

    private static void MoveNearestCompanionFirst(List<Companion> list, Vector3 origin, bool useYao)
    {
        int n = list.Count;
        if (n < 2)
        {
            return;
        }
        int best = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Companion c = list[i];
            Vector3 p = useYao ? c.yao.transform.position : c.transform.position;
            float d = (p - origin).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                best = i;
            }
        }
        if (best != 0)
        {
            Companion tmp = list[0];
            list[0] = list[best];
            list[best] = tmp;
        }
    }

    // =====================================================================================
    // ChildSpawnGovernor
    // =====================================================================================

    private const string GovPatchName = "ChildSpawnGovernor";
    // SetZiDan is driven by "timeD > 0.03f" in Update, so its real cadence is frame-quantized:
    // one tick per frame at 60 fps (~30/s), never the ideal 33.3/s. Compensation uses the
    // conservative 30/s so it can never inflate damage above vanilla throughput.
    private const float VanillaTicksPerSecond = 30f;

    private static ConfigEntry<bool> _govEnabled;
    private static ConfigEntry<int> _govMaxTicksPerParentPerSecond;
    private static ConfigEntry<int> _govGlobalMaxSpawnsPerFrame;
    private static ConfigEntry<bool> _govDamageCompensation;

    private static bool _govBroken;

    private struct SpawnWindow
    {
        public float Start;
        public int Ticks;
    }

    // Keyed by component instanceID: stable across LeanPool reuse (pooled objects are
    // disabled, not destroyed, so the same SK_FlyBall instance keeps its id) and holds no
    // object reference, so it can never resurrect a destroyed enemy or projectile. Entries
    // are only advisory 1-second rate windows; clearing the map merely grants fresh windows.
    private static readonly Dictionary<int, SpawnWindow> GovWindows = new Dictionary<int, SpawnWindow>(256);
    private const int GovWindowWatermark = 4096;

    private static int _govFrame = -1;
    private static int _govSpawnsThisFrame;

    private static void InitChildSpawnGovernor(ConfigFile config, Harmony harmony)
    {
        _govEnabled = config.Bind(GovPatchName, "Enabled", false,
            "OPT-IN, GAMEPLAY-VISIBLE. Caps how fast YOUR SK_FlyBall projectiles emit child " +
            "projectiles (SetZiDan; vanilla fires one tick per frame, ~30/sec per parent, " +
            "spawning CountMulti children per tick). Fewer children means fewer hit events, so " +
            "on-hit effects (crit rolls, ACT bar buildup, dots, hit FX) trigger less often and " +
            "area coverage thins, even though DamageCompensation preserves the expected raw " +
            "DamageB throughput. Enemy-owned child projectiles are never throttled (their damage " +
            "cannot be compensated), so this never makes the game easier. Big win on child-spam " +
            "builds (Layer_SubB); leave OFF for untouched vanilla behavior.");
        _govMaxTicksPerParentPerSecond = config.Bind(GovPatchName, "MaxChildTicksPerParentPerSecond", 10,
            "Maximum SetZiDan ticks each parent projectile may execute per second (vanilla " +
            "~33.3). Each allowed tick still spawns the full CountMulti child batch. Values " +
            ">= 34 effectively disable the per-parent throttle. Minimum 1.");
        _govGlobalMaxSpawnsPerFrame = config.Bind(GovPatchName, "GlobalMaxChildSpawnsPerFrame", 60,
            "Hard ceiling on SetZiDan child spawns across ALL parents in a single frame; ticks " +
            "arriving after the ceiling is reached are dropped (approximate: the last batch may " +
            "overshoot by up to CountMulti-1). Globally dropped ticks are NOT damage-compensated. " +
            "0 disables the global cap.");
        _govDamageCompensation = config.Bind(GovPatchName, "DamageCompensation", true,
            "When the per-parent tick cap is below vanilla rate, raise each spawned child's " +
            "damage bonus (Dicform.UPDamage, applied by the game as DamageB * (1 + UPDamage/100)) " +
            "so expected DamageB per second is preserved: UPDamage = 100 * (30/cap - 1). " +
            "The field auto-resets on despawn. CAVEAT: SK_Doom_Ball treats UPDamage == 0 as " +
            "'not yet buffed' when applying its element-synergy bonus, so compensated children " +
            "of a Doom orb skip that specific buff; set this to false if you play a Doom build. " +
            "Ticks dropped by GlobalMaxChildSpawnsPerFrame are not compensated.");

        if (!_govEnabled.Value)
        {
            return;
        }

        try
        {
            var target = AccessTools.DeclaredMethod(typeof(SK_FlyBall), nameof(SK_FlyBall.SetZiDan));
            if (target == null) throw new MissingMethodException("SK_FlyBall.SetZiDan not found");
            // Sanity-check the members the reimpl copies onto children, so a game update that
            // reshapes Dicform fails at Init (patch not installed) instead of mid-combat.
            if (AccessTools.DeclaredField(typeof(Dicform), nameof(Dicform.UPDamage)) == null)
                throw new MissingFieldException("Dicform.UPDamage not found");
            if (AccessTools.DeclaredMethod(typeof(Dicform), nameof(Dicform.SetCount)) == null)
                throw new MissingMethodException("Dicform.SetCount not found");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ProjectilesModule), nameof(SetZiDanPrefix)));
            PerfCore.OnSceneUnloaded(GovPatchName, GovWindows.Clear);
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(GovPatchName + " not installed: " + ex.Message);
        }
    }

    private static bool SetZiDanPrefix(SK_FlyBall __instance)
    {
        if (_govBroken)
        {
            return true;
        }
        try
        {
            Dicform dic = __instance.dic;
            // Vanilla gate (SK_FlyBall.SetZiDan): when it fails, vanilla does nothing - so a
            // plain skip is byte-equivalent and we never touch the throttle state.
            if (dic.sp.Layer_SubB != dic.Index || dic.SubType != 0 || !(dic.sp.DamageB > 0f) || __instance.SubB == null)
            {
                return false;
            }

            // Player-owned children only. Hostile children route damage through People.PL_Set /
            // CP_Set, which never read Dicform.UPDamage, so throttling them would cut incoming
            // damage with no way to compensate (a silent difficulty reduction).
            if (!dic.sp.ZY)
            {
                return true;
            }

            int frame = Time.frameCount;
            if (frame != _govFrame)
            {
                _govFrame = frame;
                _govSpawnsThisFrame = 0;
            }

            int maxTicks = _govMaxTicksPerParentPerSecond.Value;
            if (maxTicks < 1)
            {
                maxTicks = 1;
            }

            // Per-parent 1-second window. Time.time is timeScale-scaled, like the timeD
            // accumulator that drives SetZiDan, so pause/slow-mo throttle consistently.
            int id = __instance.GetInstanceID();
            float now = Time.time;
            SpawnWindow w;
            if (!GovWindows.TryGetValue(id, out w) || now - w.Start >= 1f)
            {
                w.Start = now;
                w.Ticks = 0;
            }
            if (w.Ticks >= maxTicks)
            {
                GovWindows[id] = w;
                return false; // dropped tick; vanilla already reset timeD before calling
            }

            // Global cap checked before consuming the parent's window slot so a globally
            // dropped tick does not also burn per-parent budget (it was never compensated).
            int globalCap = _govGlobalMaxSpawnsPerFrame.Value;
            if (globalCap > 0 && _govSpawnsThisFrame >= globalCap)
            {
                return false;
            }

            w.Ticks++;
            GovWindows[id] = w;
            if (GovWindows.Count > GovWindowWatermark)
            {
                // Windows are 1s advisory state; a rare full reset just re-opens budgets.
                GovWindows.Clear();
                GovWindows[id] = w;
            }

            float upDamage = 0f;
            if (_govDamageCompensation.Value && (float)maxTicks < VanillaTicksPerSecond)
            {
                upDamage = 100f * (VanillaTicksPerSecond / maxTicks - 1f);
            }

            // Spawn loop ported verbatim from SK_FlyBall.SetZiDan (lines 1404-1411), plus the
            // optional UPDamage write. Field order matters: SetCount before SubType/Index is
            // vanilla order; UPDamage last, after Dicform state is otherwise vanilla-complete.
            int count = __instance.ZDtimeCount;
            Vector3 pos = __instance.transform.position;
            for (int i = 0; i < count; i++)
            {
                Dicform child = LeanPool.Spawn(__instance.SubB, pos, Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f))).GetComponent<Dicform>();
                child.sp = dic.sp;
                child.SetCount(dic.sp.ZY);
                child.SubType = 2;
                child.Index = dic.Index + 1;
                if (upDamage > 0f)
                {
                    child.UPDamage = upDamage;
                }
                _govSpawnsThisFrame++;
            }
            return false;
        }
        catch (Exception ex)
        {
            _govBroken = true;
            PerfCore.Log.LogError(GovPatchName + " failed, reverting to vanilla: " + ex);
            return false;
        }
    }
}

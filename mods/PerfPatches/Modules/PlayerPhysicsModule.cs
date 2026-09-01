using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using FinkFramework.Runtime.Singleton;
using HarmonyLib;
using UnityEngine;

namespace PerfPatches;

/// <summary>
/// PlayerManager physics/scan hot-path patches.
///
/// WallRaycastFrameCache (default ON, behavior-preserving): PlayerManager.IsEnemyBlockedByWall
/// raycasts player->enemy and is called for the SAME enemies several times per frame - by
/// HasNearbyCompanionFollowEnemy (every frame), RefreshAutoLockTargets (20 Hz, and its winner is
/// raycast twice), and JSQ's 0.2s prune+re-add. All callers run in the Update phase, where
/// physics state cannot change between calls, so a same-frame memo keyed by enemy instance id
/// is exact. Prefix serves hits, Postfix stores misses.
///
/// CollectEnemiesInRangeReimpl (default ON, behavior-preserving): prefix-skip port of
/// PlayerManager.CollectEnemiesInRange replacing the O(n^2) result.Contains with a HashSet,
/// reading the private footCOLemLayerMask via FieldRef, and routing line-of-sight through the
/// patched IsEnemyBlockedByWall so the frame cache applies. Result ordering (physics overlap
/// order) and the enemyRangeHits growth loop are preserved exactly.
///
/// CompanionFollowScanThrottle (OPT-IN, timing change): serves a cached
/// HasNearbyCompanionFollowEnemy result within a configurable interval instead of running the
/// 7f-radius overlap + per-enemy raycast chain every frame.
///
/// JSQTickOptimizer (default ON, guarded): prefix-skip port of PlayerManager.JSQ. Per-frame
/// RemoveAll delegate walks become in-place reverse loops; the 0.2s tick's distance sort uses
/// precomputed sqrMagnitude keys in parallel arrays (Array.Sort - unstable like List.Sort,
/// monotonic in Vector3.Distance, so ordering is identical); em.Contains becomes a HashSet;
/// LayerMask.GetMask results are cached (the layer table is immutable at runtime); LOS goes
/// through the frame cache. Every downstream call (ApplyBurnLife, RefreshNearbyDotBuffDamage,
/// RefreshNearbyEnemyStatCounts, RefreshRuntimeDerivedStats, UpdateAutoDrink, auto-pickup,
/// TriggerEnemyDotExplosions) runs in the original order. Two compatibility guards run at
/// Init before patching: every private member the port touches must resolve, AND the vanilla
/// method's IL body (plus its compiler-generated lambda bodies) must match a hardcoded
/// FNV-1a fingerprint taken from the exact game build this port was written against. A game
/// update that changes JSQ in ANY way fails the fingerprint and the patch is simply not
/// installed - vanilla runs, behavior can never silently diverge. The same gate protects
/// the CollectEnemiesInRange port.
///
/// AutoLockRefreshInterval (OPT-IN, timing change): postfix rebasing the autolock
/// next-refresh timestamp so the 20 Hz vanilla cadence can be lowered. Vanilla folds target
/// validation into the same 20 Hz scan; to keep a dead lock target from lingering at the
/// slower cadence, a full-rate field-read check (no physics) forces an immediate re-scan
/// the moment the locked enemy dies/despawns - only acquisition among LIVE targets slows.
/// </summary>
internal static class PlayerPhysicsModule
{
    internal static void Init(ConfigFile config, Harmony harmony)
    {
        InitWallRaycastFrameCache(config, harmony);
        InitCollectEnemiesInRangeReimpl(config, harmony);
        InitCompanionFollowScanThrottle(config, harmony);
        InitJsqTickOptimizer(config, harmony);
        InitAutoLockRefreshInterval(config, harmony);

        // One shared invalidation hook: everything cached here is either frame-scoped or
        // advisory, so a blanket clear on scene unload is always safe.
        PerfCore.OnSceneUnloaded("PlayerPhysics", ClearAllCaches);
    }

    private static void ClearAllCaches()
    {
        WallCache.Clear();
        CompanionScanCache.Clear();
        CollectScratch.Clear();
        EmScratch.Clear();
        Array.Clear(_sortItems, 0, _sortItems.Length); // drop Enemy refs so the unloaded scene can be collected
        _autoLockCachedFoot = null;
        _autoLockCachedEnemy = null;
    }

    // =====================================================================================
    // Shared resolution: private PlayerManager members used by more than one patch.
    // Resolved once, never per-frame. Each patch resolves what it needs inside its own
    // try-block so one missing member only disables the patches that depend on it.
    // =====================================================================================

    private static Func<PlayerManager, Enemy, bool> _isEnemyBlockedByWall;

    private static Func<PlayerManager, Enemy, bool> ResolveBlockedByWall()
    {
        if (_isEnemyBlockedByWall == null)
        {
            var mi = AccessTools.DeclaredMethod(typeof(PlayerManager), "IsEnemyBlockedByWall", new[] { typeof(Enemy) });
            if (mi == null) throw new MissingMethodException("PlayerManager.IsEnemyBlockedByWall(Enemy) not found");
            // Open delegate onto the exact private method. Calls through it still hit the
            // Harmony detour (the detour rewrites the compiled method entry), so the frame
            // cache below applies to every reimplemented caller too.
            _isEnemyBlockedByWall = AccessTools.MethodDelegate<Func<PlayerManager, Enemy, bool>>(mi, null, virtualCall: false);
        }
        return _isEnemyBlockedByWall;
    }

    // LayerMask.GetMask results never change after startup (the layer name table is baked at
    // build time), so a one-shot cache is exact even though vanilla re-queries every call.
    private static int _footMask;
    private static bool _footMaskResolved;

    private static int FootColEmMask()
    {
        if (!_footMaskResolved)
        {
            _footMask = LayerMask.GetMask("FootCOLem");
            _footMaskResolved = true;
        }
        return _footMask;
    }

    private static int _autoPickMask;
    private static bool _autoPickMaskResolved;

    private static int AutoPickMask()
    {
        if (!_autoPickMaskResolved)
        {
            _autoPickMask = LayerMask.GetMask("AutoPick");
            _autoPickMaskResolved = true;
        }
        return _autoPickMask;
    }

    // =====================================================================================
    // Body-integrity gate for the prefix-skip reimplementations.
    //
    // A prefix-skip port silently diverges if a game update changes the method it replaces,
    // so before patching we fingerprint the vanilla IL and compare against a hash computed
    // offline from the exact Assembly-CSharp.dll this port was written against. The
    // fingerprint is FNV-1a 64 folded over: the root method's GetILAsByteArray(), then every
    // compiler-generated lambda body ("<Name>b__*" methods on PlayerManager and its direct
    // nested types - Roslyn hosts this-capturing lambdas on the type itself and capture-free
    // ones on the nested <>c), sorted by name (ordinal) for determinism, folding each name's
    // chars then its IL. Lambda bodies live OUTSIDE the root method's IL stream, so hashing
    // them too closes the gap where an update rewrites only a predicate/comparator.
    //
    // Harmony patches by other plugins do not alter metadata IL (detours are native-level),
    // and this install has no BepInEx preloader patchers (BepInEx/patchers is empty), so at
    // Init time GetILAsByteArray returns the same bytes as the DLL on disk. Runs once at
    // Init; the allocations here never touch a frame.
    // =====================================================================================

    // Computed 2026-09-01 from the shipped Assembly-CSharp.dll:
    // JSQ: root IL 937 bytes + 4 lambdas (<JSQ>b__1077_0 on <>c = "e => !e",
    //   <JSQ>b__1077_1 on <>c = "c => !c", <JSQ>b__1077_2 on PlayerManager = the 0.2s prune
    //   predicate, <JSQ>b__1077_3 on PlayerManager = the distance-sort comparator).
    private const ulong JsqExpectedBodyHash = 0x909B9170545A4082UL;

    // CollectEnemiesInRange: root IL 347 bytes, no lambdas.
    private const ulong CollectExpectedBodyHash = 0xC00B0A316E0EFC8DUL;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong FoldIl(ulong h, byte[] data)
    {
        unchecked
        {
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= FnvPrime;
            }
        }
        return h;
    }

    private static ulong FoldName(ulong h, string s)
    {
        unchecked
        {
            // Compiler-generated names are pure ASCII ('<', '>', letters, digits, '_'),
            // so the (byte) cast is lossless and matches the offline computation.
            for (int i = 0; i < s.Length; i++)
            {
                h ^= (byte)s[i];
                h *= FnvPrime;
            }
        }
        return h;
    }

    /// <summary>Returns true when the vanilla body still matches the build this port was
    /// written against. On mismatch logs a clear warning; the caller must skip the patch.</summary>
    private static bool VerifyBodyHash(MethodInfo root, string methodName, ulong expected, string patchName)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        byte[] il = root.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            PerfCore.Log.LogWarning(patchName + " not installed: PlayerManager." + methodName +
                " has no readable IL body - cannot verify the port is still valid.");
            return false;
        }
        ulong h = FoldIl(FnvOffset, il);

        string prefix = "<" + methodName + ">b__";
        List<MethodInfo> lambdas = new List<MethodInfo>();
        Type owner = typeof(PlayerManager);
        foreach (MethodInfo m in owner.GetMethods(All))
        {
            if (m.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                lambdas.Add(m);
            }
        }
        foreach (Type nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (MethodInfo m in nested.GetMethods(All))
            {
                if (m.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    lambdas.Add(m);
                }
            }
        }
        lambdas.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (int i = 0; i < lambdas.Count; i++)
        {
            byte[] body = lambdas[i].GetMethodBody()?.GetILAsByteArray();
            if (body == null)
            {
                PerfCore.Log.LogWarning(patchName + " not installed: lambda " + lambdas[i].Name +
                    " of PlayerManager." + methodName + " has no readable IL body.");
                return false;
            }
            h = FoldName(h, lambdas[i].Name);
            h = FoldIl(h, body);
        }

        if (h != expected)
        {
            PerfCore.Log.LogWarning(patchName + " not installed: PlayerManager." + methodName +
                " IL fingerprint mismatch (got 0x" + h.ToString("X16") + ", expected 0x" +
                expected.ToString("X16") + "). A game update changed the method; running vanilla " +
                "so behavior cannot silently diverge. The port needs re-verification against the new build.");
            return false;
        }
        return true;
    }

    // =====================================================================================
    // WallRaycastFrameCache
    // =====================================================================================

    private const string WallPatchName = "WallRaycastFrameCache";

    private static ConfigEntry<bool> _wallCacheEnabled;

    private struct WallCacheEntry
    {
        public int Frame;
        public int PhysicsStep;
        // The exact ray endpoints the cached verdict was computed for. Frame + physics step alone
        // are not sufficient: the lazy autolock path (TryGetAutoLockYaoPosition/FootPosition,
        // reached from Gun/ACTbar/SK_Orb_Self Updates) can re-enter this method LATER in the same
        // frame, after movement scripts have moved the player or the enemy. Requiring identical
        // endpoints makes a cache hit provably the same raycast vanilla would perform, and a
        // moved endpoint simply re-raycasts.
        public Vector2 Origin;
        public Vector2 Target;
        public bool Blocked;
    }

    // Physics bodies only move on a fixed step, so a cached raycast stays valid exactly as long
    // as BOTH the render frame and the physics step are unchanged. Frame alone is not enough:
    // other scripts' Update/FixedUpdate slots (Gun, MGC, ACTbar aim code) can reach
    // IsEnemyBlockedByWall after a physics step within the same rendered frame.
    private static int CurrentPhysicsStep()
    {
        float step = Time.fixedDeltaTime;
        return step > 0f ? (int)(Time.fixedTime / step) : 0;
    }

    // Keyed by Enemy component instance id: stable across LeanPool reuse (pooled objects are
    // disabled, not destroyed, so the component keeps its id) and unique for the lifetime of
    // the process. Entries self-expire via the frame key, so stale ids are inert; the map is
    // cleared on scene unload and size-bounded as belt-and-suspenders.
    private static readonly Dictionary<int, WallCacheEntry> WallCache = new Dictionary<int, WallCacheEntry>(256);
    private const int WallCacheWatermark = 4096;

    private static void InitWallRaycastFrameCache(ConfigFile config, Harmony harmony)
    {
        _wallCacheEnabled = config.Bind(WallPatchName, "Enabled", true,
            "Memoizes PlayerManager.IsEnemyBlockedByWall (the player->enemy wall raycast) per " +
            "enemy per frame. The companion-follow scan, autolock refresh and the 0.2s enemy " +
            "bookkeeping tick all raycast the same enemies within one frame; all run in the " +
            "Update phase where physics state cannot change between calls, so the cached answer " +
            "is exact and behavior is unchanged. On a dense floor with autolock this removes " +
            "roughly half of all player line-of-sight raycasts. Risk: none known; only a " +
            "hypothetical future caller that moves colliders mid-frame between two checks " +
            "could observe a difference.");

        if (!_wallCacheEnabled.Value)
        {
            return;
        }

        try
        {
            var target = AccessTools.DeclaredMethod(typeof(PlayerManager), "IsEnemyBlockedByWall", new[] { typeof(Enemy) });
            if (target == null) throw new MissingMethodException("PlayerManager.IsEnemyBlockedByWall(Enemy) not found");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(WallCachePrefix)),
                postfix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(WallCachePostfix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(WallPatchName + " not installed: " + ex.Message);
        }
    }

    // __state = true means the prefix answered from cache, so the postfix must not re-store
    // (the original did not run and __result is our own cached value anyway).
    private static bool WallCachePrefix(PlayerManager __instance, Enemy enemy, ref bool __result, out bool __state)
    {
        __state = false;
        // Real null (not just Unity fake-null) has no instance id; let vanilla's !enemy
        // branch handle it. Fake-null (destroyed/pooled-away) still has a valid id and
        // vanilla deterministically returns true for it, so caching that is fine.
        if ((object)enemy == null)
        {
            return true;
        }
        WallCacheEntry entry;
        int physicsStep = CurrentPhysicsStep();
        if (WallCache.TryGetValue(enemy.GetInstanceID(), out entry)
            && entry.Frame == Time.frameCount && entry.PhysicsStep == physicsStep)
        {
            // Same frame, same physics step AND both ray endpoints unmoved: the raycast is
            // guaranteed to produce the identical result, so serving it is exact. Reading two
            // transforms is still far cheaper than the raycast it replaces. A fake-null enemy
            // cannot be dereferenced for a position, but vanilla returns a constant for it, so
            // that verdict needs no endpoint check.
            if (!enemy)
            {
                __result = entry.Blocked;
                __state = true;
                return false;
            }
            Vector2 origin = __instance.transform.position;
            Vector2 target = enemy.transform.position;
            if (entry.Origin == origin && entry.Target == target)
            {
                __result = entry.Blocked;
                __state = true;
                return false;
            }
        }
        return true;
    }

    private static void WallCachePostfix(PlayerManager __instance, Enemy enemy, bool __result, bool __state)
    {
        if (__state || (object)enemy == null)
        {
            return;
        }
        if (WallCache.Count > WallCacheWatermark)
        {
            // Entries are one-frame memos; dumping them only costs re-raycasts this frame.
            WallCache.Clear();
        }
        WallCacheEntry entry;
        entry.Frame = Time.frameCount;
        entry.PhysicsStep = CurrentPhysicsStep();
        entry.Blocked = __result;
        // Record the endpoints vanilla just used (PlayerManager.cs:6400-6402). A fake-null enemy
        // took the constant-return path, so its endpoints are irrelevant; store a sentinel that
        // can never equal a live transform read.
        if (enemy)
        {
            entry.Origin = __instance.transform.position;
            entry.Target = enemy.transform.position;
        }
        else
        {
            entry.Origin = new Vector2(float.NaN, float.NaN);
            entry.Target = new Vector2(float.NaN, float.NaN);
        }
        WallCache[enemy.GetInstanceID()] = entry;
    }

    // =====================================================================================
    // CollectEnemiesInRangeReimpl
    // =====================================================================================

    private const string CollectPatchName = "CollectEnemiesInRangeReimpl";

    private static ConfigEntry<bool> _collectEnabled;

    private static Action<PlayerManager> _ensureEnemyRangeBuffers;
    private static AccessTools.FieldRef<PlayerManager, Collider2D[]> _enemyRangeHitsRef;
    private static AccessTools.FieldRef<PlayerManager, int> _footCOLemLayerMaskRef;

    // Unity's Object.Equals/GetHashCode are instance-id based and match the == semantics the
    // vanilla List.Contains used through EqualityComparer<Enemy>.Default, so this set is an
    // exact drop-in for the O(n^2) Contains scan.
    private static readonly HashSet<Enemy> CollectScratch = new HashSet<Enemy>();

    private static bool _collectBroken;

    private static void InitCollectEnemiesInRangeReimpl(ConfigFile config, Harmony harmony)
    {
        _collectEnabled = config.Bind(CollectPatchName, "Enabled", true,
            "Reimplements PlayerManager.CollectEnemiesInRange with a HashSet instead of the " +
            "O(n^2) List.Contains duplicate check, a cached layer mask, and line-of-sight " +
            "checks routed through the wall-raycast frame cache. Called every frame by the " +
            "companion-follow scan plus 20 Hz / 1 Hz / 0.2s consumers; with ~50 enemies this " +
            "replaces ~1250 list comparisons per call with ~50 hash lookups. Result contents " +
            "and ordering are identical to vanilla. Guarded: at startup the vanilla method's " +
            "IL is fingerprinted against the build this port was written for; if a game " +
            "update changed it, the patch is skipped (logged) and vanilla runs. Risk: low.");

        if (!_collectEnabled.Value)
        {
            return;
        }

        try
        {
            var ensure = AccessTools.DeclaredMethod(typeof(PlayerManager), "EnsureEnemyRangeBuffers", Type.EmptyTypes);
            if (ensure == null) throw new MissingMethodException("PlayerManager.EnsureEnemyRangeBuffers not found");
            _ensureEnemyRangeBuffers = AccessTools.MethodDelegate<Action<PlayerManager>>(ensure, null, virtualCall: false);
            _enemyRangeHitsRef = AccessTools.FieldRefAccess<PlayerManager, Collider2D[]>("enemyRangeHits");
            _footCOLemLayerMaskRef = AccessTools.FieldRefAccess<PlayerManager, int>("footCOLemLayerMask");
            ResolveBlockedByWall();

            var target = AccessTools.DeclaredMethod(typeof(PlayerManager), nameof(PlayerManager.CollectEnemiesInRange));
            if (target == null) throw new MissingMethodException("PlayerManager.CollectEnemiesInRange not found");
            if (!VerifyBodyHash(target, "CollectEnemiesInRange", CollectExpectedBodyHash, CollectPatchName))
            {
                return; // VerifyBodyHash already logged why
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(CollectEnemiesInRangePrefix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(CollectPatchName + " not installed: " + ex.Message);
        }
    }

    // Verbatim port of PlayerManager.CollectEnemiesInRange (PlayerManager.cs:7696) with the
    // three optimizations described in InitCollectEnemiesInRangeReimpl. Check order inside the
    // filter is preserved exactly so raycast counts match vanilla (the LOS check fires before
    // the duplicate check, as in the original).
    private static bool CollectEnemiesInRangePrefix(PlayerManager __instance, float range, List<Enemy> result, bool onlyNormalEnemy, bool requireLineOfSight)
    {
        if (_collectBroken)
        {
            return true;
        }
        try
        {
            _ensureEnemyRangeBuffers(__instance);
            if (result == null)
            {
                return false;
            }
            result.Clear();
            ref Collider2D[] hits = ref _enemyRangeHitsRef(__instance);
            if (range <= 0f || hits == null)
            {
                return false;
            }
            int mask = _footCOLemLayerMaskRef(__instance);
            if (mask == 0)
            {
                mask = FootColEmMask();
            }
            if (mask == 0)
            {
                return false;
            }
            Vector2 pos = __instance.transform.position;
            int count;
            // Growth loop preserved exactly: resize while the buffer saturates, capped at 128.
            // Writing through the FieldRef keeps the grown buffer on the instance like vanilla.
            while ((count = Physics2D.OverlapCircleNonAlloc(pos, range, hits, mask)) == hits.Length && hits.Length < 128)
            {
                Array.Resize(ref hits, hits.Length * 2);
            }
            HashSet<Enemy> seen = CollectScratch;
            seen.Clear();
            for (int i = 0; i < count; i++)
            {
                Collider2D collider = hits[i];
                hits[i] = null;
                if (!collider || !collider.TryGetComponent<FootCOL>(out var foot) || !foot)
                {
                    continue;
                }
                People people = foot.peo;
                if ((bool)people && people.CharacterType == 2)
                {
                    Enemy enemy = people.em;
                    if ((bool)enemy && enemy.IsAlive && !enemy.IsWuDi && !enemy.IsJump && !enemy.IsYS
                        && (!onlyNormalEnemy || enemy.Quality < 2)
                        && (!requireLineOfSight || !_isEnemyBlockedByWall(__instance, enemy))
                        && seen.Add(enemy))
                    {
                        result.Add(enemy);
                    }
                }
            }
            seen.Clear();
            return false;
        }
        catch (Exception ex)
        {
            // First failure: skip this call entirely (result stays partially built but that is
            // what vanilla exposes mid-exception too); afterwards vanilla runs permanently.
            _collectBroken = true;
            PerfCore.Log.LogError(CollectPatchName + " failed, reverting to vanilla: " + ex);
            return false;
        }
    }

    // =====================================================================================
    // CompanionFollowScanThrottle
    // =====================================================================================

    private const string CompanionPatchName = "CompanionFollowScanThrottle";

    private static ConfigEntry<bool> _companionEnabled;
    private static ConfigEntry<float> _companionInterval;

    private struct CompanionScanEntry
    {
        public float Next;
        public bool Value;
    }

    // Keyed by PlayerManager instance id. PlayerManager is a scene singleton, but keying by
    // id keeps the cache correct across scene transitions where a new instance appears.
    private static readonly Dictionary<int, CompanionScanEntry> CompanionScanCache = new Dictionary<int, CompanionScanEntry>(4);

    private static void InitCompanionFollowScanThrottle(ConfigFile config, Harmony harmony)
    {
        _companionEnabled = config.Bind(CompanionPatchName, "Enabled", false,
            "OPT-IN, TIMING CHANGE. Throttles PlayerManager.HasNearbyCompanionFollowEnemy - " +
            "vanilla runs a 7-unit-radius physics overlap plus a wall raycast per found enemy " +
            "EVERY frame just to decide whether the companion-follow anchor is in combat mode. " +
            "With a throttle the cached yes/no answer is reused within ScanInterval. Effect: " +
            "the follow anchor reacts up to ScanInterval later when combat starts or ends; no " +
            "damage or targeting outcome changes. Requires ScanInterval > 0 to do anything.");
        _companionInterval = config.Bind(CompanionPatchName, "ScanInterval", 0f,
            "Seconds between real companion-follow enemy scans. 0 = vanilla every-frame " +
            "scanning (patch inert even when Enabled). Recommended 0.1: removes ~83% of the " +
            "scan cost at 60 fps and the added anchor latency is imperceptible. Uses unscaled " +
            "time, matching how often the result can actually change perceptibly.");

        if (!_companionEnabled.Value)
        {
            return;
        }

        try
        {
            var target = AccessTools.DeclaredMethod(typeof(PlayerManager), "HasNearbyCompanionFollowEnemy", Type.EmptyTypes);
            if (target == null) throw new MissingMethodException("PlayerManager.HasNearbyCompanionFollowEnemy not found");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(CompanionScanPrefix)),
                postfix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(CompanionScanPostfix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(CompanionPatchName + " not installed: " + ex.Message);
        }
    }

    // __state = true means the original ran this call, so the postfix should refresh the cache.
    private static bool CompanionScanPrefix(PlayerManager __instance, ref bool __result, out bool __state)
    {
        __state = false;
        if (_companionInterval.Value <= 0f)
        {
            __state = true; // vanilla cadence; still record results so raising the interval later works seamlessly
            return true;
        }
        CompanionScanEntry entry;
        if (CompanionScanCache.TryGetValue(__instance.GetInstanceID(), out entry) && Time.unscaledTime < entry.Next)
        {
            __result = entry.Value;
            return false;
        }
        __state = true;
        return true;
    }

    private static void CompanionScanPostfix(PlayerManager __instance, bool __result, bool __state)
    {
        if (!__state)
        {
            return;
        }
        CompanionScanEntry entry;
        entry.Next = Time.unscaledTime + _companionInterval.Value;
        entry.Value = __result;
        CompanionScanCache[__instance.GetInstanceID()] = entry;
    }

    // =====================================================================================
    // JSQTickOptimizer
    // =====================================================================================

    private const string JsqPatchName = "JSQTickOptimizer";

    private static ConfigEntry<bool> _jsqEnabled;

    private static AccessTools.FieldRef<PlayerManager, float> _timeLevelRef;
    private static AccessTools.FieldRef<PlayerManager, bool> _isLevelUpRef;
    private static Action<PlayerManager> _applyBurnLife;
    private static Action<PlayerManager> _refreshNearbyDotBuffDamage;
    private static Action<PlayerManager> _refreshNearbyEnemyStatCounts;
    private static Action<PlayerManager> _updateAutoDrink;
    private static Action<PlayerManager> _triggerEnemyDotExplosions;

    // Parallel scratch arrays for the 0.2s distance sort: keys are precomputed sqrMagnitudes
    // (monotonic in Vector3.Distance, so Array.Sort produces the exact vanilla order; both
    // sorts are unstable so tie handling stays within vanilla's envelope). Grown on demand,
    // never shrunk - warmup-only allocation.
    private static Enemy[] _sortItems = new Enemy[64];
    private static float[] _sortKeys = new float[64];

    private static readonly HashSet<Enemy> EmScratch = new HashSet<Enemy>();

    private static bool _jsqBroken;

    private static void InitJsqTickOptimizer(ConfigFile config, Harmony harmony)
    {
        _jsqEnabled = config.Bind(JsqPatchName, "Enabled", true,
            "Reimplements PlayerManager.JSQ (the player's per-frame bookkeeping plus the 0.2s " +
            "enemy-list tick). Behavior-identical port: the every-frame RemoveAll delegate " +
            "walks become in-place loops, the 0.2s distance sort uses precomputed squared " +
            "distances (same resulting order, no per-comparison sqrt or transform reads, no " +
            "allocations), the O(n^2) duplicate check becomes a HashSet, layer masks are " +
            "cached, and wall raycasts go through the frame cache. All regen/burn/drink/" +
            "pickup/dot-explosion calls run in the original order. Double-guarded at startup: " +
            "every private member the port needs must resolve, AND the vanilla method's IL " +
            "(including its lambda bodies) must match a fingerprint of the exact game build " +
            "this port reproduces - any game update to JSQ disables the patch (logged) and " +
            "vanilla runs. Risk: low; disable to A/B against vanilla.");

        if (!_jsqEnabled.Value)
        {
            return;
        }

        try
        {
            // Compatibility guard: resolve every private member the port touches BEFORE
            // patching. A game update that renames/reshapes any of them fails here and the
            // patch is simply not installed (public members are checked at compile time
            // against the shipped Assembly-CSharp).
            _timeLevelRef = AccessTools.FieldRefAccess<PlayerManager, float>("timeLevel");
            _isLevelUpRef = AccessTools.FieldRefAccess<PlayerManager, bool>("IsLevelUP");
            _applyBurnLife = ResolvePrivateAction("ApplyBurnLife");
            _refreshNearbyDotBuffDamage = ResolvePrivateAction("RefreshNearbyDotBuffDamage");
            _refreshNearbyEnemyStatCounts = ResolvePrivateAction("RefreshNearbyEnemyStatCounts");
            _updateAutoDrink = ResolvePrivateAction("UpdateAutoDrink");
            _triggerEnemyDotExplosions = ResolvePrivateAction("TriggerEnemyDotExplosions");
            ResolveBlockedByWall();

            var target = AccessTools.DeclaredMethod(typeof(PlayerManager), nameof(PlayerManager.JSQ));
            if (target == null) throw new MissingMethodException("PlayerManager.JSQ not found");
            if (!VerifyBodyHash(target, "JSQ", JsqExpectedBodyHash, JsqPatchName))
            {
                return; // VerifyBodyHash already logged why; vanilla JSQ keeps running
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(JsqPrefix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(JsqPatchName + " not installed (vanilla JSQ keeps running): " + ex.Message);
        }
    }

    private static Action<PlayerManager> ResolvePrivateAction(string name)
    {
        // Type.EmptyTypes disambiguates overloads - ApplyBurnLife also exists as
        // ApplyBurnLife(int, DamageType) and we must call the parameterless one.
        var mi = AccessTools.DeclaredMethod(typeof(PlayerManager), name, Type.EmptyTypes);
        if (mi == null) throw new MissingMethodException("PlayerManager." + name + "() not found");
        return AccessTools.MethodDelegate<Action<PlayerManager>>(mi, null, virtualCall: false);
    }

    // Verbatim-but-optimized port of PlayerManager.JSQ (PlayerManager.cs:6457). Statement
    // order matters everywhere: RefreshRuntimeDerivedStats may change EM_Range, so EM_Range is
    // read after it, like vanilla; the LOS raycast in the prune runs only after the cheaper
    // alive/range checks pass (vanilla short-circuit order), keeping raycast counts identical.
    // Drops destroyed/fake-null entries in place, preserving the order of survivors.
    private static void CompactDestroyed<T>(List<T> list) where T : UnityEngine.Object
    {
        int write = 0;
        int count = list.Count;
        for (int read = 0; read < count; read++)
        {
            T item = list[read];
            if (item)
            {
                if (write != read)
                {
                    list[write] = item;
                }
                write++;
            }
        }
        if (write < count)
        {
            list.RemoveRange(write, count - write);
        }
    }

    private static bool JsqPrefix(PlayerManager __instance)
    {
        if (_jsqBroken)
        {
            return true;
        }
        try
        {
            List<Enemy> em = __instance.em;
            List<Companion> cp = __instance.cp;
            // em.RemoveAll(e => !e) / cp.RemoveAll(c => !c) equivalents: single forward
            // compaction (same O(n) shape and same surviving order as RemoveAll) without the
            // per-call closure allocation. Reverse RemoveAt would be O(n * removed).
            CompactDestroyed(em);
            CompactDestroyed(cp);

            float dt = Time.deltaTime;
            if (_isLevelUpRef(__instance))
            {
                ref float timeLevel = ref _timeLevelRef(__instance);
                timeLevel += dt;
                if (timeLevel >= 0.5f)
                {
                    timeLevel = 0f;
                    _isLevelUpRef(__instance) = false;
                }
            }

            if (__instance.IsAlive)
            {
                __instance.TimeA += dt;
                if (__instance.TimeA >= 1f)
                {
                    __instance.HealStat.Cur += __instance.Health_R_Max;
                    __instance.ManaStat.Cur += __instance.Mana_R_Max;
                    if (__instance.BloodLost && __instance.HealStat.Cur > __instance.HealStat.Max / 2f)
                    {
                        __instance.HealStat.Cur -= __instance.HealStat.Max / 3f;
                    }
                    _applyBurnLife(__instance);
                    _refreshNearbyDotBuffDamage(__instance);
                    __instance.TimeA = 0f;
                }
            }

            __instance.TimeB += dt;
            if (__instance.TimeB >= 0.2f)
            {
                _refreshNearbyEnemyStatCounts(__instance);
                __instance.RefreshRuntimeDerivedStats();
                _updateAutoDrink(__instance);

                Vector3 pos = __instance.transform.position;
                float emRange = __instance.EM_Range;
                float emRangeSq = emRange * emRange;

                // em.RemoveAll(e => !e || !e.IsAlive || Vector2.Distance(...) > EM_Range ||
                // IsEnemyBlockedByWall(e)) - sqr compare is exact for non-negative ranges; a
                // (pathological) negative EM_Range removes everything alive, like vanilla.
                for (int i = em.Count - 1; i >= 0; i--)
                {
                    Enemy e = em[i];
                    bool remove = !e || !e.IsAlive;
                    if (!remove)
                    {
                        Vector2 delta = (Vector2)e.transform.position - (Vector2)pos;
                        remove = emRange < 0f || delta.sqrMagnitude > emRangeSq || _isEnemyBlockedByWall(__instance, e);
                    }
                    if (remove)
                    {
                        em.RemoveAt(i);
                    }
                }

                // Distance sort with precomputed keys. Vanilla's comparator re-read both
                // transforms per comparison; positions cannot change during the sort, so
                // snapshotting is exact. Nulls (impossible after the purge above, but the
                // vanilla comparator handled them) get MaxValue = sorted last, same as vanilla.
                int n = em.Count;
                if (n > 1)
                {
                    if (_sortItems.Length < n)
                    {
                        int newLen = _sortItems.Length;
                        while (newLen < n)
                        {
                            newLen *= 2;
                        }
                        _sortItems = new Enemy[newLen];
                        _sortKeys = new float[newLen];
                    }
                    for (int i = 0; i < n; i++)
                    {
                        Enemy e = em[i];
                        _sortItems[i] = e;
                        _sortKeys[i] = !e ? float.MaxValue : (e.transform.position - pos).sqrMagnitude;
                    }
                    Array.Sort(_sortKeys, _sortItems, 0, n);
                    for (int i = 0; i < n; i++)
                    {
                        em[i] = _sortItems[i];
                        _sortItems[i] = null; // do not pin enemies in a static array between ticks
                    }
                }

                // Replaces em.Contains inside the re-add loop below. Membership semantics match
                // List.Contains: both go through Unity's instance-id based Equals.
                HashSet<Enemy> emSet = EmScratch;
                emSet.Clear();
                for (int i = 0; i < em.Count; i++)
                {
                    emSet.Add(em[i]);
                }

                // Re-add pass. hitEM is intentionally NOT grown (vanilla never grows it here,
                // so overflow truncation at 10 entries is vanilla behavior). Check order kept:
                // the duplicate check runs BEFORE IsJump/IsYS/LOS, so known enemies skip the
                // raycast exactly like vanilla.
                Collider2D[] hitEM = __instance.hitEM;
                int num = Physics2D.OverlapCircleNonAlloc(pos, emRange, hitEM, FootColEmMask());
                if (num > 0)
                {
                    for (int i = 0; i < num; i++)
                    {
                        FootCOL foot = hitEM[i].GetComponent<FootCOL>();
                        if ((bool)foot)
                        {
                            People peo = foot.peo;
                            if (peo.CharacterType == 2 && peo.em.IsAlive && !emSet.Contains(peo.em) && !peo.em.IsJump && !peo.em.IsYS && !_isEnemyBlockedByWall(__instance, peo.em))
                            {
                                em.Add(peo.em);
                                emSet.Add(peo.em);
                            }
                            hitEM[i] = null;
                        }
                    }
                }
                emSet.Clear();

                // Auto-pickup sweep with the vanilla DPIT growth loop (grows unboundedly by
                // doubling until the overlap fits; resize writes back to the public field).
                float pickRange = __instance.Pick_PL_Max;
                int pickMask = AutoPickMask();
                int num2;
                while (true)
                {
                    num2 = Physics2D.OverlapCircleNonAlloc(pos, pickRange, __instance.DPIT, pickMask);
                    if (num2 < __instance.DPIT.Length)
                    {
                        break;
                    }
                    Array.Resize(ref __instance.DPIT, __instance.DPIT.Length * 2);
                }
                if (num2 > 0)
                {
                    Collider2D[] dpit = __instance.DPIT;
                    for (int j = 0; j < num2; j++)
                    {
                        Collider2D collider = dpit[j];
                        dpit[j] = null;
                        DropItemController drop = (collider ? collider.GetComponent<DropItemController>() : null);
                        if ((bool)drop && drop.CanAutoPick && SingletonMonoScope<InventoryManager>.HasInstance && SingletonMonoScope<InventoryManager>.Instance.CanPlayerAutoHandle(drop))
                        {
                            SingletonMonoScope<InventoryManager>.Instance.AutoPickUp(drop);
                        }
                    }
                }
                __instance.TimeB = 0f;
            }

            __instance.TimeC += dt;
            if (__instance.TimeC >= 3f)
            {
                _triggerEnemyDotExplosions(__instance);
                __instance.TimeC = 0f;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Never fall through to the original mid-way: timers/heals may already have been
            // applied this tick and would double-apply. Skip the rest of this tick, then
            // vanilla permanently.
            _jsqBroken = true;
            PerfCore.Log.LogError(JsqPatchName + " failed, reverting to vanilla: " + ex);
            return false;
        }
    }

    // =====================================================================================
    // AutoLockRefreshInterval
    // =====================================================================================

    private const string AutoLockPatchName = "AutoLockRefreshInterval";
    private const float VanillaAutoLockInterval = 0.05f;

    private static ConfigEntry<bool> _autoLockEnabled;
    private static ConfigEntry<float> _autoLockInterval;

    private static AccessTools.FieldRef<PlayerManager, float> _autoLockNextRefreshTimeRef;
    private static AccessTools.FieldRef<PlayerManager, Transform> _autoLockFootTargetRef;

    // Enemy component behind the current lock target, recovered once per acquisition (vanilla
    // stores only the Transform; the Enemy sits on the same GameObject). Lets the full-rate
    // death check below read plain fields instead of GetComponent every frame.
    private static Transform _autoLockCachedFoot;
    private static Enemy _autoLockCachedEnemy;

    private static void InitAutoLockRefreshInterval(ConfigFile config, Harmony harmony)
    {
        _autoLockEnabled = config.Bind(AutoLockPatchName, "Enabled", false,
            "OPT-IN, TIMING CHANGE. Lets you lower the autolock target-refresh rate. Vanilla " +
            "re-scans candidates (physics overlap + a wall raycast per candidate) every 0.05s " +
            "(20 Hz); that same scan is also what drops a dead target, since vanilla has no " +
            "separate validation path. This patch throttles only the ACQUISITION scan: a " +
            "cheap field-read check still runs every frame and forces an immediate re-scan " +
            "the moment the locked enemy dies, despawns or becomes untargetable, so a dead " +
            "lock target never lingers (it actually drops faster than vanilla's 50ms worst " +
            "case). The cost of the throttle is slower target SWITCHING between live targets " +
            "(e.g. 0.1 = up to 100ms to prefer a new nearest enemy). Aim at the currently " +
            "locked target is unaffected between refreshes.");
        _autoLockInterval = config.Bind(AutoLockPatchName, "RefreshInterval", VanillaAutoLockInterval,
            "Seconds between autolock target refreshes. 0.05 = vanilla 20 Hz (patch " +
            "effectively inert). 0.1 halves the autolock raycast load. Values below 0.05 " +
            "refresh MORE often than vanilla (more raycasts, snappier switching). Uses " +
            "unscaled time like vanilla.");

        if (!_autoLockEnabled.Value)
        {
            return;
        }

        try
        {
            _autoLockNextRefreshTimeRef = AccessTools.FieldRefAccess<PlayerManager, float>("autoLockNextRefreshTime");
            _autoLockFootTargetRef = AccessTools.FieldRefAccess<PlayerManager, Transform>("autoLockFootTarget");
            var target = AccessTools.DeclaredMethod(typeof(PlayerManager), "RefreshAutoLockTargets", Type.EmptyTypes);
            if (target == null) throw new MissingMethodException("PlayerManager.RefreshAutoLockTargets not found");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(AutoLockPrefix)),
                postfix: new HarmonyMethod(typeof(PlayerPhysicsModule), nameof(AutoLockPostfix)));
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(AutoLockPatchName + " not installed: " + ex.Message);
        }
    }

    private static bool _autoLockBroken;

    // Runs every frame (PlayerManager.Update calls RefreshAutoLockTargets unconditionally).
    // Captures the timer for the postfix, then performs the FULL-RATE validity check: if the
    // currently locked enemy is dead/despawned/untargetable, open the timer gate so the
    // original re-scans on THIS call instead of waiting out the throttled interval. The check
    // is plain field reads (no physics); wall-occlusion changes remain on the throttled
    // cadence by design - death is the case that must never wait.
    private static void AutoLockPrefix(PlayerManager __instance, out float __state)
    {
        ref float next = ref _autoLockNextRefreshTimeRef(__instance);
        __state = next;
        if (_autoLockBroken)
        {
            return;
        }
        try
        {
            Transform foot = _autoLockFootTargetRef(__instance);
            if ((object)foot == null)
            {
                return; // no current lock target - nothing to validate
            }
            if (!ReferenceEquals(foot, _autoLockCachedFoot))
            {
                // New acquisition since we last looked: recover its Enemy once. Vanilla assigns
                // autoLockFootTarget = enemy.transform, so the component lives on that GameObject.
                _autoLockCachedFoot = foot;
                _autoLockCachedEnemy = foot ? foot.GetComponent<Enemy>() : null;
            }
            Enemy e = _autoLockCachedEnemy;
            if (!e || !e.isActiveAndEnabled || !e.IsAlive || e.IsJump || e.IsYS)
            {
                // Same liveness terms as IsAutoLockEnemyValid minus the wall raycast. -999f is
                // vanilla's own "refresh immediately" sentinel; the original's timer gate now
                // passes and it will either lock a new valid enemy or ClearAutoLockTargets.
                next = -999f;
            }
        }
        catch (Exception ex)
        {
            _autoLockBroken = true;
            PerfCore.Log.LogError(AutoLockPatchName + " failed, reverting to vanilla cadence: " + ex);
        }
    }

    // Only rebase when the original actually performed a refresh. Detection: the field
    // changed AND is positive. Early-outs leave it untouched; the autolock-inactive path
    // writes -999 (must stay -999 so the next activation refreshes immediately); a real
    // refresh writes unscaledTime + 0.05, which we replace. Time.unscaledTime is constant
    // within a frame, so this reproduces the original's base timestamp exactly.
    private static void AutoLockPostfix(PlayerManager __instance, float __state)
    {
        if (_autoLockBroken)
        {
            return; // leave the vanilla 0.05s timer the original wrote untouched
        }
        ref float next = ref _autoLockNextRefreshTimeRef(__instance);
        if (next == __state || next < 0f)
        {
            return;
        }
        float interval = _autoLockInterval.Value;
        if (interval <= 0f)
        {
            interval = VanillaAutoLockInterval;
        }
        next = Time.unscaledTime + interval;
    }
}

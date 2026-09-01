using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Lean.Pool;
using UnityEngine;
using UnityEngine.UI;

namespace PerfPatches;

/// <summary>
/// UI feedback hot-path patches.
///
/// SCT_FastMerge (default ON, behavior-preserving): DamgeTextManager.CreatCombatText runs
/// TryMergeCombatText on EVERY hit - a full reverse scan of up to 80 live combat texts with
/// two fake-null interop checks, an activeInHierarchy interop call and a Vector2.Distance
/// (sqrt) per entry. At ~1000 hits/sec that is ~80k iterations / ~240k interop calls per
/// second, plus a string format + Text.set_text per merge and a heap ActiveCombatText per
/// spawn. This module prefix-skips CreatCombatText AND the manager's private Update and keeps
/// the tracking state plugin-side: a spatial hash (cell size == merge distance, so a 3x3
/// neighborhood probe is geometrically complete) keyed by (DamageType, cell) replaces the
/// linear scan, entries are pooled, and merge-time text formatting is deferred to a single
/// LateUpdate flush per frame (LateUpdate runs after every gameplay Update and before render,
/// so each rendered frame shows exactly the same digits vanilla would have shown).
/// Movement, fade-in-place, lifetime, the 80-text cap, the 0.08s merge window, the 0.25u
/// merge radius, nearest-candidate-wins and midpoint SourcePosition averaging are preserved
/// verbatim from DamgeTextManager.cs:82-167.
///
/// SCT_Budget (own section, all defaults inert): tunables layered inside the reimplemented
/// path - MaxConcurrentTexts (80 = vanilla), MinDamageToShow (0 = vanilla), MergeWindowScale
/// and MergeDistanceScale (1 = vanilla). Purely cosmetic load shedding, only active when the
/// user edits a value AND enables the section.
///
/// HealthBar_Throttle (default ON, cosmetic-only): every damage/heal event funnels through
/// EnemyStat setters into RefreshBar, which writes Image.fillAmount unconditionally
/// (EnemyStat.cs:61-74) - each enemy owns its own world-space bar canvas, so every write is a
/// separate canvas dirty/rebuild registration. Gameplay only ever reads the currentValue
/// float (which the untouched setters keep updating synchronously); fillAmount is pure
/// cosmetics. The prefix collects dirty bars into a set and a LateUpdate driver flushes them
/// at a configurable Hz, always writing the LATEST currentValue/maxValue so every bar lands
/// on the exact final value. Death/zero, empty-max and full-heal writes bypass the throttle
/// (same-frame), an Initialize postfix force-writes so a LeanPool-recycled enemy never
/// shows the previous occupant's fill on its first visible frame, and every teardown path
/// (scene unload, runtime failure) drains pending bars by WRITING the live ones, never by
/// dropping them - the final write of a burst can never be lost.
/// </summary>
internal static class UiFeedbackModule
{
    internal static void Init(ConfigFile config, Harmony harmony)
    {
        InitSctFastMerge(config, harmony);
        InitHealthBarThrottle(config, harmony);
    }

    // =====================================================================================
    // SCT_FastMerge (+ SCT_Budget config layer)
    // =====================================================================================

    private const string SctPatchName = "SCT_FastMerge";
    private const string BudgetPatchName = "SCT_Budget";

    // Vanilla constants, read from DamgeTextManager.cs:39-43. They are private consts (baked
    // into the original IL), so they are duplicated here and must track game updates.
    private const int VanillaMaxTexts = 80;
    private const float VanillaMergeWindow = 0.08f;
    private const float VanillaMergeDistance = 0.25f;

    private static ConfigEntry<bool> _sctEnabled;
    private static ConfigEntry<bool> _budgetEnabled;
    private static ConfigEntry<int> _budgetMaxConcurrent;
    private static ConfigEntry<float> _budgetMinDamage;
    private static ConfigEntry<float> _budgetMergeWindowScale;
    private static ConfigEntry<float> _budgetMergeDistanceScale;

    // Private DamgeTextManager members, resolved once at Init, never per-frame.
    private static AccessTools.FieldRef<DamgeTextManager, bool> _sctToggleRef;
    private static AccessTools.FieldRef<DamgeTextManager, float> _sctScaleRef;
    private static AccessTools.FieldRef<DamgeTextManager, GameObject> _combatTextPrefabRef;
    private static Action<DamgeTextManager> _ensurePrefabsLoaded;

    private sealed class SctEntry
    {
        public Text Text;
        public Transform Tf;
        public Vector2 SourcePosition;
        public DamageType Type;
        public float Damage;
        public float Speed;
        public float LifeTime;
        public float Elapsed;
        public float LastMergeTime;
        public long CellKey;
        public bool TextDirty;
    }

    // Per-prefab-clone constants. Keyed by GameObject instanceID: LeanPool disables (never
    // destroys) pooled clones, so the id and the Text/CombatText components persist across
    // reuse; ids are unique per session so a cleared-then-repopulated cache cannot alias.
    // Speed/LifeTime are [SerializeField] constants per clone (CombatText.cs:8-32), so
    // caching the resolved floats also removes the per-spawn ternary property reads.
    private struct SctCloneInfo
    {
        public Text Text;
        public float Speed;
        public float LifeTime;
    }

    // Plugin-owned tracking state. While the patch is live the game's own activeCombatTexts
    // list stays permanently empty (both writers - CreatCombatText and Update - are skipped),
    // so vanilla and plugin state can never double-apply to the same text.
    private static readonly List<SctEntry> SctActive = new List<SctEntry>(VanillaMaxTexts);
    private static readonly Stack<SctEntry> SctEntryPool = new Stack<SctEntry>(VanillaMaxTexts);
    private static readonly Dictionary<long, List<SctEntry>> SctBuckets = new Dictionary<long, List<SctEntry>>(128);
    private static readonly Stack<List<SctEntry>> SctListPool = new Stack<List<SctEntry>>(32);
    private static readonly Dictionary<int, SctCloneInfo> SctCloneCache = new Dictionary<int, SctCloneInfo>(128);

    private static bool _sctBroken;

    // GetDamageColor is private static with 6 constant cases (DamgeTextManager.cs:169-181);
    // reimplemented verbatim. Color32 literals pre-converted once.
    private static readonly Color SctFrozenColor = new Color32(80, 230, byte.MaxValue, byte.MaxValue);
    private static readonly Color SctShadowColor = new Color32(243, 148, byte.MaxValue, byte.MaxValue);

    private static void InitSctFastMerge(ConfigFile config, Harmony harmony)
    {
        _sctEnabled = config.Bind(SctPatchName, "Enabled", true,
            "Replaces the damage-number merge scan in DamgeTextManager.CreatCombatText (a full " +
            "walk of up to 80 live texts with distance math and native interop per entry, on " +
            "EVERY hit) with a spatial-hash lookup, and defers merged-text string formatting to " +
            "one flush per frame. Merge window (0.08s), merge radius (0.25u), the 80-text cap, " +
            "text movement, lifetime and pooling are reproduced exactly; per rendered frame the " +
            "numbers shown are identical to vanilla. Big win during 100+ projectile barrages " +
            "(both CPU and GC garbage). Risk: low; only equidistant-candidate tie-breaks can " +
            "differ (cosmetic). Changing this requires a game restart.");

        _budgetEnabled = config.Bind(BudgetPatchName, "Enabled", false,
            "OPT-IN, COSMETIC. Master switch for the SCT_Budget tunables below. They only take " +
            "effect while SCT_FastMerge is installed (this section is a config layer inside its " +
            "reimplemented path). With this OFF - or with every value left at its default - the " +
            "behavior is exactly vanilla.");
        _budgetMaxConcurrent = config.Bind(BudgetPatchName, "MaxConcurrentTexts", VanillaMaxTexts,
            new ConfigDescription(
                "Maximum simultaneous damage texts (vanilla 80). Lower values shed per-frame " +
                "Text movement, layout and canvas cost during barrages; hits arriving at the cap " +
                "still merge into nearby texts but never spawn new ones (vanilla behaves the " +
                "same at its cap).",
                new AcceptableValueRange<int>(1, VanillaMaxTexts)));
        _budgetMinDamage = config.Bind(BudgetPatchName, "MinDamageToShow", 0f,
            new ConfigDescription(
                "Hits below this damage never SPAWN a new text (0 = vanilla, show everything). " +
                "They still merge into an existing nearby text first, so aggregate numbers stay " +
                "truthful; only a sub-threshold hit with no merge candidate is silently dropped. " +
                "Late-game damage reaches trillions+ (the game formats up to 1e24 'Y'), so the " +
                "range is deliberately wide.",
                new AcceptableValueRange<float>(0f, 1E+12f)));
        _budgetMergeWindowScale = config.Bind(BudgetPatchName, "MergeWindowScale", 1f,
            new ConfigDescription(
                "Scales the 0.08s merge window (1 = vanilla). Larger values make rapid hits " +
                "accumulate into fewer, bigger numbers for longer - fewer live texts, coarser " +
                "feedback.",
                new AcceptableValueRange<float>(0f, 20f)));
        _budgetMergeDistanceScale = config.Bind(BudgetPatchName, "MergeDistanceScale", 1f,
            new ConfigDescription(
                "Scales the 0.25 world-unit merge radius (1 = vanilla). Larger values merge " +
                "hits on different nearby enemies into one number.",
                new AcceptableValueRange<float>(0.1f, 10f)));

        if (!_sctEnabled.Value)
        {
            return;
        }

        try
        {
            _sctToggleRef = AccessTools.FieldRefAccess<DamgeTextManager, bool>("_sctToggle");
            _sctScaleRef = AccessTools.FieldRefAccess<DamgeTextManager, float>("_sctScale");
            _combatTextPrefabRef = AccessTools.FieldRefAccess<DamgeTextManager, GameObject>("combatTextPrefab");

            var ensure = AccessTools.DeclaredMethod(typeof(DamgeTextManager), "EnsurePrefabsLoaded");
            if (ensure == null) throw new MissingMethodException("DamgeTextManager.EnsurePrefabsLoaded not found");
            _ensurePrefabsLoaded = AccessTools.MethodDelegate<Action<DamgeTextManager>>(ensure, null, false);

            var creat = AccessTools.DeclaredMethod(typeof(DamgeTextManager), nameof(DamgeTextManager.CreatCombatText));
            if (creat == null) throw new MissingMethodException("DamgeTextManager.CreatCombatText not found");
            var update = AccessTools.DeclaredMethod(typeof(DamgeTextManager), "Update");
            if (update == null) throw new MissingMethodException("DamgeTextManager.Update not found");
            // The reimpl calls the public formatter directly (any third-party patches on it,
            // e.g. ReadableNumbers, keep applying); verify it still exists before committing.
            if (AccessTools.DeclaredMethod(typeof(DamgeTextManager), nameof(DamgeTextManager.FormatDamageNumber)) == null)
                throw new MissingMethodException("DamgeTextManager.FormatDamageNumber not found");

            // Transactional: CreatCombatText writes plugin state that only the replacement
            // Update ages/despawns, so it must be both methods or neither.
            harmony.Patch(creat, prefix: new HarmonyMethod(typeof(UiFeedbackModule), nameof(CreatCombatTextPrefix)));
            try
            {
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(UiFeedbackModule), nameof(DtmUpdatePrefix)));
            }
            catch
            {
                harmony.Unpatch(creat, HarmonyPatchType.Prefix, harmony.Id);
                throw;
            }

            PerfCore.OnLateUpdate(SctPatchName, SctLateFlush);
            PerfCore.OnSceneUnloaded(SctPatchName, SctOnSceneUnloaded);
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(SctPatchName + " not installed: " + ex.Message);
        }
    }

    // Cell size == merge distance, so any candidate within merge range of a point lies in the
    // point's cell or one of its 8 neighbors. DamageType is baked into the key, making every
    // bucket single-type. 21 bits per axis: cells only alias 2^21 cells (~524k world units)
    // apart - unreachable.
    private static long SctPackKey(DamageType type, int cx, int cy)
    {
        return ((long)(int)type << 42) | ((long)(uint)(cx & 0x1FFFFF) << 21) | (uint)(cy & 0x1FFFFF);
    }

    private static long SctCellKey(DamageType type, float x, float y, float invCell)
    {
        return SctPackKey(type, Mathf.FloorToInt(x * invCell), Mathf.FloorToInt(y * invCell));
    }

    private static void SctBucketAdd(SctEntry e)
    {
        List<SctEntry> list;
        if (!SctBuckets.TryGetValue(e.CellKey, out list))
        {
            list = SctListPool.Count > 0 ? SctListPool.Pop() : new List<SctEntry>(4);
            SctBuckets.Add(e.CellKey, list);
        }
        list.Add(e);
    }

    private static void SctBucketRemove(SctEntry e)
    {
        List<SctEntry> list;
        if (SctBuckets.TryGetValue(e.CellKey, out list))
        {
            list.Remove(e);
            if (list.Count == 0)
            {
                SctBuckets.Remove(e.CellKey);
                SctListPool.Push(list);
            }
        }
    }

    private static void SctRecycle(SctEntry e)
    {
        e.Text = null;
        e.Tf = null;
        e.TextDirty = false;
        SctEntryPool.Push(e);
    }

    // Original: DamgeTextManager.CreatCombatText (DamgeTextManager.cs:108-138) +
    // TryMergeCombatText (:140-167). `crit` is accepted but unused - vanilla ignores it too.
    private static bool CreatCombatTextPrefix(DamgeTextManager __instance, Vector2 position, float number, DamageType type, bool crit)
    {
        if (_sctBroken)
        {
            return true;
        }
        try
        {
            if (!_sctToggleRef(__instance))
            {
                return false;
            }
            _ensurePrefabsLoaded(__instance);
            position.y += 0.8f;

            bool budget = _budgetEnabled.Value;
            float mergeDistance = budget ? VanillaMergeDistance * Mathf.Max(0.01f, _budgetMergeDistanceScale.Value) : VanillaMergeDistance;
            float mergeWindow = budget ? VanillaMergeWindow * Mathf.Max(0f, _budgetMergeWindowScale.Value) : VanillaMergeWindow;
            float invCell = 1f / mergeDistance;

            float time = Time.time;
            int cx = Mathf.FloorToInt(position.x * invCell);
            int cy = Mathf.FloorToInt(position.y * invCell);

            // Nearest-candidate-wins with an inclusive radius, matching the original scan
            // (dist <= 0.25, ties replace). Compared in squared space - sqrt-free.
            SctEntry best = null;
            float bestSq = mergeDistance * mergeDistance;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<SctEntry> bucket;
                    if (!SctBuckets.TryGetValue(SctPackKey(type, cx + dx, cy + dy), out bucket))
                    {
                        continue;
                    }
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        SctEntry cand = bucket[i];
                        if (time - cand.LastMergeTime > mergeWindow)
                        {
                            continue;
                        }
                        Transform ctf = cand.Tf;
                        if (!ctf || !ctf.gameObject.activeInHierarchy)
                        {
                            continue; // stale entry; the Update replacement prunes it
                        }
                        float ddx = position.x - cand.SourcePosition.x;
                        float ddy = position.y - cand.SourcePosition.y;
                        float sq = ddx * ddx + ddy * ddy;
                        if (sq <= bestSq)
                        {
                            bestSq = sq;
                            best = cand;
                        }
                    }
                }
            }

            if (best != null)
            {
                best.Damage += number;
                best.SourcePosition = (best.SourcePosition + position) * 0.5f;
                best.LastMergeTime = time;
                // Vanilla formats + writes the Text here, per merge. Deferring to one
                // LateUpdate flush per frame is render-identical (LateUpdate precedes the
                // frame's render) and caps string work at dirty-texts-per-frame.
                best.TextDirty = true;
                // Midpoint averaging can drift the entry across a cell boundary; rehash so
                // future probes keep finding it.
                long newKey = SctCellKey(type, best.SourcePosition.x, best.SourcePosition.y, invCell);
                if (newKey != best.CellKey)
                {
                    SctBucketRemove(best);
                    best.CellKey = newKey;
                    SctBucketAdd(best);
                }
                return false;
            }

            int maxTexts = budget ? _budgetMaxConcurrent.Value : VanillaMaxTexts;
            if (maxTexts > VanillaMaxTexts)
            {
                maxTexts = VanillaMaxTexts; // never exceed the vanilla cap the pools are sized for
            }
            if (SctActive.Count >= maxTexts)
            {
                return false;
            }
            if (budget)
            {
                float minDamage = _budgetMinDamage.Value;
                if (minDamage > 0f && number < minDamage)
                {
                    return false; // merge was already attempted above, so aggregates stay truthful
                }
            }

            // Spawn path ported verbatim from :116-135, with GetComponent lookups memoized per
            // pooled clone. SourcePosition intentionally stores the un-jittered position, and
            // Random.Range is called exactly once per spawn (and never on merges) - RNG stream
            // parity with vanilla.
            GameObject obj = LeanPool.Spawn(_combatTextPrefabRef(__instance), __instance.transform);
            int id = obj.GetInstanceID();
            SctCloneInfo info;
            if (!SctCloneCache.TryGetValue(id, out info))
            {
                Text text = obj.GetComponent<Text>();
                CombatText combat = obj.GetComponent<CombatText>();
                info.Text = text;
                info.Speed = combat ? combat.Speed : 0.3f;
                info.LifeTime = combat ? combat.LifeTime : 0.5f;
                SctCloneCache.Add(id, info);
            }
            Text component = info.Text;
            Transform transform = component.transform;
            transform.position = position + new Vector2(UnityEngine.Random.Range(-0.1f, 0.1f), 0f);
            float t = Mathf.InverseLerp(1f, 3f, _sctScaleRef(__instance));
            component.fontSize = Mathf.RoundToInt(Mathf.Lerp(14f, 43f, t));
            component.color = SctDamageColor(type);
            component.text = DamgeTextManager.FormatDamageNumber(number);

            SctEntry e = SctEntryPool.Count > 0 ? SctEntryPool.Pop() : new SctEntry();
            e.Text = component;
            e.Tf = transform;
            e.SourcePosition = position;
            e.Type = type;
            e.Damage = number;
            e.Speed = info.Speed;
            e.LifeTime = info.LifeTime;
            e.Elapsed = 0f;
            e.LastMergeTime = time;
            e.CellKey = SctCellKey(type, position.x, position.y, invCell);
            e.TextDirty = false;
            SctActive.Add(e);
            SctBucketAdd(e);
            return false;
        }
        catch (Exception ex)
        {
            // First failure: skip this hit (never fall through mid-way - a merge or spawn may
            // already be half-applied). Later calls run vanilla; our texts are despawned so
            // nothing floats forever once vanilla's (empty) list takes over.
            _sctBroken = true;
            PerfCore.Log.LogError(SctPatchName + " (CreatCombatText) failed, reverting to vanilla: " + ex);
            SctAbandonState();
            return false;
        }
    }

    // Original: private DamgeTextManager.Update (DamgeTextManager.cs:82-106) - reverse-scan
    // prune of dead/inactive texts, lifetime despawn, upward Translate. Identical here, plus
    // bucket/pool bookkeeping on every removal.
    private static bool DtmUpdatePrefix()
    {
        if (_sctBroken)
        {
            return true;
        }
        try
        {
            float deltaTime = Time.deltaTime;
            for (int i = SctActive.Count - 1; i >= 0; i--)
            {
                SctEntry e = SctActive[i];
                Transform tf = e.Tf;
                if (!tf || !tf.gameObject.activeInHierarchy)
                {
                    // Destroyed with a scene, or despawned by someone else: drop tracking
                    // without despawning, exactly like vanilla (:88-91).
                    SctActive.RemoveAt(i);
                    SctBucketRemove(e);
                    SctRecycle(e);
                }
                else
                {
                    e.Elapsed += deltaTime;
                    if (e.Elapsed >= e.LifeTime)
                    {
                        LeanPool.Despawn(tf.gameObject);
                        SctActive.RemoveAt(i);
                        SctBucketRemove(e);
                        SctRecycle(e);
                    }
                    else
                    {
                        tf.Translate(Vector2.up * e.Speed * deltaTime);
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _sctBroken = true;
            PerfCore.Log.LogError(SctPatchName + " (Update) failed, reverting to vanilla: " + ex);
            SctAbandonState();
            return false;
        }
    }

    // Deferred merge-text formatting: one pass per frame, in LateUpdate so it lands after
    // every gameplay Update that could merge this frame and before the frame renders.
    private static void SctLateFlush()
    {
        if (_sctBroken)
        {
            return;
        }
        try
        {
            for (int i = 0; i < SctActive.Count; i++)
            {
                SctEntry e = SctActive[i];
                if (!e.TextDirty)
                {
                    continue;
                }
                e.TextDirty = false;
                Text text = e.Text;
                if (text)
                {
                    text.text = DamgeTextManager.FormatDamageNumber(e.Damage);
                }
            }
        }
        catch (Exception ex)
        {
            _sctBroken = true;
            PerfCore.Log.LogError(SctPatchName + " (flush) failed, reverting to vanilla: " + ex);
            SctAbandonState();
        }
    }

    private static void SctOnSceneUnloaded()
    {
        // Instance-id keys are never reused within a session, but the cache must not pin
        // destroyed Text references; surviving pooled clones simply re-resolve on next spawn.
        SctCloneCache.Clear();
        for (int i = SctActive.Count - 1; i >= 0; i--)
        {
            SctEntry e = SctActive[i];
            if (!e.Tf) // fake-null aware: destroyed with the scene
            {
                SctActive.RemoveAt(i);
                SctBucketRemove(e);
                SctRecycle(e);
            }
        }
    }

    // Runtime failure handoff: vanilla resumes with its own EMPTY list, which would leave our
    // still-visible texts un-aged forever - so despawn everything we own, best-effort.
    private static void SctAbandonState()
    {
        for (int i = 0; i < SctActive.Count; i++)
        {
            try
            {
                Transform tf = SctActive[i].Tf;
                if (tf && tf.gameObject.activeInHierarchy)
                {
                    LeanPool.Despawn(tf.gameObject);
                }
            }
            catch
            {
                // ignore: teardown must not throw
            }
        }
        SctActive.Clear();
        SctBuckets.Clear();
        SctCloneCache.Clear();
    }

    // Verbatim reimplementation of private static GetDamageColor (DamgeTextManager.cs:169-181).
    private static Color SctDamageColor(DamageType type)
    {
        switch (type)
        {
            case DamageType.fire: return Color.red;
            case DamageType.frozen: return SctFrozenColor;
            case DamageType.thunder: return Color.yellow;
            case DamageType.poison: return Color.green;
            case DamageType.physics: return Color.white;
            case DamageType.shadow: return SctShadowColor;
            default: return Color.white;
        }
    }

    // =====================================================================================
    // HealthBar_Throttle
    // =====================================================================================

    private const string HbPatchName = "HealthBar_Throttle";

    private static ConfigEntry<bool> _hbEnabled;
    private static ConfigEntry<float> _hbUpdateHz;

    private static AccessTools.FieldRef<EnemyStat, Image> _hbContentRef;
    private static AccessTools.FieldRef<EnemyStat, float> _hbMaxRef;
    private static AccessTools.FieldRef<EnemyStat, float> _hbCurRef;
    private static AccessTools.FieldRef<EnemyStat, bool> _hbInitializedRef;

    // Dirty bars awaiting a flush. Holding pooled-but-disabled (or even destroyed) EnemyStats
    // is harmless: the flush is fake-null aware and the set is fully drained every flush, so
    // stale references never accumulate.
    private static readonly HashSet<EnemyStat> HbDirty = new HashSet<EnemyStat>();
    private static readonly List<EnemyStat> HbFlushBuffer = new List<EnemyStat>(64);

    private static float _hbNextFlush;
    private static bool _hbBroken;

    private static void InitHealthBarThrottle(ConfigFile config, Harmony harmony)
    {
        _hbEnabled = config.Bind(HbPatchName, "Enabled", true,
            "Coalesces enemy health/stat bar fill writes. Vanilla writes Image.fillAmount on " +
            "EVERY damage/heal event (EnemyStat.RefreshBar) and each enemy owns its own " +
            "world-space bar canvas, so dense floors pay hundreds of canvas dirty/rebuilds per " +
            "second. This defers the cosmetic write to a fixed rate (UpdateHz below) while " +
            "gameplay HP values stay untouched and synchronous. Bars always land on the exact " +
            "final value; death/zero, empty bars and full heals still draw the same frame, and " +
            "(re)spawned enemies are force-drawn immediately. Risk: the bar can trail true HP " +
            "by up to 1/UpdateHz seconds mid-combat (cosmetic only). Can be toggled at runtime.");
        _hbUpdateHz = config.Bind(HbPatchName, "UpdateHz", 15f,
            new ConfigDescription(
                "How many times per second throttled bars are redrawn (vanilla is effectively " +
                "up to once per damage event per frame). 15 is visually indistinguishable in " +
                "normal play; raise toward 60 to approach vanilla responsiveness.",
                new AcceptableValueRange<float>(1f, 60f)));

        if (!_hbEnabled.Value)
        {
            return;
        }

        try
        {
            _hbContentRef = AccessTools.FieldRefAccess<EnemyStat, Image>("content");
            _hbMaxRef = AccessTools.FieldRefAccess<EnemyStat, float>("maxValue");
            _hbCurRef = AccessTools.FieldRefAccess<EnemyStat, float>("currentValue");
            _hbInitializedRef = AccessTools.FieldRefAccess<EnemyStat, bool>("_initialized");

            var refresh = AccessTools.DeclaredMethod(typeof(EnemyStat), "RefreshBar");
            if (refresh == null) throw new MissingMethodException("EnemyStat.RefreshBar not found");
            var initialize = AccessTools.DeclaredMethod(typeof(EnemyStat), nameof(EnemyStat.Initialize));
            if (initialize == null) throw new MissingMethodException("EnemyStat.Initialize not found");

            harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(UiFeedbackModule), nameof(RefreshBarPrefix)));
            try
            {
                harmony.Patch(initialize, postfix: new HarmonyMethod(typeof(UiFeedbackModule), nameof(InitializePostfix)));
            }
            catch
            {
                // The Initialize force-write is the guarantee against a pool-recycled enemy
                // briefly wearing the previous occupant's fill - without it the throttle is
                // not safe to ship, so remove the prefix too.
                harmony.Unpatch(refresh, HarmonyPatchType.Prefix, harmony.Id);
                throw;
            }

            PerfCore.OnLateUpdate(HbPatchName, HbFlush);
            PerfCore.OnSceneUnloaded(HbPatchName, HbOnSceneUnloaded);
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(HbPatchName + " not installed: " + ex.Message);
        }
    }

    // Exact replica of the RefreshBar body (EnemyStat.cs:61-74), reading the freshest field
    // values at write time - that is what makes deferred flushes always land on the final value.
    private static void HbWriteBar(EnemyStat stat)
    {
        if (!_hbInitializedRef(stat))
        {
            return;
        }
        Image content = _hbContentRef(stat);
        if (!content)
        {
            return;
        }
        float max = _hbMaxRef(stat);
        content.fillAmount = max <= 0f ? 0f : _hbCurRef(stat) / max;
    }

    // Original: private EnemyStat.RefreshBar, called by the MaxValue/CurrentValue setters and
    // Initialize. The setters run ClampCurrentInternal BEFORE RefreshBar, so the fields are
    // already clamped here: cur <= 0 is death/empty and cur >= max is full - both bypass the
    // throttle so those edges draw the same frame they happen.
    private static bool RefreshBarPrefix(EnemyStat __instance)
    {
        if (_hbBroken || !_hbEnabled.Value)
        {
            return true;
        }
        try
        {
            float cur = _hbCurRef(__instance);
            float max = _hbMaxRef(__instance);
            if (cur <= 0f || max <= 0f || cur >= max)
            {
                HbWriteBar(__instance);
                HbDirty.Remove(__instance); // the pending deferred write would be redundant
            }
            else
            {
                HbDirty.Add(__instance);
            }
            return false;
        }
        catch (Exception ex)
        {
            _hbBroken = true;
            PerfCore.Log.LogError(HbPatchName + " failed, reverting to vanilla: " + ex);
            HbDrainPending();
            // Deviates from the plugin's usual first-failure-skips contract deliberately:
            // everything this prefix does is idempotent (set membership plus a single fill
            // write the original would repeat with identical values), so falling through to
            // the original is safe and keeps THIS bar correct on the breaking call too.
            return true;
        }
    }

    // LeanPool respawn calls Initialize on the recycled EnemyStat; force-write so the bar is
    // correct on the enemy's first visible frame regardless of the flush phase.
    private static void InitializePostfix(EnemyStat __instance)
    {
        if (_hbBroken || !_hbEnabled.Value)
        {
            return;
        }
        try
        {
            HbWriteBar(__instance);
            HbDirty.Remove(__instance);
        }
        catch (Exception ex)
        {
            _hbBroken = true;
            PerfCore.Log.LogError(HbPatchName + " (Initialize) failed, reverting to vanilla: " + ex);
            HbDrainPending();
        }
    }

    private static void HbFlush()
    {
        // The ENTIRE body sits inside the try: if any part of the flush ever threw outside
        // it, PerfCore would kill this hook while the prefix kept deferring writes - bars
        // would freeze forever. This way any failure sets _hbBroken and the prefix reverts
        // to vanilla immediately.
        try
        {
            // Keep draining even if the user toggles Enabled off mid-session, so bars marked
            // dirty before the toggle still land on their final value.
            if (HbDirty.Count == 0)
            {
                return;
            }
            // unscaledTime so bars keep settling while timeScale is 0 (pause menus), matching
            // vanilla's timescale-independent immediate writes.
            float now = Time.unscaledTime;
            if (now < _hbNextFlush)
            {
                return;
            }
            float hz = _hbUpdateHz.Value;
            if (hz < 1f)
            {
                hz = 1f;
            }
            _hbNextFlush = now + 1f / hz;
            // Snapshot into a reused buffer: HbWriteBar can never mutate the set, but keeping
            // iteration and mutation phases separate makes that invariant structural.
            HbFlushBuffer.Clear();
            foreach (EnemyStat stat in HbDirty)
            {
                HbFlushBuffer.Add(stat);
            }
            HbDirty.Clear();
            for (int i = 0; i < HbFlushBuffer.Count; i++)
            {
                EnemyStat stat = HbFlushBuffer[i];
                if (!stat) // fake-null aware: destroyed with a scene unload
                {
                    continue;
                }
                HbWriteBar(stat);
            }
            HbFlushBuffer.Clear();
        }
        catch (Exception ex)
        {
            _hbBroken = true;
            PerfCore.Log.LogError(HbPatchName + " (flush) failed, reverting to vanilla: " + ex);
            // The buffer still holds this cycle's snapshot (writes are idempotent, so bars
            // already written are simply rewritten); land as many final values as possible.
            HbBestEffortWrite(HbFlushBuffer);
            HbFlushBuffer.Clear();
            HbDrainPending();
        }
    }

    // Teardown drain: land the pending final value of every still-alive bar, then clear.
    // Used on scene unload and runtime failure - pending state is always FLUSHED, never
    // dropped, so the last write of a burst can never be lost to a teardown.
    private static void HbDrainPending()
    {
        if (HbDirty.Count == 0)
        {
            return;
        }
        HbFlushBuffer.Clear();
        foreach (EnemyStat stat in HbDirty)
        {
            HbFlushBuffer.Add(stat);
        }
        HbDirty.Clear();
        HbBestEffortWrite(HbFlushBuffer);
        HbFlushBuffer.Clear();
    }

    // Per-item try/catch: one bad bar (or a broken field ref) must not stop the remaining
    // bars from landing their final values, and teardown paths must never throw.
    private static void HbBestEffortWrite(List<EnemyStat> stats)
    {
        for (int i = 0; i < stats.Count; i++)
        {
            try
            {
                EnemyStat stat = stats[i];
                if (stat) // fake-null aware: skip bars destroyed with their scene
                {
                    HbWriteBar(stat);
                }
            }
            catch
            {
                // ignore: teardown must not throw
            }
        }
    }

    private static void HbOnSceneUnloaded()
    {
        // Bars belonging to the unloaded scene are already destroyed (fake-null, skipped by
        // the drain); persistent bars (the boss-tip / enemy-tip UI reuse EnemyStat and live in
        // the UI scope) get their pending value WRITTEN rather than dropped. Phase resets so
        // the first dirty bar of the next scene flushes promptly.
        HbDrainPending();
        _hbNextFlush = 0f;
    }
}

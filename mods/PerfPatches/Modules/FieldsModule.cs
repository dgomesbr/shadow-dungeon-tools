using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FinkFramework.Runtime.Singleton;
using HarmonyLib;
using Lean.Pool;
using UnityEngine;

namespace PerfPatches;

/// <summary>
/// Ground-field / temp-hitbox patches.
///
/// EmptyColVirtualHitbox (default ON, behavior-preserving): ~11 spawner classes (~14 skill
/// archetypes: SK_Field + its three feeders, SK_Zone, SK_Strom, SK_BloodPool, SK_DropArrowRain,
/// SK_EXP_*, SK_FSQ_SonB, SK_Pen, SK_SelfPen, SK_Zone_Comp) tick by LeanPool-spawning the
/// SKPB.EmptyCol prefab, enabling its CircleCollider2D for ~0.1s and dispatching damage from
/// OnTriggerEnter2D with two GetComponent calls per hit (EmptyCOL.cs:107-210). Every tick pays
/// broadphase insert/remove + contact-pair maintenance against every enemy collider in radius
/// for ~5 fixed steps. This patch never enables the collider: SetStart is prefix-skipped, and a
/// postfix on EmptyCOL.Update runs one Physics2D.OverlapCircle per frame over the same layer
/// mask, tracks contact episodes (exact trigger-ENTER semantics), and dispatches a VERBATIM port
/// of OnTriggerEnter2D with the GetComponent calls replaced by a collider->component cache.
/// Covers all spawners at the EmptyCOL level with three small patches.
///
/// GroundImmunityFix (default OFF, opt-in gameplay change): SK_Field.Fashe contains a dead
/// store `IsGround = true; IsGround = false;` (SK_Field.cs:231-232), so the NoGround/CPNoGround
/// immunity branches in EmptyCOL (:131-141, :153-163) are dead code as shipped. Opting in makes
/// SK_Field-originated ticks count as ground hits again (a player BUFF: NoGround players take
/// less damage from enemy ground fields) and fixes the latent NRE at EmptyCOL.cs:155
/// (component.peo.pl is null for companions; CPNoGround is read from the PlayerManager
/// singleton instead). Requires EmptyColVirtualHitbox.
///
/// VirtualFieldTick from the design (skipping the pooled GameObject entirely for stationary
/// tickers) is deliberately NOT shipped: with the collider/trigger/GetComponent cost gone, the
/// residual LeanPool activate/deactivate churn (~12-30 cycles/s) is minor, and the patch would
/// duplicate four spawner Fashe bodies (silent divergence risk on game updates) for that
/// marginal win.
///
/// EmptyCOL_BF (FootCOL-only buff variant used by SK_BloodPool/SK_Orb_Aura/SK_Totem) has the
/// same anti-pattern but is a different class; not covered here (documented follow-up).
/// </summary>
internal static class FieldsModule
{
    private const string PatchName = "EmptyColVirtualHitbox";
    private const string GroundFixName = "GroundImmunityFix";

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<bool> _groundFix;

    private static bool _broken;          // runtime failure -> future activations use vanilla
    private static bool _groundFixActive;
    private static bool _tagChecked;

    // Tags with known gameplay reactions; if the EmptyCol prefab itself carried one of these,
    // keeping its collider disabled could suppress interactions we did not port. Verified today
    // that no script reacts to being touched by EmptyCol, but assert at runtime anyway.
    private static readonly string[] ReactiveTags =
    {
        "BodyCOL", "FootCOL", "Break", "ZoneSK", "DoomBall", "blockFLY", "blockWALL", "Player"
    };

    // ---- per-instance activation state --------------------------------------------------
    // Keyed by component reference: pooled EmptyCOL instances are disabled-not-destroyed, so
    // the same component object cycles forever and its State (and inner HashSet) is allocated
    // once and reused. Cleared on scene unload (instances are scene-local).
    private sealed class ColState
    {
        public bool Active;
        public bool Scanned;                       // at least one scan done this activation
        public float ScanAccum;                    // time since last scan (physics-step paced)
        public float LocalRadius;                  // pre-scale radius; scale applied per scan
        public Vector2 RawOffset;                  // collider offset in local space
        public float Radius;
        public Vector2 LocalOffset;                // collider offset, scaled (added rotated per scan)
        public bool OwnBodyGeneratesContacts;      // own Rigidbody2D satisfies Unity's pair rule alone
        public bool IsGroundField;                 // set by the GroundImmunityFix Fashe port
        public ContactFilter2D Filter;
        // Colliders currently INSIDE the hitbox. OnTriggerEnter2D fires once per contact episode,
        // not once per activation: a target that leaves and re-enters within the tick gets a
        // second hit in vanilla. Keeping "inside" state (and dropping colliders that are absent
        // from a scan) reproduces enter semantics instead of permanently deduping.
        public readonly HashSet<Collider2D> Inside = new HashSet<Collider2D>();
        public readonly HashSet<Collider2D> Seen = new HashSet<Collider2D>();
    }

    private static readonly Dictionary<EmptyCOL, ColState> States = new Dictionary<EmptyCOL, ColState>(64);

    // collider -> (tag kind, component) identity cache. Tag and component identity are
    // immutable for a collider's lifetime (pooled enemies keep collider+BodyCOL/FootCOL and
    // re-resolve their .peo in OnEnable, which we read live per hit) — only identity is cached.
    private const byte KindNone = 0, KindBody = 1, KindFoot = 2, KindBreak = 3;

    private struct CachedTarget
    {
        public byte Kind;
        public Component Comp;
    }

    private static readonly Dictionary<Collider2D, CachedTarget> TargetCache = new Dictionary<Collider2D, CachedTarget>(256);

    private static Collider2D[] _buf = new Collider2D[128];

    internal static void Init(ConfigFile config, Harmony harmony)
    {
        _enabled = config.Bind(PatchName, "Enabled", false,
            "OPT-IN, MEASURE BEFORE TRUSTING - this is the only patch in the suite that touches " +
            "damage delivery. Replaces the spawned-collider tick of ground fields/zones/storms " +
            "with a direct Physics2D.OverlapCircle plus a verbatim port of the game's own hit " +
            "dispatch and a collider->component cache. The pooled EmptyCol hitbox's collider is " +
            "never enabled, so the engine pays no broadphase insert/remove or contact-pair " +
            "maintenance per tick and the per-hit GetComponent calls become dictionary lookups. " +
            "Same victims, same damage calls in the same order, same 50% FX rolls; contact-episode " +
            "tracking reproduces trigger-ENTER semantics (leave and re-enter within a tick hits " +
            "again, like vanilla), and scans are paced to the physics step so hit counts stay " +
            "frame-rate independent. Honest caveat: it swaps a handful of fixed-step trigger " +
            "evaluations for physics-paced overlap queries, so the net gain is build-dependent and " +
            "UNMEASURED - benchmark it with Shift+F8 on your own machine before leaving it on. " +
            "Fail-soft: any runtime error reverts future ticks to vanilla (the in-flight tick is " +
            "lost).");

        _groundFix = config.Bind(GroundFixName, "Enabled", false,
            "OPT-IN, GAMEPLAY-VISIBLE (player buff). Restores the intent of the IsGround dead " +
            "store in SK_Field.Fashe (SK_Field.cs:231-232): enemy-cast ground-field ticks respect " +
            "the NoGround/CPNoGround immunity stats again, and the latent companion NRE in that " +
            "vanilla branch is fixed (CPNoGround read from the PlayerManager singleton). Only " +
            "SK_Field-originated ticks are treated as ground (all other spawners deliberately " +
            "pass false). Requires " + PatchName + ".");

        if (!_enabled.Value)
        {
            if (_groundFix.Value)
            {
                PerfCore.Log.LogWarning(GroundFixName + " skipped: requires " + PatchName + " to be enabled.");
            }
            return;
        }

        try
        {
            var setStart = AccessTools.DeclaredMethod(typeof(EmptyCOL), nameof(EmptyCOL.SetStart));
            var update = AccessTools.DeclaredMethod(typeof(EmptyCOL), "Update");
            var onEnable = AccessTools.DeclaredMethod(typeof(EmptyCOL), "OnEnable");
            if (setStart == null || update == null || onEnable == null)
            {
                throw new MissingMethodException("EmptyCOL.SetStart/Update/OnEnable not found");
            }
            // Sanity-check every member the dispatch port touches so a game update that
            // reshapes the damage path fails at Init instead of mid-combat.
            if (AccessTools.DeclaredMethod(typeof(People), nameof(People.EM_Set)) == null ||
                AccessTools.DeclaredMethod(typeof(People), nameof(People.PL_Set)) == null ||
                AccessTools.DeclaredMethod(typeof(People), nameof(People.CP_Set)) == null)
            {
                throw new MissingMethodException("People.EM_Set/PL_Set/CP_Set not found");
            }
            if (AccessTools.DeclaredMethod(typeof(BreakOBJ), nameof(BreakOBJ.Break)) == null)
            {
                throw new MissingMethodException("BreakOBJ.Break not found");
            }

            harmony.Patch(setStart, prefix: new HarmonyMethod(typeof(FieldsModule), nameof(SetStartPrefix)));
            // PREFIX, not postfix: vanilla Update calls LeanPool.Despawn (EmptyCOL.cs:56-62) which
            // deactivates the object synchronously, so a postfix would be skipped on the frame the
            // hitbox dies. With lifeTime = 0.1s that silently dropped the ENTIRE tick's damage
            // whenever a frame took >= 50ms - exactly the low-fps case this patch exists for.
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(FieldsModule), nameof(UpdatePrefix)));
            harmony.Patch(onEnable, postfix: new HarmonyMethod(typeof(FieldsModule), nameof(OnEnablePostfix)));
            PerfCore.OnSceneUnloaded(PatchName, ClearCaches);
            PerfCore.Log.LogInfo(PatchName + " installed");
        }
        catch (Exception ex)
        {
            PerfCore.Log.LogWarning(PatchName + " not installed: " + ex.Message);
            return;
        }

        if (_groundFix.Value)
        {
            try
            {
                var fashe = AccessTools.DeclaredMethod(typeof(SK_Field), nameof(SK_Field.Fashe));
                if (fashe == null)
                {
                    throw new MissingMethodException("SK_Field.Fashe not found");
                }
                harmony.Patch(fashe, prefix: new HarmonyMethod(typeof(FieldsModule), nameof(FashePrefix)));
                _groundFixActive = true;
                PerfCore.Log.LogInfo(GroundFixName + " installed (SK_Field ticks count as ground)");
            }
            catch (Exception ex)
            {
                PerfCore.Log.LogWarning(GroundFixName + " not installed: " + ex.Message);
            }
        }
    }

    private static void ClearCaches()
    {
        States.Clear();
        TargetCache.Clear();
        Array.Clear(_buf, 0, _buf.Length);
    }

    private static ColState GetState(EmptyCOL col)
    {
        if (!States.TryGetValue(col, out ColState st))
        {
            st = new ColState();
            States[col] = st;
        }
        return st;
    }

    // ---- EmptyCOL.OnEnable postfix: reset per-activation state --------------------------
    private static void OnEnablePostfix(EmptyCOL __instance)
    {
        if (_broken)
        {
            return;
        }
        try
        {
            ColState st = GetState(__instance);
            st.Active = false;
            st.IsGroundField = false;
            st.Inside.Clear();
            st.Seen.Clear();
        }
        catch (Exception ex)
        {
            _broken = true;
            PerfCore.Log.LogError(PatchName + " OnEnable failed, reverting to vanilla: " + ex);
        }
    }

    // ---- EmptyCOL.SetStart prefix: record radius, never enable the collider --------------
    private static bool SetStartPrefix(EmptyCOL __instance)
    {
        if (_broken)
        {
            return true; // vanilla: enables the collider, trigger path takes over
        }
        try
        {
            // Vanilla guards (EmptyCOL.cs:89-102): no collider or no PlayerManager -> dud
            // activation (initialized already true, so it is never retried), object just expires.
            if (!__instance.col)
            {
                return false;
            }
            PlayerManager pl = SingletonMonoScope<PlayerManager>.Instance;
            if (!pl)
            {
                return false;
            }

            if (!_tagChecked)
            {
                _tagChecked = true;
                string tag = __instance.gameObject.tag;
                for (int i = 0; i < ReactiveTags.Length; i++)
                {
                    if (tag == ReactiveTags[i])
                    {
                        _broken = true;
                        PerfCore.Log.LogWarning(PatchName + " self-disabled: EmptyCol prefab carries reactive tag '" +
                                                tag + "' whose touch interactions the port does not cover.");
                        return true;
                    }
                }
            }

            ColState st = GetState(__instance);
            // Vanilla assigns col.radius, a LOCAL-space value the engine scales by the transform
            // and centers at (position + rotated offset). Physics2D.OverlapCircle takes a WORLD
            // radius and an explicit center, so both have to be converted or the hit area is wrong
            // on any prefab with non-unity scale or a non-zero collider offset.
            st.LocalRadius = __instance.size + __instance.size * pl.EXP_Range / 100f; // EmptyCOL.cs:103
            st.RawOffset = __instance.col.offset;
            ApplyTransformScale(__instance, st); // re-applied per scan: scale can animate
            // Keep the collider's own field truthful even though it stays disabled, so anything
            // else inspecting it (debug tools, a later vanilla SetStart after _broken) agrees.
            __instance.col.radius = st.LocalRadius;

            // Unity 2D generates trigger contacts only for pairs where at least one body is
            // DYNAMIC, or a kinematic body with useFullKinematicContacts. Model the hitbox's own
            // body here; the per-hit check completes the rule.
            // attachedRigidbody, not GetComponent: Unity attaches a collider to the body on its own
            // object OR the nearest ancestor, and one spawner (SK_FSQ_SonB) parents the hitbox.
            Rigidbody2D ownBody = __instance.col.attachedRigidbody;
            st.OwnBodyGeneratesContacts = ownBody != null &&
                (ownBody.bodyType == RigidbodyType2D.Dynamic ||
                 (ownBody.bodyType == RigidbodyType2D.Kinematic && ownBody.useFullKinematicContacts));
            // Same candidate set as the trigger path: the layer collision matrix row for the
            // hitbox's layer, triggers included (trigger events fire for trigger and solid
            // colliders alike as long as the matrix allows the pair).
            var filter = default(ContactFilter2D);
            filter.useTriggers = true;
            filter.SetLayerMask(Physics2D.GetLayerCollisionMask(__instance.gameObject.layer));
            st.Filter = filter;
            st.Active = true;
            st.Scanned = false;
            st.ScanAccum = 0f;
            st.Inside.Clear();
            st.Seen.Clear();
            return false; // col stays disabled (OnEnable disabled it, EmptyCOL.cs:43-46)
        }
        catch (Exception ex)
        {
            _broken = true;
            PerfCore.Log.LogError(PatchName + " SetStart failed, reverting to vanilla: " + ex);
            return true;
        }
    }

    // ---- EmptyCOL.Update prefix: the virtual trigger scan ---------------------------------
    // Runs BEFORE the vanilla body so the scan always happens while the hitbox is still alive
    // (vanilla despawns inside Update). Scans are paced to the PHYSICS step, not the frame
    // rate: that matches the cadence at which vanilla's OnTriggerEnter2D could fire, keeps hit
    // counts frame-rate independent, and bounds the cost on high-refresh displays. One scan per
    // activation is guaranteed even if the whole lifetime fits inside a single long frame.
    // Position is re-read per scan, so the moving hitboxes (SK_Pen/SK_SelfPen) still track.
    // Unity scales a CircleCollider2D by the larger absolute axis of the lossy scale and offsets
    // it in local space; convert both into the world values Physics2D.OverlapCircle wants.
    private static void ApplyTransformScale(EmptyCOL inst, ColState st)
    {
        Vector3 scale = inst.transform.lossyScale;
        float sx = scale.x < 0f ? -scale.x : scale.x;
        float sy = scale.y < 0f ? -scale.y : scale.y;
        st.Radius = st.LocalRadius * (sx > sy ? sx : sy);
        st.LocalOffset = new Vector2(st.RawOffset.x * scale.x, st.RawOffset.y * scale.y);
    }

    private static bool _scanning; // re-entrancy guard: _buf is shared across all instances

    private static void UpdatePrefix(EmptyCOL __instance)
    {
        if (_broken || _scanning || !__instance.isActiveAndEnabled)
        {
            return;
        }
        ColState st;
        if (!States.TryGetValue(__instance, out st) || !st.Active)
        {
            return;
        }
        float step = Time.fixedDeltaTime;
        st.ScanAccum += Time.deltaTime;
        if (st.Scanned && st.ScanAccum < step)
        {
            return;
        }
        st.ScanAccum = 0f;
        st.Scanned = true;
        try
        {
            _scanning = true;
            // Re-derive world radius/offset every scan: a parented hitbox inherits an animated
            // parent scale, which the engine would track continuously.
            ApplyTransformScale(__instance, st);
            // Center in world space, mirroring how the engine places the circle collider.
            Vector2 pos = __instance.transform.position;
            if (st.LocalOffset.x != 0f || st.LocalOffset.y != 0f)
            {
                pos += (Vector2)(__instance.transform.rotation * st.LocalOffset);
            }
            int n;
            while (true)
            {
                n = Physics2D.OverlapCircle(pos, st.Radius, st.Filter, _buf);
                if (n < _buf.Length)
                {
                    break;
                }
                _buf = new Collider2D[_buf.Length * 2]; // warmup-only growth, then rescan
            }
            st.Seen.Clear();
            for (int i = 0; i < n; i++)
            {
                Collider2D hit = _buf[i];
                _buf[i] = null;
                if (!hit)
                {
                    continue;
                }
                // Complete Unity 2D contact rule: a trigger event needs at least one DYNAMIC body
                // in the pair, or a kinematic body with useFullKinematicContacts. Without this the
                // overlap is a superset of the real trigger pairs and would damage targets vanilla
                // never touches (e.g. static-collider breakables).
                if (!st.OwnBodyGeneratesContacts)
                {
                    Rigidbody2D other = hit.attachedRigidbody;
                    if (other == null ||
                        !(other.bodyType == RigidbodyType2D.Dynamic ||
                          (other.bodyType == RigidbodyType2D.Kinematic && other.useFullKinematicContacts)))
                    {
                        continue;
                    }
                }
                st.Seen.Add(hit);
                if (!st.Inside.Add(hit))
                {
                    continue; // still inside from a previous frame: no new ENTER event
                }
                DispatchHit(__instance, st, hit);
            }
            // Anything that was inside and is no longer overlapping has EXITED; forget it so a
            // re-entry within this activation fires again, exactly like OnTriggerEnter2D.
            if (st.Inside.Count != st.Seen.Count)
            {
                st.Inside.IntersectWith(st.Seen);
            }
        }
        catch (Exception ex)
        {
            _broken = true;
            PerfCore.Log.LogError(PatchName + " scan failed, reverting to vanilla: " + ex);
        }
        finally
        {
            _scanning = false;
        }
    }

    // ---- verbatim port of EmptyCOL.OnTriggerEnter2D (EmptyCOL.cs:107-210) -----------------
    // Only deltas: GetComponent per hit -> TargetCache; a null-peo guard (vanilla would NRE and
    // abort the same hit inside Unity's callback handler); IsGround comes from the activation
    // state when GroundImmunityFix is on (vanilla field is a constant false as shipped); the
    // CPNoGround read uses the PlayerManager singleton (vanilla's component.peo.pl is null for
    // companions - the branch is only reachable with the fix enabled).
    private static void DispatchHit(EmptyCOL __instance, ColState st, Collider2D collision)
    {
        CachedTarget target;
        if (!TargetCache.TryGetValue(collision, out target) || (target.Kind != KindNone && !target.Comp))
        {
            target.Kind = KindNone;
            target.Comp = null;
            if (collision.CompareTag("BodyCOL"))
            {
                target.Kind = KindBody;
                target.Comp = collision.GetComponent<BodyCOL>();
            }
            else if (collision.CompareTag("FootCOL"))
            {
                target.Kind = KindFoot;
                target.Comp = collision.GetComponent<FootCOL>();
            }
            else if (collision.CompareTag("Break"))
            {
                target.Kind = KindBreak;
                target.Comp = collision.GetComponent<BreakOBJ>();
            }
            TargetCache[collision] = target;
        }

        bool isGround = _groundFixActive ? st.IsGroundField : __instance.IsGround;
        Dicform dic = __instance.dic;
        GameObject fx = __instance.FX;
        float dotMulti = __instance.DotMulti;

        if (__instance.Body)
        {
            if (target.Kind == KindBody)
            {
                BodyCOL component = (BodyCOL)target.Comp;
                if ((bool)dic && (bool)dic.sp && (bool)component && component.peo != null)
                {
                    People peo = component.peo;
                    if (dic.sp.ZY)
                    {
                        if (peo.CharacterType == 2 && peo.em.IsAlive && !peo.em.IsJump && !peo.em.IsYS)
                        {
                            peo.EM_Set(dic.sp, dotMulti, dic.SubType, dic.sp.Dot_Infect, dic.sp.Dot_Infect_Layer, dic.UPDamage);
                            if ((bool)fx && UnityEngine.Random.Range(0, 100) < 50)
                            {
                                LeanPool.Spawn(fx, peo.em.yao.transform.position, Quaternion.identity, peo.em.yao.transform);
                            }
                        }
                    }
                    else
                    {
                        if (peo.CharacterType == 0 && peo.pl.IsAlive)
                        {
                            if (!isGround || !peo.pl.NoGround)
                            {
                                peo.PL_Set(dic.sp, dic.SubType);
                                if (fx != null && UnityEngine.Random.Range(0, 100) < 50)
                                {
                                    LeanPool.Spawn(fx, peo.pl.yao.transform.position, Quaternion.identity, peo.pl.yao.transform);
                                }
                            }
                        }
                        if (peo.CharacterType == 1 && peo.cp.IsAlive)
                        {
                            // CPNoGround lives on the player; vanilla's peo.pl is null here.
                            PlayerManager plMgr = SingletonMonoScope<PlayerManager>.Instance;
                            if (!isGround || !(plMgr && plMgr.CPNoGround))
                            {
                                peo.CP_Set(dic.sp, dic.SubType);
                                if (fx != null && UnityEngine.Random.Range(0, 100) < 50)
                                {
                                    LeanPool.Spawn(fx, peo.cp.yao.transform.position, Quaternion.identity, peo.cp.yao.transform);
                                }
                            }
                        }
                    }
                }
            }
        }
        else if (target.Kind == KindFoot)
        {
            FootCOL component2 = (FootCOL)target.Comp;
            if ((bool)dic && (bool)dic.sp && (bool)component2 && component2.peo != null)
            {
                People peo2 = component2.peo;
                if (dic.sp.ZY)
                {
                    if (peo2.CharacterType == 2 && peo2.em.IsAlive && !peo2.em.IsJump && !peo2.em.IsYS)
                    {
                        peo2.EM_Set(dic.sp, dotMulti, dic.SubType, dic.sp.Dot_Infect, dic.sp.Dot_Infect_Layer, dic.UPDamage);
                    }
                }
                else
                {
                    // NO IsGround/NoGround gating here, deliberately: vanilla's FootCOL branch
                    // (EmptyCOL.cs:189-199) has none - the immunity branches exist only in the
                    // Body path (:131-141, :153-163). GroundImmunityFix restores the intent of
                    // SK_Field's dead store; it must not INVENT immunity where even a repaired
                    // vanilla would deal damage.
                    if (peo2.CharacterType == 0 && peo2.pl.IsAlive)
                    {
                        peo2.PL_Set(dic.sp, dic.SubType);
                    }
                    if (peo2.CharacterType == 1 && peo2.cp.IsAlive)
                    {
                        peo2.CP_Set(dic.sp, dic.SubType);
                    }
                }
            }
        }

        if (target.Kind == KindBreak)
        {
            BreakOBJ component3 = (BreakOBJ)target.Comp;
            if ((bool)component3)
            {
                component3.Break();
            }
        }
    }

    // ---- GroundImmunityFix: SK_Field.Fashe port (SK_Field.cs:220-233) ---------------------
    // Identical to vanilla except the dead store becomes a real per-activation ground flag.
    // Field order preserved (sp before SetCount before SubType).
    private static bool _fasheBroken;

    private static bool FashePrefix(SK_Field __instance)
    {
        if (_broken || _fasheBroken)
        {
            return true; // virtual hitbox reverted or port failed -> run vanilla
        }
        try
        {
            EmptyCOL component = LeanPool.Spawn(SingletonMonoScope<GameDataManager>.Instance.SKPB.EmptyCol,
                __instance.transform.position, Quaternion.identity).GetComponent<EmptyCOL>();
            Dicform component2 = component.GetComponent<Dicform>();
            component2.sp = __instance.dic.sp;
            component2.SetCount(__instance.dic.sp.ZY);
            component2.SubType = __instance.dic.SubType;
            component.size = __instance.size;
            component.Body = __instance.Body;
            component.DotMulti = __instance.DotMulti;
            component.lifeTime = 0.1f;
            component.IsGround = false;             // vanilla net result of the dead store
            GetState(component).IsGroundField = true; // the restored intent
            return false;
        }
        catch (Exception ex)
        {
            _fasheBroken = true;
            _groundFixActive = false;
            PerfCore.Log.LogError(GroundFixName + " failed, future Fashe calls use vanilla: " + ex);
            // Do NOT fall through to vanilla for THIS call: the spawn may already have happened
            // and vanilla would double-spawn. Losing one 0.5s tick is the safer failure.
            return false;
        }
    }
}

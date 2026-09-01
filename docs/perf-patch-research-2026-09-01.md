# Shadow Dungeon — Performance Patch Research (2026-09-01)

Source: Max's Steam thread (588436648123960166) verified claim-by-claim against the decompiled
code by 5 analysis agents; full designs in the workflow output (journal wf_033a1a4a-d5c).

## Verdicts on Max's claims
- Fields spawn a pooled collider hitbox every 0.5s tick, despawned 0.1s later: CONFIRMED
  (SK_Field.cs:104-114, 220-233; ~14 skill archetypes flow through SKPB.EmptyCol; EmptyCOL_BF
  is a second variant). Per-hit GetComponent chains in EmptyCOL.OnTriggerEnter2D: CONFIRMED.
- IsGround ground-immunity bypass: PARTLY — SK_Field sets IsGround=true then immediately false
  (dead store, SK_Field.cs:231-232). But IsGround only gates the ENEMY->player branch, so
  fixing it would BUFF players with NoGround stats (and exposes a latent NRE for companions).
- Child projectiles 33/s per parent: CONFIRMED (SK_FlyBall.cs:370-375 -> SetZiDan :1400).
- Homing rescan+sort: CONFIRMED — 4 classes (SK_FlyBall/FlyFollow/FlyA .Refresh, SK_FlySowrdFSQ
  .RefreshB) List.Sort with sqrt-per-comparison; ~341k sqrt/s at 100 projectiles.
- Enemy AI 4Hz scans: CONFIRMED (EnemyBrain.Tick :92-125; ScanCompanions :500-549 overlap +
  GetComponent + closure sort). LOS raycasts: CONFIRMED (~200/s at 50 enemies) — but the far
  bigger cost is PLAYER-side: PlayerManager companion-follow + autolock raycast every frame
  (~3000-4500 raycasts/s at 50 enemies) — Max missed the biggest one.
- Vector2.Distance sqrt claim: REFUTED as posted (cited region has no distance math; per-frame
  sqrts are nanoseconds and their fields are consumed as linear distances everywhere).
- Damage text list scan: CONFIRMED (TryMergeCombatText :140-167 reverse scan per hit).
- Health bars update per damage event: CONFIRMED (EnemyStat.RefreshBar :61-74).
- "Pooling doesn't help" / LeanPool expensive: mostly REFUTED — LeanPool reuse path is cheap,
  no allocs; the game already uses OverlapCircleNonAlloc EVERYWHERE (Max's alloc claims are
  stale; a dev perf pass clearly already happened). Residual: LayerMask.GetMask string lookups
  at 72 call sites; guaranteed-empty IPoolable GetComponents per spawn/despawn.
- No culling/LOD system exists; enemies tick at full rate regardless of distance: CONFIRMED.

## Patch menu (17 designs, full details in workflow journal)
SAFE SET (behavior-preserving, recommended default-on):
1. EmptyColVirtualHitbox [L] — replace the spawned-collider tick with direct OverlapCircle +
   verbatim damage dispatch + collider->component cache. Biggest win for field builds.
2. HomingSortRemoval [S] — argmin scan instead of List.Sort in 4 Refresh methods (~99% of
   comparator work + ~1700 allocs/s removed in projectile storms).
3. WallRaycastFrameCache [S] — same-frame LOS memo by enemy id; kills 40-60% of the
   ~4500 raycasts/s player-side storm.
4. CollectEnemiesInRangeReimpl [M] — O(n^2) Contains -> HashSet; enables #3 on autolock path.
5. JSQTickOptimizer [L] — reimplement PlayerManager.JSQ 0.2s tick (dedup raycasts, precomputed
   sort keys, no closures). Highest maintenance surface; gate with body-hash check.
6. CompanionScanOverhaul [M] — registry + no-sort ScanCompanions (kills 100% of the cost when
   no companions; big for summon builds).
7. AITickStagger [S] — dephase the synchronized 4Hz AI burst (~92% spike-height reduction).
8. SCT_FastMerge [M] — dictionary merge for combat text (>95% of merge CPU).
9. HealthBar_Throttle [S] — coalesce fillAmount writes to 15Hz (~75-90% of bar cost).
10. LayerMaskGetMaskMemo [S] — memoize GetMask(single name) engine-wide (72 sites).
11. LegacyScanMicroFix [S] — towers/brainless enemies scan path.
12. PoolNotificationNone [S] — skip guaranteed-empty IPoolable notifications.
13. FrameTimeOverlay+Benchmark [M] — F8 overlay (id 49314) + 60s CSV benchmark protocol
    (3 scenarios x 3 runs, medians; gen0/min for alloc patches, p99 for spike patches).

OPT-IN SET (timing/gameplay-adjacent, default off):
14. CompanionFollowScanThrottle [S] — 0.1s interval on the every-frame follow scan (~83% of
    the single largest per-frame physics chain).
15. AutoLockRefreshInterval [S] — 20Hz -> 10Hz autolock (halves autolock raycasts).
16. LOSRaycastCache [M] — movement-invalidated LOS TTL (60-90% of AI raycasts).
17. OffscreenAILOD [M] — far enemies tick at 10Hz (~30-50% of AI cost on dense floors).
18. ChildSpawnGovernor [M] — cap child-projectile ticks/s with UPDamage compensation (fewer
    hit events = fewer procs; explicitly gameplay-visible).
19. EngineTweaks [M] — probe+log Physics2D/GC state; opt-in velocity/positionIterations cuts,
    autoSyncTransforms=false (potentially the single biggest win if currently true),
    fixedDeltaTime scale, incremental GC time slice.

REJECTED (with reasons): callbacksOnDisable=false (breaks trigger-exit cleanup on pooled
despawn), NameToLayer patch (extern icall), Distance->sqrMagnitude sweep (units consumed
linearly), UIManager.BuildPanelKey memo (generic patching unreliable + panels mutate
mid-frame), LeanPool internals rewrite, TMP migration (asset surgery), shared health bar
canvas (breaks per-enemy alpha logic), re-optimizing DamgeTextManager pooling (already tight),
forcing incremental GC (build-time flag).

Estimated combined effect (dense Mijing floor, 50+ enemies, 100+ projectiles, summon build):
player-side physics query load -70-85%, AI spike frames -90%, projectile targeting cost -95%+,
combat-text/health-bar UI cost -75-95%, plus measured-not-guessed engine wins. The overlay
(#13) turns each into a before/after number.

Compat notes: composes with VfxReducer (different surfaces; Harmony postfix stacking on
LeanPool.Spawn is safe); IMGUI id 49314 reserved for the overlay; F8 hotkey free again after
SummonCounter retirement.

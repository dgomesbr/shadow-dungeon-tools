# Performance Patches (`custom.perfpatches`)

Reduces CPU work in Shadow Dungeon's hottest code paths — the physics/raycast storms, projectile
targeting, enemy AI scheduling, and combat-UI churn identified in the community performance
analysis. Every patch is independently toggleable, and each one either preserves behaviour
exactly or is opt-in and off by default.

Requires BepInEx 5.4.23.5 x64. Config: `BepInEx\config\custom.perfpatches.cfg` (created on first
run). No hotkeys: both the frame-time overlay and the 60-second benchmark capture are driven
from the floating **Mods** menu docked to the right screen border (rows *FPS Overlay* and
*Run Benchmark*).

## How to measure (do this before judging anything)

1. In a dungeon, click **FPS Overlay** in the Mods menu (avg FPS, 1% low, frame ms, GC gen0/min).
2. Pick a repeatable spot — the *Corrupted Realm* row (MijingSelector) is ideal.
3. Click **Run Benchmark** for a 60-second capture. The row then counts down (`Bench 42s left`);
   clicking it again after the first 3 seconds cancels the run. It writes
   `BepInEx\plugins\PerfBench\<timestamp>.csv` (per-frame ms) plus a summary line in the log
   (avg / median / p95 / p99 ms, 1% low FPS, GC counts).
4. Toggle **one** config entry, restart, repeat the same scenario, compare medians and p99.

Keep overlay visibility identical across runs — the overlay itself allocates a little while
visible, and the benchmark deliberately does *not* force it on so its GC numbers stay clean.

## Patches, by module

### PlayerPhysics — the biggest win on dense floors
The game raycasts every nearby enemy **every frame** for companion-follow and 20×/s for
auto-lock (~3,000–4,500 raycasts/second with 50 enemies).

| Patch | Default | What it does |
|---|---|---|
| `WallRaycastFrameCache` | **on** | Memoizes line-of-sight raycasts per enemy for the current frame *and* physics step. Exact: physics bodies can't move between queries within one step. |
| `CollectEnemiesInRangeReimpl` | **on** | Replaces an O(n²) duplicate check with a hash set and routes line-of-sight through the frame cache. Identical results and ordering. |
| `JSQTickOptimizer` | **on** | Reimplements the player's 0.2 s housekeeping tick: no per-frame closure allocations, precomputed squared-distance sort keys, deduplicated raycasts. Guarded by an IL fingerprint of the vanilla method — if a game update changes it, the patch refuses to install and vanilla runs. |
| `CompanionFollowScanThrottle` | off | Caches the every-frame companion-follow scan for `ScanInterval` seconds (try 0.1). Removes ~83 % of that scan chain; the follow anchor reacts up to one interval later. |
| `AutoLockRefreshInterval` | off | Lowers auto-lock retargeting from 20 Hz. Halves auto-lock raycasts; target switching gets slightly slower. |

### Fields — ground-effect hitboxes
Ground fields spawn a pooled collider object every 0.5 s tick purely to find targets, then
despawn it 0.1 s later; ~14 skill archetypes do this.

| Patch | Default | What it does |
|---|---|---|
| `EmptyColVirtualHitbox` | **off** | Keeps the object but never enables its collider — damage is dispatched from a direct overlap query with a verbatim port of the game's own hit logic (both player-side and enemy-side branches), plus a collider→component cache replacing per-hit `GetComponent` calls. Scans are paced to the physics step, so hit counts stay frame-rate independent. **Ships off**: it is the only patch that touches damage delivery, and review showed the net gain is build-dependent and unmeasured — benchmark it yourself before leaving it on. |
| `GroundImmunityFix` | off | Repairs a dead store in the game's field code so enemy ground fields respect your ground-immunity stat. This is a **player buff**, not a nerf — off by default because it changes balance. |

### Projectiles

| Patch | Default | What it does |
|---|---|---|
| `HomingSortRemoval` | **on** | Homing projectiles sorted the whole enemy list with a square root per comparison (~341 k sqrt/s at 100 projectiles). Replaced with a single nearest-target scan; the sword variant keeps an exact 5-nearest prefix because the game samples randomly from it. |
| `ChildSpawnGovernor` | off | Caps how fast *your* projectiles emit child projectiles, with damage compensation. Gameplay-visible (fewer hit events → fewer on-hit procs). Enemy projectiles are never throttled, so it can't make the game easier. Read the config notes if you play a Doom-orb build. |

### EnemyAi

| Patch | Default | What it does |
|---|---|---|
| `CompanionScanOverhaul` | **on** | Enemies scanned for companions 4×/s each with a physics query, `GetComponent` calls and an allocating sort. Now served from a live companion registry with a nearest-scan; when you have no companions the query is skipped entirely. |
| `AITickStagger` | **on** | All enemies from one spawner used to think in the *same* frame every 0.25 s. Their decision phases are now spread deterministically — same total work, ~92 % lower spike height. |
| `LegacyScanMicroFix` | **on** | Same treatment for towers and brainless enemies (cached layer masks, no sort delegate). |

### EnemyAiLod (both opt-in)

| Patch | Default | What it does |
|---|---|---|
| `LOSRaycastCache` | off | Caches enemy line-of-sight for `CacheTTL` (default 0.6 s; must exceed the 0.25 s decision cadence to ever hit). Narrow benefit: only helps standoffs where enemy and target both stand still. |
| `OffscreenAILOD` | off | Enemies beyond `ThrottleDistance` think every Nth frame, with skipped time accumulated so no internal timer runs slow. Costs up to a few frames of reaction latency far from the player. |

### UiFeedback

| Patch | Default | What it does |
|---|---|---|
| `SCT_FastMerge` | **on** | Damage numbers did a full list scan per hit (~80 k comparisons/s at 1,000 hits/s). Replaced with a spatial hash using the game's exact merge window and radius, plus one deferred text format per frame. Render-identical. |
| `HealthBar_Throttle` | **on** | Enemy health bars wrote their fill on every damage event; now coalesced to `UpdateHz` (15). Death, empty and full-heal always write immediately, and bars always land on the exact final value. Cosmetic-only lag of up to 1/15 s mid-combat. |
| `SCT_Budget` | off (inert) | Optional damage-number load shedding: max concurrent texts, minimum damage to display, merge window/distance scaling. Defaults reproduce vanilla exactly. |

### Engine

| Patch | Default | What it does |
|---|---|---|
| `LayerMaskGetMaskMemo` | **on** | The game resolves layer masks by *name* at 72 call sites in hot loops; memoized (the layer table never changes at runtime). |
| `PoolNotificationNone` | **on** | Skips a guaranteed-empty component scan on every pooled spawn and despawn. Self-disables if any mod actually uses the pooling notification interface. |
| `EngineTweaks` | probe **on**, setters off | Always logs the real Physics2D / GC / vsync state at startup and scene load (ground truth for tuning). Opt-in setters: solver iteration counts, `autoSyncTransforms`, fixed timestep, incremental-GC time slice, GC-on-scene-unload. |

`Physics2D.callbacksOnDisable` is deliberately never touched: pooled despawn disables objects,
and ~40 game scripts depend on the trigger-exit callback firing then to purge target lists.

### Overlay
`FrameTimeOverlay` (on, hidden until the *FPS Overlay* menu row is clicked) and the
*Run Benchmark* capture described above.

## Safety model

- Behaviour-preserving patches are on by default; anything that changes timing or gameplay is
  off by default and says so in its config description. One exception is deliberately
  conservative: `EmptyColVirtualHitbox` is behaviour-preserving by construction but ships off
  because it is the only patch on the damage-delivery path and its win is unproven.
- Every patch installs inside its own guard — one failing never stops the others.
- Reimplemented methods fail soft: the first runtime exception skips that single tick (never
  half-applies), logs once, and permanently reverts that patch to vanilla for the session.
- `JSQTickOptimizer` and the save-snapshot-adjacent paths additionally verify an IL fingerprint
  of the vanilla method before patching, so a game update can't silently desync the port.
- Caches are invalidated on scene unload and are aware that pooled objects are disabled rather
  than destroyed.

## Not shipped

Seven additional module files (graphics, loading, memory, save-system, interactables, skill
refresh, misc hot paths) were auto-generated beyond the reviewed scope and are parked in
`plugins/_quarantine-unreviewed/`. They are **not compiled into the DLL** and not registered.
They touch save writing and asset unloading and have had no adversarial review — treat them as
drafts, not features.

## Review status

All eight shipped modules were reviewed line-by-line against the decompiled vanilla bodies by
independent adversarial agents. Findings fixed in this build: line-of-sight cache invalidation
across physics steps, companion-registry hooks converted to finalizers (so a companion can never
become invisible to enemies), auto-lock target validity across pool reuse, O(n) list compaction,
child-spawn damage-compensation rate and scope, incremental-GC property typing, benchmark buffer
sizing, and benchmark GC self-pollution.

The ground-field module needed the most work. Two independent reviewers caught the same blocker:
the overlap scan ran as a *postfix* on the hitbox's `Update`, but the game despawns that object
*inside* `Update` — so on any frame slower than ~50 ms the tick dealt **no damage at all**. The
scan now runs before the vanilla body, is guaranteed at least once per activation, and is paced to
the physics step. Also fixed there: the trigger-eligibility rule now reads the collider's attached
rigidbody (a parented spawner would otherwise have stopped breaking scenery), world radius/offset
are recomputed per scan so animated parent scale tracks, and a re-entrancy guard protects the
shared query buffer.

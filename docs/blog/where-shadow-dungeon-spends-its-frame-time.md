# Where Shadow Dungeon spends its frame time

Max posted a decompiler-assisted teardown of Shadow Dungeon's performance in the Steam
discussions and concluded that the problems were too broad to fix from a BepInEx mod. I went
through his list line by line against the decompiled code, measured my own game, and ended up
disagreeing about the mod part. Most of what he found is patchable. Some of it is already
fixed in the shipped build. And the biggest cost in my capture is something his list doesn't
mention.

Everything below comes from the 1.0.9-era build, decompiled with ILSpy, plus a 60-second
frame-time capture on a level-100 Necromancer running a summon build on a crowded floor.

## What the numbers look like

Averages hide this game's problem completely.

| | |
|---|---|
| frames captured | 2884 in 60.0 s |
| average | 20.8 ms (48 fps) |
| median | 18.1 ms (55 fps) |
| p95 | 36.4 ms |
| p99 | 55.4 ms |
| worst frame | 220 ms |
| 1% low | 12.7 fps |
| gen0 garbage collections | 4 in the whole minute |

The median frame sits just past my 60 Hz vsync cap, so the game is fine most of the time. Then
6.2% of frames run longer than 33 ms, and those frames eat 14.7% of the wall clock. Nine
seconds of every minute goes to stutter, including one 220 ms freeze that you feel as a hitch
rather than a framerate.

Four gen0 collections per minute is nothing. Whatever is wrong here, garbage collection isn't
it, which rules out a whole family of fixes people usually reach for first.

## The cost Max's list misses

The single most expensive thing in my capture happens on the player, not the enemies.

`PlayerManager` runs a companion-follow scan **every frame**: an overlap query for enemies
within 7 units, then a `Physics2D.Raycast` per enemy found to check line of sight. On top of
that, auto-lock retargeting runs 20 times a second and raycasts every enemy in range again,
then raycasts the winner a second time. With 50 enemies on screen that lands somewhere around
3,000 to 4,500 raycasts per second before any enemy AI has done anything.

Two things make it cheap to fix. Physics bodies only move on the fixed step, so two raycasts
against the same enemy inside one frame must return the same answer, which makes a
frame-and-step-keyed cache exact rather than approximate. And the same enemies get raycast
repeatedly by different callers in the same frame, so the cache hits constantly.

`CollectEnemiesInRange` also had an `O(n²)` duplicate check: for every candidate it scanned the
result list it was building. At 50 enemies that's about 1,250 comparisons per call, and the
method gets called every frame by the follow scan, 20 times a second by auto-lock, and again on
slower ticks. A hash set makes it linear.

## Where Max is right

**Ground fields spawn a collider to find their targets.** A field ticks every 0.5 s
(`SK_Field.Update`), and each tick pools a GameObject with a `CircleCollider2D`, inserts it into
the physics broadphase, collects `OnTriggerEnter2D` callbacks for 0.1 s, then despawns it. Around
14 skill archetypes route through that same `EmptyCol` prefab. Every hit then does a tag compare
plus `GetComponent<BodyCOL>()` or `GetComponent<FootCOL>()` before it can apply damage.

**Homing projectiles sort the enemy list with a square root per comparison.** Four classes do
this in their target refresh, using `Vector3.Distance` inside a `List.Sort` comparator, and they
only ever read element zero afterwards. At 100 live projectiles that's roughly 341,000 square
roots per second to answer "which enemy is nearest", plus a delegate allocation per sort.

**Every enemy from a spawner thinks on the same frame.** `EnemyBrain.Tick` runs a companion scan
and a decision every 0.25 s, and because they were all reset together they stay in phase. Fifty
enemies means fifty overlap queries, fifty decisions and up to fifty line-of-sight raycasts
landing in one frame, then fourteen idle frames. That is a periodic spike by construction, and
it matches the shape of my p99.

**Damage numbers scan a list per hit**, and **enemy health bars write their fill on every damage
event**. Both are real, and in a horde build with damage-over-time ticking on everything they
add up.

## Where Max's teardown is out of date

**`OverlapCircleAll` allocations.** There isn't a single call to it in `Assembly-CSharp`. The
game uses `OverlapCircleNonAlloc` with persistent buffers at all 56 of its overlap sites.
Somebody already did an allocation pass on this code. What survives is `LayerMask.GetMask("...")`
being called by name inside hot loops at 72 sites, which allocates a small `params` array and
does a native name lookup each time. Worth memoizing, not worth rewriting anything for.

**"Object pooling doesn't make physics cheaper."** True as stated, but the implication that
LeanPool itself is a cost doesn't hold. Its reuse path is a list operation, a reparent and a
`SetActive`, with no allocation. The one genuinely wasted call is a `GetComponents` scan for
`IPoolable` on every spawn and despawn, and no type in any game assembly implements that
interface, so it always finds nothing.

**`Vector2.Distance` where squared distance would do.** The cited region has no distance maths in
it. The real per-frame calls cost one or two square roots per enemy per frame, and their results
get stored in fields that dozens of call sites read as linear distances. Converting them to
squared units would silently break every consumer to save nanoseconds. The profitable
conversions are inside sort comparators, which is a different fix.

**The ground-immunity bug is real but backwards.** `SK_Field.Fashe` sets `IsGround = true` and
then immediately sets it back to `false` on the next line, so the immunity branches never run.
But those branches only gate damage flowing *toward* the player, so repairing the dead store
makes players with the relevant stat take *less* damage, and it exposes a null dereference for
companions that the dead store has been hiding.

## What the engine probe found

Reading the live settings at startup killed two ideas before I wrote any code.
`Physics2D.autoSyncTransforms` is already `false`, so the biggest theoretical win from the
physics settings has nothing left to give. And the player ships with non-incremental garbage
collection, so tuning the incremental time slice isn't available either. What remains is solver
iteration counts (8 velocity, 3 position) and the fixed timestep at 0.02.

`Physics2D.callbacksOnDisable` is `true`, and it needs to stay that way. Pooled despawn works by
disabling the object, and around 40 scripts rely on the resulting `OnTriggerExit2D` to purge
their target lists.

## The bug that cost the most to find

I wrote a patch that keeps the field hitbox object but never enables its collider, replacing the
trigger callbacks with a direct overlap query and a verbatim port of the game's damage dispatch.
It compiled, it read correctly, and two independent reviewers found the same fault in it.

The scan ran as a Harmony **postfix** on `EmptyCOL.Update`. Vanilla `Update` despawns the object
when its lifetime expires, and LeanPool despawn deactivates it synchronously, so on the frame the
hitbox died my postfix saw an inactive object and returned without scanning. With a 0.1 s
lifetime, any frame slower than about 50 ms meant the entire tick dealt no damage at all, in
exactly the low-framerate situation the patch exists to improve.

The fix was to run before the vanilla body instead of after, guarantee one scan per activation,
and pace scans to the physics step so hit counts stay framerate-independent. Two smaller faults
came out of the same review: the trigger-eligibility rule has to read `Collider2D.attachedRigidbody`
rather than `GetComponent<Rigidbody2D>` (one spawner parents the hitbox, so the body lives on an
ancestor), and world radius and offset have to be recomputed per scan because a parented hitbox
inherits an animated parent scale.

That patch ships switched off. It's the only one on the damage path, and its win is
build-dependent and unmeasured, so it stays behind a config toggle until someone benchmarks it
on their own machine.

## What shipped

Nineteen patches in one plugin, each independently toggleable, split by whether they can change
what you see. On by default: the raycast caches, the `O(n²)` fix, the reworked player
housekeeping tick, nearest-target scans instead of sorts, the companion-scan rework, AI tick
staggering, dictionary-based damage-number merging, health-bar write coalescing, and the layer
mask and pooling micro-fixes. Off by default: distance-based AI throttling, the line-of-sight
cache, the follow-scan throttle, auto-lock interval, the child-projectile governor, the
ground-immunity repair, and every engine-level setter.

The plugin also ships the tools I used to measure all of this, because I don't trust my own
estimates. A frame-time overlay shows average FPS, 1% low, frame time and gen0 collections per
minute, and a 60-second capture writes per-frame data plus median, p95, p99 and 1% low to a CSV.

If you benchmark it, turn vsync off first. My baseline capture has `vsync=1` in it, which pins
frame times to the refresh rate and hides any CPU-side gain you were trying to measure. Compare
medians and p99 across three runs of the same scenario, toggling one setting at a time, and use
the Corrupted Realm floor selector to return to the same floor each run.

Download and source: <https://github.com/dgomesbr/shadow-dungeon-tools/tree/main/mods>

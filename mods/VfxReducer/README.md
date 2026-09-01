# VFX Reducer

BepInEx 5 plugin for Shadow Dungeon. Combat FPS rescue: clamps particle effects on
**player-cast skill objects, skill VFX, weapon base-attack procs, and companion objects**.
Enemy effects and telegraphs are never touched.

- GUID: `custom.vfxreducer`
- Version: 1.0.1
- Assembly: `VfxReducer.dll` (build with `dotnet build -c Release`, copy the DLL to `BepInEx\plugins\`)

## What it does

Click the **`VFX: ...`** row in the shared **Mods** menu (docked to the right screen
border) to cycle three modes. The row itself shows the current mode and lights up
whenever clamping is active, so there is no hotkey and no on-screen toast:

| Mode | Effect on player skill/companion particle systems |
|---|---|
| **Off** | Originals restored (default at every game launch). |
| **Reduced** | `maxParticles` and `emission.rateOverTimeMultiplier` scaled to `ParticleBudgetPercent` % of original (`maxParticles` floored at 4). |
| **Minimal** | Same `maxParticles` budget, but emission rate dropped to 10 % of original; optionally also disables `TrailRenderer`s. |

Mode changes apply immediately to every clamped effect that is currently alive
(a registry of live markers is swept on toggle) **and** to everything spawned
afterwards. Because the game pools effects via LeanPool, settings are
re-applied (or restored, when Off) on every spawn, idempotently, from originals
captured the first time each pooled clone is seen.

## Config (`BepInEx\config\custom.vfxreducer.cfg`)

| Entry | Default | Meaning |
|---|---|---|
| `[Clamping] ParticleBudgetPercent` | 40 (range 10-100) | Percent of original `maxParticles` + emission rate kept in Reduced mode (Minimal uses it for `maxParticles` only). Live-reapplies on change. |
| `[Clamping] MinimalAlsoDisablesTrails` | true | Minimal mode also disables `TrailRenderer` components (restored in Off/Reduced). |

The `[Hotkeys] CycleModeHotkey` entry was removed in 1.0.1. An existing config file
may still contain the orphaned line; BepInEx ignores it and rewrites the file
without it on the next launch.

## Hotkeys

None. This plugin owns no keyboard shortcut. Its single control is the
`vfxreducer.mode` row in the Mods menu (`ModMenuProvider.GetMenuItems`).

## Exact game methods hooked (Harmony)

Scope gate (Prefix increments a depth counter, Finalizer decrements it - these
are the player-only spawn paths; `Gun` is the player's scene-scoped weapon
controller):

- `Gun.MGCattack()`, `Gun.SQSattack()`, `Gun.ARCattack()`, `Gun.DEADattack()` - per-class skill casts, spawn `SKPB.SK_FX` VFX
- `Gun.CreatSP()` - spawns the skill object prefab itself
- `Gun.CreatCP(ACTListSkillBT, out CompanionRuntimeData)` - spawns companion objects (`SKPB.CP_OBJ`); also covers `Gun.CreatCP()` and `Gun.SpawnCompanionInstant(ACTListSkillBT, Vector3)` which funnel through it
- `Gun.Summon(bool)` - summon cast VFX (`SKPB.CP_FX`)
- `Gun.ACTprefabFS(SkillOBJ_DT_SP, Vector3)` - weapon base-attack proc object (`ACT.ATprefab`)

(`Gun.CastDirect` is covered transitively: it only dispatches into the four
attack methods and `Summon`.)

Worker:

- Postfix on `Lean.Pool.LeanPool.Spawn(GameObject, Vector3, Quaternion, Transform, bool)` -
  the overload every `Gun` spawn site (and the generic `Spawn<T>` wrappers) funnels
  into. Only acts while the depth counter is > 0 and the spawned object contains at
  least one `ParticleSystem`; attaches/reuses a `VfxClampMarker` component holding
  the original `maxParticles` / `rateOverTimeMultiplier` / trail-enabled values and
  applies the current mode.

## Performance notes

- Spawn postfix is allocation-free after warmup: per pooled clone, arrays are
  allocated exactly once (first sighting); afterwards it is a `GetComponent` plus
  array walks over preallocated buffers.
- Fail-soft: if any method cannot be resolved at patch time, one warning is logged
  and the feature stays disabled. If the spawn postfix ever throws, it logs once
  and permanently disables itself for the session.

## Known limitations

- Only effects flowing through the patched player `Gun` paths are clamped. Secondary
  on-hit/child effects spawned later by skill object scripts (`SK_*` classes),
  companion attack VFX spawned by `CompA`/`CompB`, buffs, and pickups are untouched.
- Objects first spawned while the mode is Off get no marker; they become clamped the
  next time the pool respawns them while a clamp mode is active (mode toggles sweep
  only objects that already carry a marker).
- Emission clamping scales `rateOverTime` only; one-shot Burst emissions are not
  reduced directly (they are still capped by the reduced `maxParticles`).
- Mode is not persisted; every game launch starts at Off.

# Combat DPS Meter (custom.dpsmeter) v1.0.0

A real in-level DPS meter for Shadow Dungeon (BepInEx 5 plugin).

## Why this exists

The game ships its own `DPSManager`, but it is hard-gated to the HomeScene training dummy:
`DPSManager.RecordDamage` early-outs unless `SceneManager.GetActiveScene().name == "HomeScene"`
**and** `enemy.IsDpsTarget` is true (see decompiled `DPSManager.cs`, `RecordDamage` /
`IsHomeScene`). It can therefore never display DPS inside actual dungeon levels. This plugin
measures independently via Harmony hooks on the enemy damage-intake methods.

## What it does

- Rolling-window DPS (default 10 s, configurable) over all damage your side deals to enemies.
- Per-source attribution: **Player** vs **each companion** (keyed by the companion's skill
  name) plus a shared **DoT** bucket, shown as `name - DPS - share%` sorted by DPS.
- Big total DPS readout plus the peak total DPS seen since the last reset.
- Manual **Reset** button, and automatic reset on level change (detected via the scene-scoped
  `SingletonMonoScope<ACTbar>` instance reference changing).

## Hotkeys

| Key | Action | Config |
|-----|--------|--------|
| F9 (default) | Show/hide the meter window | `Window / ToggleHotkey` |

(F6, F8, F10, F11 are used by other local plugins and are left untouched.)

## Config entries (BepInEx/config/custom.dpsmeter.cfg)

- `Window / ToggleHotkey` (KeyboardShortcut, default `F9`) - shows/hides the DPS window.
- `Meter / RollingWindowSeconds` (float, default `10`, range 1-120) - length of the rolling
  DPS window; damage older than this is dropped.

## Exact game methods hooked (Harmony prefix+postfix pairs)

All on `Enemy` (global namespace, `Assembly-CSharp.dll`), verified against the decompiled
source:

1. `public void TakeDamage(float damage, float chuan, float BJrate, float BJDamage, float MSrate, float MSnumber, float yun, DamageType type, int indexType, PlayerManager pl, Companion cp, SkillOBJ_DT_SP skillSource = null)`
   - main direct-hit path. Attribution uses the game's own discriminator
     (`indexType == 1 && cp != null` = companion hit, otherwise Player).
2. `public void TakeDotDamage(DamageType type, float damage, float chuan)`
   - DOT tick path. Recorded under the shared `DoT` source (see limitations).
3. `public void TakeDirectDamage(float damage, DamageType type)`
   - crit-transfer damage (`Crit_BoomEXP`), attributed to Player.
4. `public void TakeCutJumpDamage(DamageType type, float percent)`
   - percent-HP jump damage, attributed to Player.

Also used (not hooked): `public static string DamgeTextManager.FormatDamageNumber(float)`
(`DamgeTextManager.cs:183`) for K/M/B display formatting, and `Companion.Name` (public field,
assigned `dt_cp.skillName` at spawn in `SK_FSQ_comp.cs:145`) as the stable per-companion key.

## How damage is measured (post-mitigation)

The raw `damage` parameters of these methods are **pre-mitigation**. Instead of trusting them,
the prefix snapshots `Enemy.HealthStat.CurrentValue` and the postfix records the HP delta -
i.e. the **final applied damage** after resists, armor, crits, percent-cut procs and execute
procs. `EnemyStat` clamps HP at 0 (`ClampCurrentInternal`), so overkill damage on the killing
blow is *not* counted (this matches "HP actually removed").

## Implementation notes

- Fixed 4096-entry struct ring buffer `(time, amount, sourceId)` with per-source running sums
  maintained by evicting expired entries each `Update`. After each source name has been seen
  once, the record path (per enemy hit) performs zero allocations: no LINQ, no string work,
  no closures. Display strings are rebuilt only at 4 Hz while the window is open.
- Timestamps use `Time.time` (scaled), matching the game's own DPSManager, so pausing the game
  freezes the window instead of draining it.
- Fail-soft: if `Enemy.TakeDamage` cannot be resolved the meter disables itself with a single
  warning; if any hook ever throws, recording stops after logging one error (never per-frame
  spam). Missing secondary hooks (`TakeDotDamage` etc.) disable only that damage category.
- IMGUI window id **49309**; rect is round-tripped through `GUILayout.Window` and clamped to
  the screen; all controls are drawn before `GUI.DragWindow()`.

## Known limitations

- **DoT attribution:** `Enemy.TakeDotDamage` carries no attacker parameter (the game even calls
  `ClearLastDamageCompanion()` there), so all DOT ticks - whether the dot was applied by the
  player or by a companion - are grouped under one `DoT` row.
- **Companion names are internal skill keys:** `Companion.Name` is set to the summoning skill's
  internal `skillName`, not the localized display name, so rows may show internal identifiers.
  Multiple companions from the same skill share one row (by design - per-skill attribution).
- **Not counted:** damage applied by direct `HealthStat.SetCurrent(...)` side effects that
  bypass the four hooked methods, e.g. the dot chain-death explosion (`TryDeadDotExplosion`
  sets a neighbor to 0 HP) and any scripted percent-HP drains.
- Overkill on the killing blow is excluded because enemy HP clamps at 0.
- The meter only counts enemy HP loss; shields/summons of enemies (if any) that are not backed
  by `Enemy.HealthStat` are not tracked.

## Build

`dotnet build` in this directory; copy `bin/.../CombatDpsMeter.dll` to
`F:\SteamLibrary\steamapps\common\Shadow Dungeon\BepInEx\plugins\`.

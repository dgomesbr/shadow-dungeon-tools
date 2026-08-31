# SummonAll

A BepInEx 5 plugin for **Shadow Dungeon** that re-summons every companion skill you
have learned with one click (or one hotkey). Written as a companion to Max's
*Character Utilities* plugin: when Character Utilities is installed, SummonAll embeds
a **Summon All** button at the top of its F6 window; when it is not, SummonAll shows
its own small F6 window instead.

- **GUID:** `dgome.summonall` &nbsp;|&nbsp; **Version:** 1.0.0
- **Soft dependency:** `max.characterutilities` (works fine without it)

## What it does

For each skill in the action bar (`ACTbar.actListSkill`) that the game itself treats
as a summon (`DT.type == 1 && DT.comp != null`), it computes the deficit between the
maximum companion count (`Summon_count_Last` from the talent tree, falling back to
the data table's `Summon_count`) and the companions currently alive in the skill's
`cpList`, then fills the gap. Two modes, chosen by config:

| Mode | Config value | Behaviour |
|---|---|---|
| Free (default) | `RespectCooldownAndMana = false` | Instantly spawns each missing companion via `Gun.SpawnCompanionInstant`, scattered around the player — same code path the game uses for its own after-death auto-resummon. |
| Fair | `RespectCooldownAndMana = true` | Casts each deficient summon skill once through the normal pipeline (`ACTbar.TryReleaseSkillDirect`), paying mana and starting the cooldown; skipped skills are reported with the reason. |

Config lives in `BepInEx\config\dgome.summonall.cfg` after first run. An optional
`SummonAllHotkey` (e.g. `F7`) triggers the summon without opening any window.

## Build

Prerequisites: a .NET SDK (`dotnet` on PATH) and a Shadow Dungeon install with
BepInEx 5 already set up (see [`docs/modding/installation.md`](../../docs/modding/installation.md)).

The `.csproj` references game and BepInEx assemblies by absolute path from the
default Steam location:

```
F:\SteamLibrary\steamapps\common\Shadow Dungeon\BepInEx\core\...
F:\SteamLibrary\steamapps\common\Shadow Dungeon\Shadow Dungeon_Data\Managed\...
```

If your game lives elsewhere, edit the `<HintPath>` entries in `SummonAll.csproj`
to point at your install. Then:

```bash
dotnet build -c Release
```

Output: `bin/Release/netstandard2.0/SummonAll.dll` (the game DLL references use
`<Private>false</Private>`, so only the plugin DLL itself is produced — never copy
game assemblies anywhere).

## Deploy

Copy the built DLL into the game's plugin folder and (re)start the game:

```bash
cp bin/Release/netstandard2.0/SummonAll.dll \
  "/f/SteamLibrary/steamapps/common/Shadow Dungeon/BepInEx/plugins/"
```

Verify in `BepInEx\LogOutput.log`:

```
[Info   :   BepInEx] Loading [Summon All 1.0.0]
[Info   :Summon All] Summon All button embedded into the Character Utilities (F6) window.
```

(or `Character Utilities plugin not found - Summon All uses its own F6 window.`)

## Usage

1. Enter a dungeon (the plugin needs a live level: `ACTbar`, `Gun` and
   `PlayerManager` must exist).
2. Press **F6** and click **Summon All**, or press your configured hotkey.
3. The status line reports what was summoned, e.g.
   `Summoned 44: 6x Skeleton Warrior, 6x Death Sentry, ...`.

## Implementation notes

- The Character Utilities integration is a Harmony **Prefix** on
  `CharacterUtilities.Plugin.DrawWindow`, resolved at runtime with
  `AccessTools.TypeByName` so the dependency stays soft. It must be a Prefix, not a
  Postfix: `DrawWindow` ends with `GUI.DragWindow()`, which swallows clicks on any
  control laid out after it (see
  [`docs/modding/adding-features.md`](../../docs/modding/adding-features.md#pitfalls)).
- Summon detection deliberately keys off `DT.type == 1 && DT.comp != null` — the
  discriminator the game's own `ACTbar.RestoreRebornAutoSummons` uses — never off
  `SkillType`, because weapon-granted summon skills can carry unrelated `SkillType`
  values.
- The private helpers `ACTbar.GetCurrentCompSummonCount` / `GetAliveCompCount` are
  replicated (not reflected) in `GetMaxSummonCount` / `GetAliveCompCount` inside
  [`Plugin.cs`](Plugin.cs).

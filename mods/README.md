# Shadow Dungeon QoL Plugin Suite

Seven open-source BepInEx 5 plugins for **Shadow Dungeon** (Steam), each shipped as its own
DLL so you can install exactly the ones you want. Every plugin was built against the game's
decompiled code with verified method signatures, and fails soft: if a game update breaks a
hook, the plugin logs one warning and disables itself instead of breaking your game.

Download: grab `ShadowDungeon-QoL-Plugins-1.1.0.zip` from the
[releases page](https://github.com/dgomesbr/shadow-dungeon-tools/releases) — or build from
source (below).

## The plugins

| Plugin | Hotkey | What it does |
|---|---|---|
| **Summon All** | F6 window / config hotkey | One click re-summons every companion your talents allow (after death, zone change). Fair mode makes it pay mana/cooldowns. Embeds into Character Utilities' F6 window when present. |
| **Combat DPS Meter** | **F9** | Real in-dungeon DPS (the game's own meter only works on the training dummy). Rolling window, per-source rows: Player / each summon / DoT, share %, peak. Measures actual post-mitigation HP removed. |
| **Readable Numbers** | passive | Damage/DPS/gold at the nearest named scale: `510 Billion`, `1.2 Trillion`, `3.4 Quadrillion` … up to Undecillion. Full grouped integers below a million. Other modes in config. |
| **Advanced Tooltips** | passive | Every rollable affix line shows `(min~max)` so you can judge rolls at a glance, and hovering ground loot shows its full tooltip without picking it up. |
| **VFX Reducer** | **F11** | Cycles Off / Reduced / Minimal: clamps particle counts and emission on your skill and companion effects for dense-floor FPS. Enemy effects and telegraphs untouched. |
| **Quick Enhance** | **Shift+click** | Hold Shift when clicking enhance at the forge: loops the enhancement until +max / out of budget / out of gold, one attempt per frame. |
| **Mijing Floor Selector** | **F10** | Jump to any unlocked Corrupted Realm floor; a confirm-gated button raises your unlocked cap (uses the game's own progression API, raise-only). |

All windows are draggable IMGUI overlays; hotkeys are rebindable in each plugin's config file
(`BepInEx\config\custom.<plugin>.cfg`, created on first run).

## Install

1. **BepInEx** (one time): extract
   [BepInEx_win_x64_5.4.23.5.zip](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)
   into the game root (the folder with `Shadow Dungeon.exe`). Run the game once and quit.
2. **Plugins**: extract the release zip into the same game root — it only adds DLLs into
   `BepInEx\plugins\`. Delete any DLL you don't want.
3. Play. Check `BepInEx\LogOutput.log` if something doesn't appear.

## Building from source

Each plugin folder here contains its complete source (`Plugin.cs`, `.csproj`, `README.md`).
Fix the `<HintPath>`s in the `.csproj` to your game install, then:

```
dotnet build -c Release
```

The DLL lands in `bin/Release/netstandard2.0/` — copy it to `BepInEx\plugins\`.

## Design notes & known limitations

- Per-plugin READMEs document the exact game methods hooked and every limitation.
  Highlights: the DPS meter counts post-mitigation damage (overkill on a killing blow is
  excluded); the VFX reducer clamps your cast effects but not summon bodies or enemy VFX;
  Advanced Tooltips hides its range annotation whenever it cannot reproduce the game's exact
  roll math rather than guess; the floor selector skips the vanilla gold entry fee.
- Hot paths (per-hit damage hooks, spawn hooks) are allocation-free after warmup.
- IMGUI window ids 49300–49399 are reserved across this suite to avoid collisions
  (49309 DPS, 49312 VFX toast, 49313 Mijing; Character Utilities uses 49265).
- A retired eighth plugin (Summon Counter overlay) is not shipped: the game's own top-bar
  skill icons already show live summon counts.

## Credits & safety

The suite is unaffiliated fan work under the repo's MIT license. The separate **Character
Utilities** mod (the original F6 window: gold transfer, story copy, auto-aim, boss replay) is
by **Max** from the community Discord (#qol-mod) — see the
[`mods-v1.0.0` release](https://github.com/dgomesbr/shadow-dungeon-tools/releases/tag/mods-v1.0.0),
redistributed with attribution and removed on request. Plugins run code inside the game
process — only install DLLs you trust, and back up your saves first
(`%USERPROFILE%\AppData\LocalLow\OO Cat\Shadow Dungeon\`).

# Credits

## Max — Character Utilities

**Max** (Discord user) is the original author of the *Character Utilities* plugin
(`max.characterutilities`, v1.1.0, shipped as `ShadowDungeonPlus.dll`) and the
person who first got BepInEx modding working on Shadow Dungeon. The plugin is
shared in the
[#qol-mod channel](https://discord.com/channels/1543586564439810138/1543599915006165002)
of the Shadow Dungeon Discord.

Character Utilities pioneered every pattern this documentation teaches:

- Harmony postfixes that redirect the game's aim context to its own auto-lock
  target (the closest-enemy Auto Cast feature);
- the F6 IMGUI utility window (gold transfer/export/import between save slots,
  story-progress copy);
- safe save-slot mutation through `SaveManager`, including reflection into its
  private loaders and flushing via `SaveAndWaitIfNeeded`;
- the replayable boss portal (event-hook postfix + marker component + intercepting
  prefix).

Our own SummonAll plugin embeds into Character Utilities' window and exists because
Max's plugin showed the way. These docs derive their patterns from studying the
decompiled plugin; we describe its internals in our own words rather than
reproducing its code, and take the plugin's public sharing on the community Discord
as implied permission for that study. Max: if you'd like anything here changed or
removed, say the word in #qol-mod.

## OO Cat — Shadow Dungeon

**OO Cat** made Shadow Dungeon itself (Steam appid 4423580). This is unaffiliated
fan tooling: no game assets or game code are redistributed here, and everything in
these docs comes from studying a legitimately owned copy of the game.

## Tooling

- [BepInEx](https://github.com/BepInEx/BepInEx) — the plugin loader
  (BepInEx contributors, LGPL-2.1).
- [HarmonyLib](https://github.com/pardeike/Harmony) — runtime patching
  (Andreas Pardeike, MIT), bundled with BepInEx.
- [ILSpy](https://github.com/icsharpcode/ILSpy) — the decompiler used to study the
  game and plugin assemblies.
- [UniTask](https://github.com/Cysharp/UniTask) — the async library the game (and
  therefore its mods) build on (Cysharp, MIT).

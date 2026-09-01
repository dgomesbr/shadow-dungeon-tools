# Modding Shadow Dungeon

Shadow Dungeon (OO Cat, Steam appid 4423580) is a Unity 2019.4 **Mono** game with
unobfuscated code — about the friendliest modding target there is. With
[BepInEx 5](https://github.com/BepInEx/BepInEx) installed, a mod is a plain .NET
class library dropped into `BepInEx\plugins\` that patches game methods with
Harmony at runtime. No game files are ever modified.

These docs are grounded in two real, working plugins:

- **Character Utilities** v1.1.0 by **Max** (`max.characterutilities`) — the
  community's original QoL plugin: closest-enemy auto-aim for Auto Cast, an F6
  utility window for moving gold and story progress between save slots, and a
  replayable boss portal.
- **SummonAll** v1.0.0 (`dgome.summonall`, source in this repo at
  [`mods/SummonAll/`](../../mods/SummonAll/README.md)) — one click re-summons every
  companion skill; embeds into Character Utilities' F6 window when present.

Both ship prebuilt as `ShadowDungeon-F6-Mods-1.0.0.zip` on this repo's
[`mods-v1.0.0` release](https://github.com/dgomesbr/shadow-dungeon-tools/releases/tag/mods-v1.0.0).

## Contents

| Doc | What's in it |
|---|---|
| [installation.md](installation.md) | Installing BepInEx 5.4.23.5 x64 step by step, verifying with `LogOutput.log`, and installing both plugins — either via the one-zip [`mods-v1.0.0` release](https://github.com/dgomesbr/shadow-dungeon-tools/releases/tag/mods-v1.0.0) (`ShadowDungeon-F6-Mods-1.0.0.zip`) or by hand. |
| [mod-mechanics.md](mod-mechanics.md) | How the game is put together for modders: the singleton/manager landscape (`PlayerManager`, `SaveManager`, `ACTbar`, `Gun`, ...), the four Harmony patch patterns with code, safe save mutation, the IMGUI overlay pattern, config binding and hotkeys. |
| [adding-features.md](adding-features.md) | Building a new plugin from zero: csproj, plugin skeleton, finding patch targets in decompiled code, the build/deploy loop, debugging, and the pitfalls that cost us real time. |
| [credits.md](credits.md) | Max, OO Cat, and the tooling this all stands on. |

## The 60-second version

```csharp
[BepInPlugin("yourname.myplugin", "My Plugin", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private void Awake()
    {
        var enabled = Config.Bind("General", "Enabled", true, "Master toggle.");
        new Harmony("yourname.myplugin").PatchAll(typeof(Plugin).Assembly);
        Logger.LogInfo("My Plugin loaded.");
    }
}

[HarmonyPatch(typeof(PlayerManager), "IsAutoLockActive")]
static class MyPatch
{
    static void Postfix(ref bool __result) { /* ... */ }
}
```

Build against `Shadow Dungeon_Data\Managed\` + `BepInEx\core\`
(`dotnet build -c Release`, `netstandard2.0`), copy the DLL to `BepInEx\plugins\`,
start the game, read `BepInEx\LogOutput.log`. Details in
[adding-features.md](adding-features.md).

## Safety notes

- Plugins run arbitrary code in the game process — only install DLLs you trust or
  built yourself.
- Back up your saves (`%USERPROFILE%\AppData\LocalLow\OO Cat\Shadow Dungeon\`)
  before using anything that touches them.
- Everything here is unaffiliated fan work; be excellent to OO Cat and the
  community.

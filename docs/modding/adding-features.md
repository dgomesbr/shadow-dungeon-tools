# Adding a feature: building a new plugin from zero

A practical walkthrough for writing your own BepInEx plugin for Shadow Dungeon.
The running example is a minimal plugin, but every step is the exact process used
to build [SummonAll](../../mods/SummonAll/) — copy that project as a template if
you prefer starting from working code.

Prerequisites:

- A .NET SDK (any recent one — the *SDK* version doesn't need to match the game;
  we target `netstandard2.0` which every SDK since .NET Core 3 can build).
- BepInEx installed and verified per [installation.md](installation.md).
- Optionally a decompiler ([ILSpy](https://github.com/icsharpcode/ILSpy) /
  `ilspycmd`) pointed at `Shadow Dungeon_Data\Managed\Assembly-CSharp.dll` — you
  will live in the decompiled game code while finding things to patch.

## 1. Project file

Create `MyPlugin/MyPlugin.csproj`. This is the exact shape SummonAll ships with —
plain assembly references into the game install, nothing from NuGet:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>MyPlugin</AssemblyName>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Version>1.0.0</Version>
    <DebugType>none</DebugType>
    <!-- convenience: one place to change when the game lives elsewhere -->
    <GameDir>F:\SteamLibrary\steamapps\common\Shadow Dungeon</GameDir>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="BepInEx"><HintPath>$(GameDir)\BepInEx\core\BepInEx.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="0Harmony"><HintPath>$(GameDir)\BepInEx\core\0Harmony.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Assembly-CSharp"><HintPath>$(GameDir)\Shadow Dungeon_Data\Managed\Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="FinkFramework.Runtime"><HintPath>$(GameDir)\Shadow Dungeon_Data\Managed\FinkFramework.Runtime.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine"><HintPath>$(GameDir)\Shadow Dungeon_Data\Managed\UnityEngine.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.CoreModule"><HintPath>$(GameDir)\Shadow Dungeon_Data\Managed\UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.IMGUIModule"><HintPath>$(GameDir)\Shadow Dungeon_Data\Managed\UnityEngine.IMGUIModule.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.InputLegacyModule"><HintPath>$(GameDir)\Shadow Dungeon_Data\Managed\UnityEngine.InputLegacyModule.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
```

Notes:

- **`netstandard2.0` works.** The game runs Mono with a .NET 4.x-equivalent CLR
  (`CLR runtime version: 4.0.30319` in the BepInEx log), which fully implements
  netstandard2.0. SummonAll ships this way. The classic BepInEx templates target
  `net35`/`net46`; you only need those if you want the template's tooling —
  avoid `net35` specifically, it forbids modern language conveniences and this
  game's runtime doesn't need the downgrade. Stay away from netstandard2.1-only
  APIs to be safe (some are missing on Unity's profile).
- **`<Private>false</Private>` on every reference.** You must never copy game or
  BepInEx DLLs next to your plugin; the loader provides them at runtime.
- Add Unity module references as compile errors demand them: `UnityEngine.UI` for
  world-space `Text`, `UniTask` for `Cysharp.Threading.Tasks`,
  `UnityEngine.JSONSerializeModule` for `JsonUtility`, etc. They are all in
  `Shadow Dungeon_Data\Managed\`.
- **Private members:** you can usually reach them with reflection or Harmony's
  `AccessTools`/`Traverse` (this is what Character Utilities does for
  `SaveManager.TryPeekSlotForEntry`). If a feature needs *lots* of private access,
  generate publicized reference assemblies with
  [BepInEx.AssemblyPublicizer](https://github.com/BepInEx/BepInEx.AssemblyPublicizer)
  and reference those at compile time instead — neither existing plugin needed it.

## 2. Plugin skeleton

`MyPlugin/Plugin.cs`:

```csharp
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MyPlugin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "yourname.myplugin"; // unique, stable, lowercase
    public const string PluginName = "My Plugin";
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> Enabled;

    private Harmony _harmony;

    private void Awake()   // runs once at game startup, before any scene logic
    {
        Log = Logger;
        Enabled = Config.Bind("General", "Enabled", true, "Master toggle.");

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Plugin).Assembly);  // finds [HarmonyPatch] classes

        Log.LogInfo($"{PluginName} loaded.");
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();
}
```

Your plugin class *is* a MonoBehaviour on a persistent BepInEx GameObject, so
`Update()`, `OnGUI()`, coroutines etc. all work — that is how the F6 windows and
hotkeys in the existing plugins run.

Add patch classes alongside (see the four patterns in
[mod-mechanics.md](mod-mechanics.md#harmony-patching-patterns)):

```csharp
[HarmonyPatch(typeof(PlayerManager), "IsAutoLockActive")]
internal static class ExamplePatch
{
    private static void Postfix(PlayerManager __instance, ref bool __result)
    {
        // ...
    }
}
```

## 3. Find something to patch

Workflow that produced both existing plugins:

1. Decompile `Assembly-CSharp.dll` and grep for gameplay terms
   (`Summon`, `BossPortal`, `AimContext`, `Money`, ...). Names are unobfuscated;
   some identifiers/comments are Chinese (e.g. `Xi` = talent tree, `mijing` =
   secret realm) — grep is still effective.
2. Read how the game itself performs the action you want. **Mirror the game's own
   code path** rather than inventing one: SummonAll copies the discriminator and
   count logic from `ACTbar.RestoreRebornAutoSummons` / `TryAutoUseSkills` instead
   of guessing what "is a summon skill" means.
3. Prefer calling public game methods (`Gun.SpawnCompanionInstant`,
   `ACTbar.TryReleaseSkillDirect`) over reimplementing behaviour; patch only where
   you must change behaviour.

## 4. Build and deploy loop

```bash
cd MyPlugin
dotnet build -c Release
cp "bin/Release/netstandard2.0/MyPlugin.dll" \
   "/f/SteamLibrary/steamapps/common/Shadow Dungeon/BepInEx/plugins/"
# launch the game, test, quit, repeat
```

The game loads plugins only at startup — a full restart per iteration. The loop is
fast (the game boots to menu in seconds); still, batch your testing: add a status
label to your GUI so you can verify behaviour in-game without alt-tabbing to logs.

You cannot overwrite the DLL while the game is running (Windows locks loaded
assemblies) — quit first.

## 5. Debugging

- **`BepInEx\LogOutput.log`** — everything from the last run: chainloader plugin
  list, your `Log.LogInfo/LogWarning/LogError` lines, and full stack traces from
  exceptions inside patches. First place to look, always.
- **Live console** — set `[Logging.Console] Enabled = true` in
  `BepInEx\config\BepInEx.cfg` to watch logs in real time.
- **Fail loudly but safely.** Wrap feature entry points in try/catch, log the
  exception, and show it in your status line (both existing plugins do
  `_status = "ERROR: " + ex.GetBaseException().Message` + `Log.LogError(ex)`).
  An unhandled exception inside a Harmony prefix/postfix can break the patched
  game method for the rest of the session.
- **Confirm your patch actually applied.** Log after `PatchAll`, and if a target
  is resolved dynamically, log which branch you took (SummonAll logs whether it
  embedded into Character Utilities or fell back to its own window).

## <a id="pitfalls"></a>6. Common pitfalls

- **`GUI.DragWindow()` eats clicks — inject with a Prefix, never a Postfix.**
  If you add IMGUI controls to another plugin's window whose draw callback ends
  with `GUI.DragWindow()`, controls drawn after it (a Postfix) render but never
  receive clicks, because DragWindow claims all remaining input in the window.
  Draw first (Prefix). This cost real debugging time in SummonAll.
- **Wrong target framework.** Use `netstandard2.0` (proven) or `net46`-era
  profiles. `netstandard2.1` APIs may be missing at runtime; NuGet packages that
  drag in `System.*` shim DLLs will not load — keep dependencies at zero and
  `<Private>false</Private>` everywhere.
- **UniTask, not Task.** Game async methods (`SaveManager.SaveAndWaitIfNeeded`,
  `SceneLoadManager.LoadLevelScene`) return Cysharp `UniTask`. `await` them from
  `async UniTask`/`UniTaskVoid` methods and fire-and-forget with `.Forget()`.
  Mixing in `Task.Run`/thread-pool continuations will touch Unity APIs off the
  main thread and crash or silently misbehave.
- **Soft dependencies done right.** To integrate with another plugin without
  requiring it: `[BepInDependency("their.guid", BepInDependency.DependencyFlags.SoftDependency)]`
  on your plugin class (guarantees load *order* if present), then resolve their
  types at runtime with `AccessTools.TypeByName("Their.Namespace.Type")` — never
  a compile-time reference, which would hard-crash your plugin when they're
  absent. Wrap the wiring in try/catch and implement a fallback (SummonAll's
  standalone window).
- **Check `HasInstance` before `Instance`.** Level-scoped singletons don't exist
  in the menu/town; `Instance` on a missing singleton runs `FindObjectOfType`,
  logs a warning, and returns null. Guard with `HasInstance` and fail with a
  friendly status message ("Not in a level - enter a dungeon first.").
- **Unique everything.** Harmony ID = your GUID; IMGUI window ids must not collide
  with other plugins (pick a random 5-digit number); config section/key names are
  per-plugin so those are safe.
- **Assembly name ≠ plugin identity.** BepInEx keys on the `[BepInPlugin]` GUID,
  not the DLL name (Character Utilities famously ships as `ShadowDungeonPlus.dll`).
  Keep the GUID stable across versions or users lose their config.
- **Saves are sacred.** Never write save files yourself from a plugin; go through
  `SaveManager` as described in
  [mod-mechanics.md](mod-mechanics.md#save-mutation-safety), and keep a busy flag
  around save-touching operations.

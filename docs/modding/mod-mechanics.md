# How Shadow Dungeon is put together (for modders)

Everything in this document is grounded in the game's decompiled code
(`Assembly-CSharp.dll`, Unity 2019.4.39, Mono backend) and in two real plugins:
Max's **Character Utilities** (`max.characterutilities`, studied via decompilation —
described here in our own words, not reproduced) and our own
**SummonAll** ([`mods/SummonAll/`](../../mods/SummonAll/Plugin.cs)).

## The stack

| Layer | What it is |
|---|---|
| `Assembly-CSharp.dll` | All game logic. ~900 classes, no obfuscation, readable names. |
| `FinkFramework.Runtime.dll` | The developer's in-house framework: singletons, utils. |
| `FinkFramework.Odin.OdinSerializer.dll` | Save serialization (binary, unencrypted). |
| `UniTask.dll` | Cysharp UniTask — the game's async model. Game methods return `UniTask`, not `Task`. |
| BepInEx 5 + HarmonyLib | Our injection layer. Plugins are plain .NET libraries in `BepInEx\plugins\`. |

Reference DLLs for compiling live in
`Shadow Dungeon_Data\Managed\` (game + Unity modules) and
`BepInEx\core\` (`BepInEx.dll`, `0Harmony.dll`).

## Singletons: how to reach any manager

Almost every manager is a MonoBehaviour singleton from FinkFramework. Three bases
matter:

- **`SingletonMonoScope<T>`** (`FinkFramework.Runtime.Singleton`) — the workhorse.
  Static `Instance` (lazily `FindObjectOfType` if not cached) and `HasInstance`
  (true only if the instance is already cached/alive).
- **`ScopedSingletonMono<T>`** (game code, `Scenes` namespace) — *derives from*
  `SingletonMonoScope<T>` and additionally registers itself with the global
  `SessionManager` under `ProcessScope.Game`, so it is torn down when the play
  session ends. Because it derives from `SingletonMonoScope<T>`,
  `SingletonMonoScope<ACTbar>.Instance` works even though `ACTbar` is declared as
  `ScopedSingletonMono<ACTbar>` — the static instance field lives in the base.
- **`SingletonMonoGlobal<T>`** — process-lifetime singletons (`SessionManager`,
  `PlayerSpawnManager`, ...).

The pattern every plugin should use — check before you touch:

```csharp
if (!SingletonMonoScope<PlayerManager>.HasInstance)
    return; // in the main menu, or between scenes
var player = SingletonMonoScope<PlayerManager>.Instance;
```

`HasInstance` is the "am I in a level right now?" test. Level-scoped managers
(`ACTbar`, `Gun`, `TalentManager`, ...) simply do not exist in the town/menu.

### Manager cheat sheet

| Manager | Declared as | What it owns |
|---|---|---|
| `PlayerManager` | `SingletonMonoScope` | The player: `IsAlive`, `AutoAttackEnabled` (the in-game Auto Cast toggle), `TryGetAutoLockYaoPosition(out Vector3)` (current auto-lock target), `transform.position`. |
| `ACTbar` | `ScopedSingletonMono` | The skill bar: `actListSkill` (list of `ACTListSkillBT`), `TryReleaseSkillDirect(skill, useCooldown, spendMana, skipAnimation = false)`, the auto-resummon logic (`RestoreRebornAutoSummons`). |
| `Gun` | `ScopedSingletonMono` | Skill execution/projectiles: `SpawnCompanionInstant(ACTListSkillBT skill, Vector3 spawnPos)`. |
| `TalentManager` | `ScopedSingletonMono` | Talent trees: `XiData[]` (per-class `SkillXiData`, whose `Comp_F` dictionary maps skill `IndexName` → `SkillData_Comp_Father` with the live `Summon_count_Last`). |
| `ItemManager` | `SingletonMonoScope` | Item/affix/set data tables (e.g. `SET`). |
| `InventoryManager` | `ContainerManager` (singleton) | Live inventory; `GlobalMoney` mirrors the save's gold. |
| `LevelManager` | `SingletonMonoScope` | Static level queries: `GetIsBoss()`, `GetCurLevel()`, `GetIsCurChapterFinal()`. |
| `CompanionManager` | singleton | Summoned companions; `RequestRefreshNextFrame()` after spawning. |
| `DialogManager` | singleton | Story/dialog state; `ApplySaveData(DialogSaveData)`. |
| `PlayerSpawnManager` | `SingletonMonoGlobal` | Where the player appears after a scene load (`SetLevelRequest`). |
| `SceneLoadManager` | static API | `LoadLevelScene(levelId, SceneTransitionMode).Forget()`. |
| `SaveManager` | **static API** (plain `Singleton<T>`, not a MonoBehaviour scope) | Everything below in "Save mutation safety". |

## Harmony patching patterns

Both plugins use HarmonyLib (bundled with BepInEx as `0Harmony.dll`). Two
bootstrap styles, both real:

**Attribute style** (Character Utilities): patch classes are annotated and
discovered in one call from `Awake()`:

```csharp
_harmony = new Harmony(PluginGuid);
_harmony.PatchAll(typeof(Plugin).Assembly);
```

**Manual style** (SummonAll): resolve the target at runtime — required when the
target type may not exist (soft dependency on another plugin):

```csharp
var type = AccessTools.TypeByName("CharacterUtilities.Plugin"); // null if absent
var method = type != null ? AccessTools.Method(type, "DrawWindow") : null;
if (method != null)
    _harmony.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(DrawWindowPrefix)));
```

Always unpatch on unload: `void OnDestroy() => _harmony?.UnpatchSelf();`

The four patch shapes below cover everything the existing plugins do. The snippets
are fresh illustrations of the *pattern*; see the feature descriptions afterwards
for what the real plugins actually do with them.

### 1. Postfix that rewrites a return value (`ref __result`)

Let the game compute its answer, then override it under your own conditions.
Character Utilities' auto-aim is exactly this shape on
`AimProvider.GetCurrentAimContext`:

```csharp
[HarmonyPatch(typeof(AimProvider), "GetCurrentAimContext")]
static class AimRedirectPatch
{
    static void Postfix(ref AimContext __result)
    {
        if (!SingletonMonoScope<PlayerManager>.HasInstance) return;
        var player = SingletonMonoScope<PlayerManager>.Instance;
        // only when OUR toggle and the GAME'S Auto Cast toggle are both on,
        // and the game can supply an auto-lock target:
        if (!MyToggle.Value || !player.IsAlive || !player.AutoAttackEnabled) return;
        if (!player.TryGetAutoLockYaoPosition(out var target)) return;

        target.z = 0f;
        __result.WorldPoint    = target;                     // aim here...
        __result.Direction     = ((Vector2)(target - player.transform.position)).normalized;
        __result.HasDirection  = true;                       // ...not at the mouse
        __result.HasTargetPoint = true;
    }
}
```

A boolean variant of the same shape flips `PlayerManager.IsAutoLockActive` to
`true` (only when it was `false` and the same gates hold), so the rest of the game
believes auto-lock is engaged.

### 2. Postfix as an event hook (react, don't change)

Patch a notification method to run extra logic after it. Character Utilities hooks
`LevelRoot.OnBossAllDefeated` this way to spawn its replay portal:

```csharp
[HarmonyPatch(typeof(LevelRoot), "OnBossAllDefeated")]
static class BossDefeatedHook
{
    static void Postfix()
    {
        if (!LevelManager.GetIsBoss()) return;   // only in boss levels
        // ... react: spawn objects, grant rewards, log, etc.
    }
}
```

What the real patch does: finds the `BossPortal` nearest the player with
`FindObjectsOfType`, clones its GameObject at a free spot from
`PortalManager.GetPortalSpawnPos`, tags the clone with a marker `MonoBehaviour`
(`ReplayBossPortalMarker`), relabels its world-space `Text` to "Replay Boss", and
re-inits it as `GoLevel` (or `GoHome` on a chapter's final level). Tagging clones
with your own marker component is the idiomatic way to recognize *your* objects
later without maintaining global lists — markers survive on the GameObject and are
found with a simple `GetComponent`.

### 3. Prefix that replaces the original (`return false`)

Intercept a method and skip the game's implementation for objects you own.
Character Utilities pairs this with the marker above on `BossPortal.Interact`:

```csharp
[HarmonyPatch(typeof(BossPortal), "Interact")]
static class PortalInterceptPatch
{
    static bool Prefix(BossPortal __instance)
    {
        var marker = __instance.GetComponent<ReplayBossPortalMarker>();
        if (!marker) return true;      // not ours → run the original
        // ours → do our thing instead, then skip the original:
        // (real plugin: guard against double-use, queue a spawn request on
        //  PlayerSpawnManager, then SceneLoadManager.LoadLevelScene(current level))
        return false;
    }
}
```

The real implementation reloads the *current* level scene — a fresh boss fight —
by setting a `LevelPlayerSpawnRequest` (`EnterFromTeleport` / `TeleportType.Exit`)
on the global `PlayerSpawnManager` and calling
`SceneLoadManager.LoadLevelScene(LevelManager.GetCurLevel(), SceneTransitionMode.Fade).Forget()`.

### 4. Prefix to inject IMGUI into someone else's window

SummonAll adds its button to Character Utilities' F6 window by prefixing the
window's draw callback (real code, [`Plugin.cs`](../../mods/SummonAll/Plugin.cs)):

```csharp
private static void DrawWindowPrefix()
{
    DrawSummonSection();   // GUILayout controls, drawn BEFORE the host's controls
    GUILayout.Space(6f);
}
```

**It must be a Prefix.** The host's `DrawWindow` ends with `GUI.DragWindow()`,
which claims every unclaimed click in the window — any control laid out *after* it
(i.e. from a Postfix) renders fine but never receives clicks. Drawing first keeps
your controls live. This is the single most confusing IMGUI pitfall; see
[adding-features.md](adding-features.md#pitfalls).

## Save mutation safety

`SaveManager` (static API) is the only correct door to save data. Key surface,
verified against the decompiled class:

```csharp
public static SaveData RuntimeData { get; }          // live save of the ACTIVE slot
public static bool     HasRuntime  { get; }
public static int      CurrentSlotId { get; }        // -1 = no active session
public static async UniTask<bool> SaveAndWaitIfNeeded();
public static bool HasAnySaveSlot();
public static IEnumerable<SaveSlotData> GetAllSaveSlotForUI();  // slot metadata for menus

// private — reflection required:
private static bool TryPeekSlotForEntry(int slotId, out SaveData data, out string loadedPath);
private static bool BeginNewSession(int slotId, SaveData selectedData);
```

Character Utilities' gold/story tools establish the safe mutation protocol:

1. **Flush the active slot first.** If a session is active
   (`CurrentSlotId >= 0 && HasRuntime`), `await SaveManager.SaveAndWaitIfNeeded()`
   *before* reading any slot, so on-disk data is current.
2. **Read a slot.** Active slot → just use `SaveManager.RuntimeData`. Inactive
   slot → reflect into the private loader:

   ```csharp
   var peek = typeof(SaveManager).GetMethod("TryPeekSlotForEntry",
       BindingFlags.Static | BindingFlags.NonPublic);
   var args = new object[] { slotId, null, null };
   if ((bool)peek.Invoke(null, args))
       data = (SaveData)args[1];          // args[2] is the loaded file path
   ```

3. **Mutate the `SaveData` object** (e.g. `data.InventoryData.Money`,
   `data.UnlockedChapterIds`, `data.DialogData`, the `mijingFloor_*` fields).
4. **Commit.** Active slot → push the change into the *live* managers too
   (`InventoryManager.Instance.GlobalMoney = ...`,
   `DialogManager.Instance.ApplySaveData(...)`), then
   `await SaveManager.SaveAndWaitIfNeeded()` again. Inactive slot → reflect into
   `BeginNewSession(slotId, data)`, which persists the modified `SaveData` through
   the game's own writer.
5. **Serialize operations.** Keep a `busy` flag so two async save operations never
   overlap, and surface every exception to the user (both plugins funnel errors
   into an on-screen status line plus `Log.LogError`).

Why go through all this instead of writing the `.sav` files directly: the game
keeps **three files per slot** (`slot_N.sav`, `slot_N_auto.sav`, `slot_N_exit.sav`)
and on load silently falls back from `_exit` → `_auto` → base if one fails
validation. Editing only one file (or bypassing the game's OdinSerializer writer)
produces saves that *appear* fine and then quietly roll back. The game's own save
path keeps the trio consistent. (Full format details: this repo's save-format
docs and the standalone SaveTool pipeline.)

Note the async model: these flows are `async UniTask` (Cysharp), launched from GUI
handlers with `.Forget()`. Use `UniTask`, not `Task` — it is what the game's
methods return and it is Unity-main-thread aware.

## The IMGUI overlay pattern

Both plugins use Unity's immediate-mode GUI — zero setup, works from any
MonoBehaviour (your `BaseUnityPlugin` is one):

```csharp
private bool _show;
private Rect _rect = new Rect(30, 30, 340, 110);

private void Update()
{
    if (Input.GetKeyDown(KeyCode.F6)) _show = !_show;   // hotkey toggle
}

private void OnGUI()
{
    if (!_show) return;
    _rect = GUI.Window(49277, _rect, DrawMyWindow, "My Plugin"); // unique id!
}

private void DrawMyWindow(int id)
{
    GUILayout.BeginVertical();
    if (GUILayout.Button("Do the thing")) DoTheThing();
    GUILayout.Label("Status: " + _status);
    GUILayout.EndVertical();
    GUI.DragWindow();   // LAST — everything after this line loses clicks
}
```

Rules learned from the real plugins:

- **Unique window id** per plugin (Character Utilities uses `49265`, SummonAll's
  fallback uses `49277`). Colliding ids make windows fight over input.
- **Store the `Rect` returned by `GUI.Window`** in a field, or dragging silently
  does nothing (the window snaps back every frame because you rebuilt the Rect).
  SummonAll also clamps the stored rect to the screen so the window can't be
  dragged off-screen.
- **`GUI.DragWindow()` goes last** — see pattern 4 above.
- Long-running work started from a button goes through `async UniTask` +
  `.Forget()` with a busy flag; IMGUI handlers run every repaint and must return
  immediately.
- Report results in a status label rather than only the log — players don't read
  `LogOutput.log`.

## Config binding and hotkeys

BepInEx gives every plugin a typed config file for free
(`BepInEx\config\<GUID>.cfg`, auto-generated with your descriptions as comments):

```csharp
internal static ConfigEntry<bool> FairMode;
internal static ConfigEntry<KeyboardShortcut> Hotkey;

private void Awake()
{
    FairMode = Config.Bind("Summoning", "RespectCooldownAndMana", false,
        "When true, ... costs mana, starts the cooldown ...");
    Hotkey = Config.Bind("Summoning", "SummonAllHotkey", KeyboardShortcut.Empty,
        "Optional keyboard shortcut ... (e.g. F7).");
}
```

- Read with `.Value`; write with `.Value = x` then `Config.Save()` if you changed
  it from your own UI (Character Utilities exposes its toggle as an in-window
  checkbox that writes back to config).
- `BepInEx.Configuration.KeyboardShortcut` is the right type for user-bindable
  keys (supports modifiers, has `IsDown()` for polling in `Update()`), while a
  hardcoded `Input.GetKeyDown(KeyCode.F6)` is fine for a de-facto standard key —
  both plugins deliberately share F6 so the windows merge.
- Users edit the `.cfg` while the game is closed; it is plain INI-style text.

# Mijing Floor Selector (MijingSelector)

BepInEx 5 plugin for Shadow Dungeon. GUID `custom.mijingselector`, version 1.0.1.

Use the **Corrupted Realm** row in the floating *Mods* menu (docked to the right screen border)
to toggle an IMGUI window (window id 49313) that lets you jump
straight to any unlocked Mijing (秘境) floor without paging through the vanilla panel one
step at a time, and optionally raise your unlocked-floor cap.

## What it does

The window only appears where the Mijing system exists (the manager is a scene-scoped
singleton that lives in the home town and inside levels). It shows:

- Current difficulty (Easy / Medium / Hard / Master) and current Mijing floor.
- The highest unlocked floor ("cap") for the current difficulty, read from the save.
- A target-floor field with `-10` / `-1` / `+1` / `+10` buttons.
- **Enter floor** - enters the target floor, always clamped to `[1, unlocked cap]`.
  The button is disabled (with a gray line explaining why) unless all of the game's own
  preconditions hold: a save is loaded, Mijing is unlocked on that save, you are standing
  in the home town (`HomeScene`) or already inside a Mijing floor, the Mijing level list
  has been loaded, and no floor transition is already in progress.
- **Set unlocked cap to target** (guarded by a **Confirm** checkbox) - raises the cap so
  higher floors become enterable.

### The cap-raise button writes save progression

**Prominent note:** "Set unlocked cap to target" calls the game's own
`MijingManager.SetUnlockedFloorByCurrentDifficultyMax(int)`, which writes
`mijingFloor_easy/medium/hard/master` into the live save data (persisted on the game's next
save). It respects the game's own clamps: it can only **raise** the cap for the **current
difficulty**, never lower it (the game floors it at 1 and ignores values below the current
cap). This is exactly the code path the game itself runs when you clear a floor - but you
are still skipping progression, so use deliberately. Set `AllowRaisingCap = false` in the
config to hide the button entirely.

Entering a floor is kept honestly separate: **Enter floor** never goes above the cap; to
go higher you must first raise the cap explicitly via the confirmed button.

## Config (`BepInEx/config/custom.mijingselector.cfg`)

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | AllowRaisingCap | true | Show the confirmed "Set unlocked cap to target" button. |

## Hotkeys

None. The window is toggled from the *Mods* menu row **Corrupted Realm**; the former F10
binding was removed. F6 (a third-party plugin) is the only hotkey still in use.

## Game methods used (no Harmony patches; all public API, verified against decompiled source)

- `Mijing.MijingManager` (a `SingletonMonoScope<MijingManager>`; accessed only via
  `HasInstance` / `Instance`):
  - `EnterMijing(int floor)` - starts the async floor load; internally requires
    `HomeScene` or an existing Mijing floor and non-empty `mijingIds` (mirrored as button
    preconditions so its error paths are never hit), and ignores re-entrant calls while
    `IsEnteringMijing` is true.
  - `GetCurrentFloor()`, `GetUnlockedFloorByCurrentDifficulty()`,
    `SetUnlockedFloorByCurrentDifficultyMax(int)`, `CurrentDifficulty`, `IsEnteringMijing`,
    static `mijingIds`.
- `SaveManager.HasRuntime`, `SaveManager.RuntimeData.UnlockedMijing` (read only).
- `LevelManager.GetIsMijing()` (static), `UnityEngine.SceneManagement.SceneManager.GetActiveScene()`.

## Known limitations

- **Entry price is bypassed.** The vanilla Mijing panel charges gold
  (`GetEnterPriceMultiplier(floor)`) before calling `EnterMijing`; this plugin calls
  `EnterMijing` directly and does not charge you.
- `EnterMijing(floor)` itself raises the cap to the entered floor via
  `SetUnlockedFloorByCurrentDifficultyMax` - irrelevant here because Enter is clamped to
  the cap, but worth knowing if you script it.
- The difficulty shown/used is whatever `MijingManager.CurrentDifficulty` currently is
  (set by the vanilla panel's difficulty buttons). This plugin does not change difficulty -
  open the vanilla Mijing panel to switch it.
- There is no maximum-floor constant in the game; the target field is soft-capped at 99999.
- If the Mijing API ever fails at runtime (e.g. after a game update), the window logs one
  error and disables itself for the session instead of spamming.

## Additional limitation

While the target-floor text field has keyboard focus, typed keys still reach the game (Unity legacy Input cannot be suppressed from IMGUI), so digits/letters may trigger in-game hotkeys. Prefer the -10/-1/+1/+10 buttons during combat.

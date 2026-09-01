# Quick Enhance (custom.quickenhance) 1.0.0

BepInEx 5 plugin for Shadow Dungeon. Holding **Shift** while clicking in the weapon
enhance panel runs enhancement repeatedly - one attempt per frame - until the weapon
hits its +max, you run out of gold, or the per-burst iteration cap is reached,
instead of one +level per click. A plain click (without Shift) behaves exactly like
vanilla.

## How it works

- The game performs one enhancement per left click via the private method
  `UI.Panels.WeaponManager.HandleEnhInput()`, which `WeaponManager.Update()` calls every
  frame while `GameUIManager.CurrentModalState == GlobalUiModalState.WeaponEnh`.
- This plugin installs a **Harmony prefix on `WeaponManager.HandleEnhInput`**. On a
  Shift-click it suppresses the vanilla single action and starts a coroutine on the
  plugin MonoBehaviour that, each frame, re-runs the game's own guard chain via cached
  reflection handles and performs one enhancement:
  - `WeaponManager.RefreshForgeContext(WeaponForgeMode.Enh)` (private, private enum arg)
  - `WeaponManager.forgeContext.IsValid` / `.RuntimeWeapon` (private nested `WeaponForgeContext`)
  - `WeaponManager.CanTryForgeEnh()` (private; hand-item, remaining-count and money guards -
    its vanilla fail tip still shows once on the attempt that ends the burst)
  - `WeaponManager.GetRemainEnhanceCount(WeaponClass)` (private static; belt-and-braces re-check)
  - `WeaponManager.TryRandomEnh()` (private; the actual enhancement)
  - `WeaponManager.IsSubmitDown()` (private static; used by the prefix so gamepad Submit
    works the same as a mouse click)
- The burst stops immediately when: the enhance modal closes or changes, the plugin is
  disabled, the `WeaponManager` is destroyed (scene change), an attempt makes no progress
  (weapon `ZQ_CountMax` did not increase - e.g. a weapon with no enhanceable stat), any
  guard fails, or `MaxIterationsPerBurst` is reached.
- After each burst one summary line is logged: `Quick Enhance: from +X to +Y
  (N enhancement(s), M attempt(s)).`
- All reflection is resolved once in `Awake`. If any member is missing (game update),
  the plugin logs a single warning and stays inert - no patch, no per-frame work. Any
  runtime reflection failure aborts the burst and disables the feature with one warning.

## Config (BepInEx/config/custom.quickenhance.cfg)

| Entry | Default | Description |
| --- | --- | --- |
| `QuickEnhance.Enabled` | `true` | Master switch. When false the plugin never intercepts the panel and any running burst stops. |
| `QuickEnhance.RequireShift` | `true` | Only Shift-clicks start a burst; plain clicks stay vanilla. Set to `false` to make **every** enhance click loop. |
| `QuickEnhance.MaxIterationsPerBurst` | `40` (1-500) | Safety cap on attempts per burst (one per frame). |

## Hotkeys

- No dedicated hotkey and no IMGUI window (no window ids used). The trigger is
  **Left/Right Shift held during the enhance click** (mouse left button, or gamepad
  Submit when a gamepad is the current input device).
- Does not touch reserved keys F6/F8/F9/F10/F11.

## Exact game methods hooked / invoked

- **Patched (Harmony prefix):** `UI.Panels.WeaponManager.HandleEnhInput()` (private, parameterless).
- **Invoked via cached reflection:** `WeaponManager.RefreshForgeContext(WeaponForgeMode)`,
  `WeaponManager.CanTryForgeEnh()`, `WeaponManager.TryRandomEnh()`,
  `WeaponManager.GetRemainEnhanceCount(WeaponClass)`, `WeaponManager.IsSubmitDown()`;
  fields `WeaponManager.forgeContext`, `WeaponForgeContext.IsValid`,
  `WeaponForgeContext.RuntimeWeapon`; plus public reads of
  `GameUIManager.CurrentModalState`, `WeaponClass.ZQ_CountMax`.

## Known limitations

- The enhance sound and the stat tip pop-up fire once per enhancement, so a long burst
  plays the sound on consecutive frames (that is the game's own feedback per level).
- The burst enhances whatever weapon the game's forge context resolves each frame (the
  slot under the mouse via `ContainerGridUtil.GetMainSlot`); moving the mouse to another
  weapon mid-burst continues on the newly resolved weapon, exactly as rapid manual
  clicking would.
- "Out of budget" means the game's own money guard (`CanTryForgeEnh`) fails - there is no
  separate spend-limit setting beyond `MaxIterationsPerBurst`.
- If a game update renames the private members listed above, the plugin disables itself
  with a single warning at startup rather than patching blindly.

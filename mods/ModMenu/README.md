# Mod Menu (`custom.modmenu`)

A single, always-visible, translucent panel docked to the **right screen border**, titled **"Mods"**.
It collects the interaction points of every other custom plugin into one list of rows, each row showing
a procedurally generated icon, a short label, and its current state (ON / OFF) or an action chevron.

This plugin exists so the setup no longer needs a fistful of keyboard shortcuts. **All hotkeys in our
own plugins have been retired in favour of this menu.** The only key still bound anywhere is **F6**,
which belongs to a third-party plugin we do not touch.

- Assembly / namespace: `ModMenu`
- Version: `1.0.1`
- Provider contract: **v2** (adds an optional per-row description shown as a hover tooltip; v1 4-cell
  rows keep working unchanged).
- Binds **no** hotkey at all. The panel *is* the interaction point.
- No Harmony patching. It only reads other assemblies by reflection and draws IMGUI.

## How it looks / behaves

- Docked flush to the right edge, vertically centered by default.
- Header row reads `Mods` and is itself a click target: clicking it collapses the list to a narrow
  vertical `MODS` tab so the game is unobstructed. The collapse state is persisted in the config file.
- Each row: `[icon] label [pill]`.
  - Icon: an 18x18 `Texture2D` generated in code (no external assets). Shape (circle, rounded square,
    triangle, diamond, ring, bars) and hue are derived from a stable FNV-1a hash of the row id, so a
    row always looks the same across sessions. Cached statically, generated once.
  - Pill: rows that expose a state show a lit green `ON` or a dim grey `OFF`. Pure action rows show a
    small `>` chevron instead.
- Rows brighten on mouse hover and, after a short rest, show a description tooltip to the left of the
  panel (see **Hover tooltips** below). Mouse only, no keyboard handling whatsoever.
- The whole panel is drawn with a global `GUI.color` alpha (`Opacity`), so the game stays visible
  through it. Backgrounds are 1x1 generated textures rather than `GUI.skin.box`, so the alpha is exact.
- Clicks are consumed (`Event.current.Use()`) while the cursor is inside the panel rect, so a click on
  a row is not also handed to anything else that draws IMGUI later in the same frame.
- If no provider is found the panel still draws the header plus a dim `no mods registered` row, which
  makes a load-order or contract problem obvious instead of silent.
- If more rows exist than fit on screen, the last visible slot becomes a `+N more` hint (there is no
  scrolling): lower `RowHeight` or `Scale`.

## Hover tooltips

Rest the pointer on a row and, after `HoverDelaySeconds`, a description panel appears explaining what
that row does.

- **Placement.** The menu is docked to the right border, so the tooltip is drawn to the **left** of the
  panel (`260` scaled px wide, with a `6` px gap). Its top is aligned with the hovered row, then clamped
  so the whole box stays fully on screen — vertically it is pushed up when it would run off the bottom,
  and horizontally it is pinned to the left border in the extreme case of a very wide panel on a very
  narrow screen. It never clips: the box height is derived from `GUIStyle.CalcHeight` on the word-wrapped
  text, using the exact style and width the text is then drawn with.
- **Content.** First line is the row's own current label (bold, full white); below it, the description,
  word-wrapped, in a slightly dimmer grey.
- **Look.** The same flat generated-texture treatment as the panel and the same global-alpha idea, but
  deliberately **more opaque than the rows** (`Opacity + 0.35`, clamped) so the body text stays readable
  over a busy scene. It is drawn **after** the panel in the same `OnGUI` pass, so IMGUI's immediate draw
  order guarantees it is never covered by the panel or its rows.
- **Timing.** The pointer must rest on the *same* row for `HoverDelaySeconds`; moving to another row (or
  off the panel) resets the timer. The clock is `Time.unscaledTime`, so tooltips work while the game is
  paused. Collapsing the panel resets the timer too, so re-expanding never pops a stale tooltip.
- **Rows without a description** (contract-v1 rows, a `null` 5th cell, or a description that returns
  `null`/empty) simply show no tooltip. This is not an error and is not logged.
- Set `ShowTooltips = false` to turn the whole feature off; nothing else about the panel changes.

## Config

File: `BepInEx/config/custom.modmenu.cfg`

| Section | Key | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| General | `Enabled` | bool | `true` | Master switch. When false nothing is drawn and no assemblies are scanned. |
| General | `Collapsed` | bool | `false` | Persisted collapse state (also toggled by clicking the header / tab). |
| Layout | `DockOffsetY` | float | `0` | Vertical offset from the centered dock position. `0` = centered, negative = up. Always clamped on screen. |
| Layout | `Width` | float | `190` | Panel width in scaled pixels (clamped 120-420). |
| Layout | `RowHeight` | float | `30` | Row height in scaled pixels (clamped 18-60). |
| Appearance | `Opacity` | float | `0.55` | Global panel alpha (clamped 0.15-1.0). |
| Appearance | `Scale` | float | `1.0` | Uniform UI scale of the panel (clamped 0.75-2.0). |
| Tooltips | `ShowTooltips` | bool | `true` | Show a description tooltip to the left of the panel while the pointer rests on a row. Rows whose plugin supplies no description never show one. |
| Tooltips | `HoverDelaySeconds` | float | `0.35` | How long the pointer must rest on the same row before its tooltip appears (clamped 0-2). `0` = instantly. Measured in unscaled time, so it also works while the game is paused. |

## Provider contract v2 (copy this verbatim into a new plugin to join the menu)

Each participating plugin declares, in its own root namespace, this EXACT type:

```csharp
public static class ModMenuProvider
{
    // Each row: new object[] { string id, Func<string> label, Func<bool> state, Action onClick, Func<string> description }
    //   id         : stable unique string, e.g. "summonall.toggle"
    //   label      : short row text, may change per frame (e.g. "Summon All" / "Dismiss All (23)")
    //   state      : null for pure actions; otherwise true = ON/active (menu draws it lit)
    //   onClick    : performs the action / flips the toggle
    //   description: OPTIONAL 5th element - one or two short sentences shown as a hover tooltip,
    //                explaining what the row DOES and, where useful, what its current state means.
    //                Plain text, no markup, aim for 60-160 characters.
    public static object[][] GetMenuItems() { ... }
}
```

**The 5th element is OPTIONAL:** a 4-element row (no description) still works exactly as before and
simply shows no tooltip. Everything else is unchanged from v1: `state` may be `null` for pure actions,
all delegates are the framework types `Func<string>` / `Func<bool>` / `Action`, and no delegate may
throw (try/catch with a safe fallback inside every body).

Rules: never throw from `GetMenuItems`, `label()`, `state()`, `onClick()` or `description()` - wrap bodies
in try/catch and return safe fallbacks. Delegates must be `Func<string>`, `Func<bool>`, `Action`
(framework types, so they marshal across assemblies). Rows are drawn in the order returned. Keep labels
under ~22 chars.

### What the host does with it

- Every ~2 seconds it looks for **newly loaded** assemblies only (plugin load order is not guaranteed),
  and inside each one for a type whose `Name` is `ModMenuProvider` that is a static class
  (`IsClass && IsAbstract && IsSealed`) with a public static parameterless `GetMenuItems`.
  `GetTypes()` is never called twice on the same assembly; `ReflectionTypeLoadException` and any other
  failure is caught per assembly.
- `GetMenuItems()` is invoked once and the resulting rows are cached; only `label()` / `state()` are
  re-read while drawing. A provider that returns zero rows is retried on the next tick (its subsystem
  may not have been ready), which costs one delegate call and no extra reflection.
- Cell `[4]` is read only when `row.Length >= 5` **and** it casts successfully to `Func<string>`; anything
  else (absent, `null`, wrong type) means "this row has no tooltip" and is accepted silently.
- A provider that **throws** from `GetMenuItems()` is dropped permanently with exactly one logged
  warning. A row whose `label()`, `state()`, `onClick()` or `description()` throws is degraded (label
  shows `(label error)`, state stops being queried, the row stops firing, the tooltip stops being built)
  with one warning each.
- Malformed rows (missing id, missing `Func<string>` label, wrong delegate types, fewer than 4 cells)
  are skipped with a warning; the rest of the provider still works.
- Row order is deterministic: grouped by provider assembly name (ordinal), then provider type name,
  then the order the provider returned its rows. The menu never reshuffles between sessions.

## Performance notes

`OnGUI` runs several times per frame (Layout, MouseMove, MouseDown, Repaint, ...), so the draw path is
allocation-free after warmup: `GUIStyle`s are built on the first `OnGUI`, all background/pill textures
and row icons are cached statically, every fixed string is a `const`, each row reuses one `GUIContent`
whose `text` is only written when the provider's label actually changed, and the only composed string
(`+N more`) is rebuilt only when `N` changes. Reflection happens off the draw path, in `Update`, at most
once every 2 seconds and only for assemblies never seen before.

The tooltip follows the same discipline. `description()` is called at most **once per hovered-row change**,
plus one cheap re-read on the single `Layout` event of a frame so a live description can still update. The
returned string, the title string and both wrapped heights are cached; `GUIStyle.CalcHeight` runs **only**
when that text (or the wrap width) actually changed, never per event. The cache is invalidated when the
hovered row id changes and when row order is rebuilt. The steady-state Repaint path just reuses two
`GUIContent`s and constructs `Rect`/`Color` structs, so it allocates nothing.

## Known limitations

- `Event.current.Use()` blocks other **IMGUI** consumers. It does not participate in Unity's uGUI /
  `EventSystem` raycasting, so a click on the panel can in principle also be seen by a uGUI element
  sitting underneath it. The dock position (flush right edge, vertically centered) is chosen to avoid
  the game's own HUD clusters; move it with `DockOffsetY` if it ever overlaps something clickable.
- The panel is drawn whenever the game renders, including menus and loading screens, because the host
  intentionally does not reference `Assembly-CSharp` and therefore has no notion of "in a run". Set
  `Enabled = false` (or collapse it) if that is ever a problem.

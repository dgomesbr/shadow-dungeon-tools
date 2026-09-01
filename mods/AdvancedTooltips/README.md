# Advanced Tooltips (custom.advancedtooltips) v1.0.0

BepInEx 5 plugin for Shadow Dungeon. Two purely display-only tooltip upgrades, each behind its
own config switch. Nothing about items, drops, or saves is ever modified.

## Features

### A) Affix roll ranges on weapon tooltips
Every main-affix line of a weapon tooltip (both tip A and tip B, including the equipped-item
compare tooltip) gets a grey `(min~max)` suffix showing the possible roll range for that stat,
so you can judge how well the item rolled.

Ranges are recomputed from the live weapon template (`ItemManager.FindWeaponTemplate` resolves
the item's `Item_MB` table entry from the already-loaded template groups, no CSV parsing) using
the game's own generation formulas:

- Base Damage/Health/Mana: `Floor(table * 1.066^level * (1 +/- RandomCount) * GivePRC_Base)`,
  with the max extended by the "special affix rolled away" compensation multiplier (1.3-1.5x)
  when the item has no special affix but its template offers one. Displayed values include the
  weapon's base-value multiplier (double-stat enchant).
- Main-affix stats: classified exactly like `ItemManager.GenerateWeaponStatValue` -
  recovery stats (indexes 3-6), integer-growth stats, mijing extra-integer stats, and the
  regular float stats scaled by the level/mijing roll band (0.9-1.0 up to 1.4-1.6).
  Values are formatted with the game's own `ItemManager.FormatWeaponStatValue`.
- Element lines: `FloorToInt((table/split + Floor(table * GivePRC_PRC)) * (1 +/- RDEL))`, with
  the split-count bounds derived from the table value exactly like `ApplyElement`.

Fail-soft rules: a suffix is only appended when the tooltip's line layout matches the expected
`WeaponClass.GetMain()` structure, the template stat can be matched unambiguously, and the
item's actual value still lies inside the computed range. Enhanced base stats
(`ZQ_CountMax > 0`) and enhanced elements (`JHEL_Count > 0`) are skipped, since those no longer
reflect the drop roll. Anything that cannot be verified simply shows no suffix.

### B) Ground-loot hover tooltips
Hovering an item lying on the ground shows its full, real tooltip without picking it up:

- Weapons/armor: the game's own `GameUIManager.ShowWPTipB(Vector3, WeaponClass)` anchored at
  the drop's screen position (same call the inventory character buttons use).
- Gems/runes and consumables: the game's private `FillGemTip` / `FillUseItemTip` +
  `RefreshWeaponTipLayout` + `LayoutSingleTip` helpers, replicating `ShowBSTip` / `ShowUseTip`
  but anchored at the drop instead of an inventory slot grid (those public methods require a
  slot grid, so no persistent anchor transform is needed - the layout methods take a screen
  position directly).

The tooltip hides again when the cursor leaves the drop. Suppressed while any modal state or
panel (inventory, shop, warehouse, character, talents, weapon/gem enhance) is open, while the
drop is still airborne, and for empty payloads. Weapon hover tooltips also get the feature-A
roll ranges automatically.

## Config (`BepInEx/config/custom.advancedtooltips.cfg`)

| Section  | Key                     | Default | Meaning                                   |
|----------|-------------------------|---------|-------------------------------------------|
| Tooltips | ShowAffixRollRanges     | true    | Append `(min~max)` to weapon affix lines. |
| Tooltips | GroundLootHoverTooltips | true    | Show real tooltips when hovering drops.   |

## Hotkeys
None. Both features are passive; toggle them via the config file. (No IMGUI windows are used;
the reserved window-id range 49300-49399 is left unused.)

## Game methods hooked (Harmony)
- Postfix `GameUIManager.FillWeaponTipA(WeaponClass, Vector2)` - appends ranges to `WP_mainA`.
- Postfix `GameUIManager.FillWeaponTipB(WeaponClass)` - appends ranges to `WP_mainB`.
  (These are the fill methods behind `ShowWPTipA`/`ShowWPTipB`/`ShowCompareWeaponTips`, so all
  weapon-tooltip entry points are covered.)
- Postfix `DropItem.OnHover(bool)` - shows/hides the hover tooltip for ground loot.

Called via reflection (cached `MethodInfo`/delegates, resolved once in `Awake`):
`ItemManager.FindWeaponTemplate`, `IsWeaponIntegerGrowthIndex`, `IsMijingExtraIntegerIndex`,
`IsWeaponFloatWholeIndex`, `IsWeaponFloatOneDecimalIndex`, `GameUIManager.FillGemTip`,
`FillUseItemTip`, `LayoutSingleTip`, `RefreshWeaponTipLayout`. If any of them fails to resolve,
the affected feature logs one warning and disables itself; runtime exceptions inside a hook
disable that feature for the session after logging once.

## Known limitations
- Level-100 mijing items: `GivePRC_Base`/`GivePRC_PRC` are evaluated with the *current* scene's
  mijing difficulty context (the game's context struct is private). Inspecting such an item in
  a different scene can make the computed range miss - the containment check then hides the
  suffix rather than showing a wrong one.
- Main-affix ranges are skipped for a whole tooltip when the game renders fewer lines than the
  weapon has main-affix entries (exotic stat indexes that produce no display line), because the
  line-to-stat mapping would be ambiguous.
- Stats whose table entry appears twice with different base values, and stats copied verbatim
  from the table (no roll), show no range suffix.
- Base-stat ranges include the worst-case "no special affix" compensation in the max, so they
  can look wide on items whose template offers a special affix.
- If a hovered drop is picked up or despawns without a hover-exit event, the tooltip stays until
  the next tooltip is shown or hidden by the game.
- Ranges reflect the current game version's formulas (ItemManager.SetWPdata / 
  GenerateWeaponStatValue / ApplyElement); a game update that rebalances those bands will make
  suffixes disappear (containment check) rather than show wrong numbers.

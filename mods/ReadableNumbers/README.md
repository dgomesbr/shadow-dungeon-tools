# Readable Numbers (custom.readablenumbers) v1.0.0

BepInEx 5 plugin for Shadow Dungeon. Adds thousands separators to the game's big numbers.
Community ask: end-game damage reaches trillions and the vanilla unit-suffix-only formatting
("1.2 T") hides how big hits actually are.

## What it does

- **Combat damage text** (floating hit numbers) and the **DPS meter** are reformatted with
  grouped digits. Both go through the same game function, so one patch covers both:
  `DPSManager.FormatDamageNumber(float)` just delegates to `DamgeTextManager.FormatDamageNumber(float)`.
- **Gold counter** in the inventory UI gets thousands separators too (optional, on by default).

Vanilla behavior is preserved exactly for zero/negative values (`"0"`), values of 1000 or less
(plain floored integer), and NaN/Infinity inputs - the prefix steps aside and lets the original
method run for those.

Formatting uses the invariant culture, so the group separator is always `,` regardless of the
OS locale.

## Config (BepInEx/config/custom.readablenumbers.cfg)

| Section | Key | Default | Meaning |
|---|---|---|---|
| Damage | `Mode` | `GroupedSuffix` | `GroupedSuffix`: grouped mantissa + short unit, ladder capped at B so trillions read as `1,234.5 B` and quadrillions as `1,234,500 B`. (The cap is deliberate: with the vanilla K/M/B/T/... ladder the mantissa never exceeds 3 digits, so separators would never appear.) `FullBelowBillion`: full grouped integer (e.g. `843,083,369`) below 1e9, GroupedSuffix style above. `FullAlways`: full grouped integer for every value (e.g. `43,083,369,558`). |
| Money | `FormatMoney` | `true` | Rewrite the inventory gold counter with grouped digits (`184,039,201` instead of `184039201`). |

## Hotkeys

None. This plugin has no UI and no windows (no IMGUI window ids used).

## Exact game methods hooked (Harmony, GUID `custom.readablenumbers`)

- `DamgeTextManager.FormatDamageNumber(float number)` - public static; **Prefix** that sets
  `__result` and returns `false` for finite values > 1000, returns `true` (vanilla path)
  otherwise.
- `InventoryManager.GlobalMoney` **property setter** (`set_GlobalMoney`) - **Postfix** that
  rewrites `moneyText.text` with the grouped value.
- `InventoryManager.Start()` (private) - same **Postfix**; this is the only other place the
  vanilla game writes `moneyText.text`.

All patches are applied in `Awake` with cached `MethodInfo` lookups via `AccessTools`. If a
target cannot be resolved, the plugin logs one warning and disables that feature. If formatting
ever throws at runtime, it logs the error once and permanently falls back to vanilla formatting
(never throws per frame / per hit).

## Known limitations

- **`FullAlways` label overflow**: combat text labels were sized for short strings like
  `1.2 T`. A full `43,083,369,558` is much wider; overlapping hits can visually collide and
  very large fonts (SCT scale near 3) may clip. `GroupedSuffix` or `FullBelowBillion` are the
  safe choices.
- **Float precision**: damage flows through the game as `float` (~7 significant digits). Above
  ~16.7 million the exact trailing digits shown by `FullBelowBillion`/`FullAlways` are the
  float's nearest representable value, not the true running total. This is a limitation of the
  game's own pipeline, not of this plugin; the grouped-suffix mode is unaffected in practice.
- **Shop/forge/price labels are NOT reformatted**: only the main gold counter has a clean
  patch point (two `GlobalMoney.ToString()` writes inside `InventoryManager`). Price texts
  elsewhere are scattered raw `ToString()` calls and localization-format calls (e.g.
  `BaoshiManager.cs:963` builds price strings through `LOC.MM.GetLevelFormat("mijing_need_price", ...)`
  with inline color markup; similar patterns exist in `ShopManager`/`WeaponManager`). Patching
  those would mean rewriting localized rich-text strings per panel - out of scope.
- **GroupedSuffix precision detail**: vanilla always shows one decimal for mantissas <= 100
  (`5.0 K`); this plugin drops a trailing `.0` (`5 K`). Cosmetic only.

## Build

`dotnet build` against the absolute `HintPath` references in `ReadableNumbers.csproj`
(netstandard2.0). Copy `ReadableNumbers.dll` to `BepInEx/plugins/`.

## v1.1.0

New default mode **NamedUnits**: numbers are shown at their nearest short-scale named unit - `510 Billion`, `1.2 Trillion`, `3.4 Quadrillion`, up through Quintillion, Sextillion, Septillion, Octillion, Nonillion, Decillion, Undecillion (the full float range). Values below one million show as full grouped integers. The previous GroupedSuffix/FullBelowBillion/FullAlways modes remain available via the Mode config entry.

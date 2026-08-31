# Shadow Dungeon item icons

Extracted by `pipeline/extract-icons.py` into `web/public/icons/` (5,197 unique
PNGs + `icons-index.json`). Game files were read-only inputs.

## Where the icons live in the game assets

| What | Location |
|---|---|
| Sprites + sheet textures | `sharedassets1.assets` (pixel data in `sharedassets1.assets.resS`) |
| `IconData` ScriptableObjects (one per sheet, `public Sprite[] icon`) | `sharedassets1.assets`, path_ids ~126881–126915 |
| `ItemManager` MonoBehaviour (wires everything together) | `level1` scene, path_id 10899 |
| `ItemManager` / `IconData` MonoScripts | `globalgamemanagers.assets` (path_ids 1473 / 2171) |
| Item tables (CSV despite look) | TextAssets `0 0 Weapon`, `0 2 Baoshi`, `0 3 UseItem`, `0 5 Set` in `sharedassets1.assets` |

The game never looks icons up by name. `ItemIconUtil.GetWeaponIcon` resolves
`ItemManager.IconData[row.IconType].icon[row.Icon]` — pure array indexing on
serialized Sprite arrays. The extractor replays that offline via UnityPy
(1.25.3) with typetrees generated from `Managed/*.dll` (TypeTreeGeneratorAPI).

## Naming convention

Every icon sheet is a grid-sliced texture whose sprites are named
`<SheetName>_<index>`, and — verified across all 5,197 sprites with zero
exceptions — **the array position always equals the name suffix**:
`IconData["SwordA"].icon[7]` is the sprite `SwordA_7`. So `(IconType, Icon)`
resolves to a file with no lookup table beyond the IconType→sheet map below.

Inventory grid cell = **60 px**. A sprite's logical rect is
`SizeX*60 × SizeY*60` (e.g. 2×4 staff → 120×240). In the assets the sprites
are tight-trimmed (settingsRaw 64, unrotated, unpacked); the extractor pastes
the trimmed pixels back onto the full transparent rect at
`textureRectOffset`, so every PNG in a sheet has uniform dimensions and the
game's in-cell alignment.

### IconType → sheet (order of `ItemManager.IconData[]`, level1)

```
 0 StaffC    1 StaffD    2 StaffB    3 StaffA    4 SpellA    5 SpellB
 6 SwordA    7 SwordB    8 SwordC    9 ShieldA  10 ShieldB  11 ShieldC
12 BowB     13 BowC     14 BowA     15 ArrowC   16 ArrowB   17 ArrowA
18 StickB   19 StickD   20 StickC   21 StickA   22 CorpseC  23 CorpseB
24 CorpseA  25 HeadA    26 ArmorA   27 HandA    28 ShoesA   29 CrossA
30 PearlA   31 RingA    32 JewelA
```

Plus two dedicated sheets: `IconBaoshi` = **LittleC** (289 gem icons) and
`IconUse` = **LittleA** (289 consumable icons).

## Output layout

```
web/public/icons/
  weapons/<Sheet>_<i>.png      4,619 files, ~85 MB  (60x60 up to 120x240)
  gems/LittleC_<i>.png           289 files, ~0.9 MB (60x60)
  consumables/LittleA_<i>.png    289 files, ~0.9 MB (60x60)
  icons-index.json             sprite name -> {path, w, h} + ordered arrays
```

Total ≈ 87 MB. Individual PNGs are 3–35 KB (lossless, `optimize=True`), so no
spritesheet variant was emitted — the total is driven by icon *count*, not
per-file bloat; repacking 4.6k mixed-size sprites wouldn't shrink it
meaningfully and adds client complexity. If a bundle is ever wanted, the
cheapest lossless route is exporting the 35 original sheet Texture2Ds and
using `m_Rect`-based CSS `background-position` (all sprites are unrotated).

## Resolving an item to its icon path

### Weapons / armor / accessories / set pieces (`0 0 Weapon` table)

A save item carries `(PLtype, CharType, Quality, GlobalID)`. Find the table
row with the same `GlobalID` within the matching `(PLtype, CharType, Quality)`
bucket — note `PLtype == 1000` means "generic / all classes" (the game clones
those rows into every class bucket; for matching, treat 1000 as a wildcard).
Relevant 0-based CSV columns (per `ItemManager.LoadData_WP`):

| col | field | col | field |
|---|---|---|---|
| 2 | ItemName | 9 | **IconType** |
| 3 | GlobalID | 10 | **Icon** |
| 5 | Quality (0 Normal…6 Mythical) | 14 | PLtype (0–3 class, 1000 generic) |
| 6/7 | SizeX/SizeY | 16 | CharType (slot category) |

Then: `path = icons/weapons/<SHEET[IconType]>_<Icon>.png`, or via the index:
`weaponIconTypes[IconType].sprites[Icon]` → key into `sprites`.

**Set pieces have no separate sheet** — they are ordinary weapon-table rows
(Necromancer set armor: PLtype 3, sheets HeadA/ArmorA/HandA/ShoesA), resolved
exactly as above.

### Gems / Baoshi (`0 2 Baoshi` table)

CSV col 5 (0-based; per `LoadData_BS`) is the `Icon` index into LittleC:
`icons/gems/LittleC_<Icon>.png` (`gemIcons.sprites[Icon]` in the index).
Runtime overrides from `ItemIconUtil.GetBaoshiIcon` for rune-type gems:

- `UseType 3` (skill rune): `special.skillRuneByElement[EL]` — LittleC_75–80
  for EL 0–5 (Fire, Frozen, Thunder, Poison, Physics, Shadow)
- `UseType 4` (SPC rune): `special.spcRune` = LittleC_81
- `UseType 5` (base rune): `special.baseRune` = LittleC_82
- `Double_Icon` (doubled-base-value badges): LittleC_64–66

### Consumables / UseItem (`0 3 UseItem` table)

CSV col 5 (0-based; per `LoadData_USE`) is the `Icon` index into LittleA:
`icons/consumables/LittleA_<Icon>.png` (`useItemIcons.sprites[Icon]`).

### icons-index.json schema

```jsonc
{
  "cellSizePx": 60,
  "sprites":         { "SwordA_0": { "path": "icons/weapons/SwordA_0.png", "w": 60, "h": 180 }, ... },
  "weaponIconTypes": [ { "iconType": 0, "sheet": "StaffC", "sprites": ["StaffC_0", ...] }, ... ],
  "gemIcons":        { "sheet": "LittleC", "sprites": [...] },   // index == Baoshi CSV Icon col
  "useItemIcons":    { "sheet": "LittleA", "sprites": [...] },   // index == UseItem CSV Icon col
  "special":         { "skillRuneByElement": [...], "spcRune": "...", "baseRune": "...", "doubleIcons": [...] }
}
```

Web-app recipe: load the index once; for a weapon do
`idx.sprites[idx.weaponIconTypes[iconType].sprites[icon]].path`; for
gems/consumables use `gemIcons`/`useItemIcons` the same way. Since the
name-suffix invariant holds, `icons/weapons/${SHEET[iconType]}_${icon}.png`
also works without the index.

## Re-running

```
python pipeline/extract-icons.py
```

Requires `pip install UnityPy` (pulls TypeTreeGeneratorAPI). ~90 s: loads
level1 + sharedassets + globalgamemanagers, finds the ItemManager
MonoBehaviour by MonoScript reference (no hardcoded path_ids), and rewrites
`web/public/icons/` and the index. Output order is deterministic; array order
in the index is the game's serialized order and must never be re-sorted.

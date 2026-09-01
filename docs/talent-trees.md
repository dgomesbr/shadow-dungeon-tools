# Shadow Dungeon talent trees

Extracted by `pipeline/extract-talents.py` into
`web/public/data/talent-trees.json` + `web/public/icons/skills/` (459 PNGs,
all 64×64). Game files were read-only inputs. Positions are **real Unity
scene coordinates** from the `level1` talent panel, not a synthesized layout
(`"synthesized": false` in the JSON root).

## Where the layout lives in the game assets

The talent panel is a scene-baked UI in `level1` under
`UICanvas/Talent/Tree`. The `TalentManager` MonoBehaviour (scene singleton,
class in `Assembly-CSharp.dll`) wires everything together:

| TalentManager field | What it is |
|---|---|
| `XiCAV : CanvasGroup[12]` | one `Tree (N)` container GameObject per subclass tree (Xi 0–11) |
| `DFXiCAV : CanvasGroup` | the shared `Tree DF` container (Divine Favor / "Paragon Talents", Xi 12 here) |
| `iconDT : IconData[12]` | colored icon sheets `Icon SkillA 00`…`11`, one per Xi (33–35 sprites each) |
| `iconDTB : IconData[12]` | greyscale locked variants of the same sheets (not exported) |
| `SPCA` / `SPCB : IconData` | Divine-Favor icon sheets (512 sprites; colored / locked) |
| `skillTA : TextAsset[10]` | CSV tables: `0 SampleF`, `1 SampleS`, `2 CompF`, `3 CompS`, `4 DotF`, `5 DotS`, `6 Bei`, `7 DF`, `8 Change`, `9 CP Change` |
| `XiTA : TextAsset` | Xi table — 13 rows of tree metadata; col `IndexName` is the English tree name (rows 0–11 = subclasses, row 12 = `Paragon Talents`) |

Every visible tree node is a **`SkillBT`** MonoBehaviour (serialized fields
`IndexName`, `Xi`, `SkillType`) on a `Bottom` GameObject inside a
`SkillBT (n)` group under its `Tree (N)` container. `level1` contains 446
`SkillBT` instances: 398 with an `IndexName` (exactly the 398 rows of
`skills.json`) plus 48 empty-named nodes under an unused `Tree MB` template
that the extractor skips. Divine Favor nodes are **`SKillBT_DF`** (field
`Index` 0–49, matching the 50 DF table rows), and the 8 per-column point
counters at the bottom of the DF page are **`SKillBT_Lie`** (field `Type`
0–7).

A node's position is its RectTransform local position accumulated (scale-
aware) up the parent chain to the tree container — that sum is the node's
scene-local coordinate inside `Tree (N)`. `TalentManager.RegisterSkillBT`
/ `TryBindSkillBT` binds each scene button to the CSV row with the same
`(Xi, SkillType, IndexName)`, which is how the extractor associates
positions with skills (and it verifies each node actually sits under the
`Tree (N)` matching its `Xi`).

### Measured grid

* Subclass trees (Xi 0–11): 6 columns × 8 rows, spacing **151 px × 160 px**.
  Rows correlate exactly with `UnLock_Point`: the unlock-0 row is at the
  bottom, unlock-34 at the top (in-game the tree grows upward).
* Divine Favor: 8 columns × 8 node rows (50 nodes; upper rows are sparser),
  spacing ≈ **113.8 px × 140 px**, plus the `SKillBT_Lie` column-counter row
  below the nodes (exported as `columns` on tree 12). Row unlocks bottom-up:
  0 / 20 / 50 / 80 / 100 / 120 / 150 / 160; `unlock` here means *points
  invested in the node's `lie` column(s)*
  (`TalentManager.IsDFLieRequirementMet`), not tree points.

## How icons resolve

The game never looks skill icons up by name. Each CSV row carries an `icon`
index and the loaders do pure array indexing
(`TalentManager.LoadData_SampleF` etc.):

```csharp
skill.icon  = iconDT [row.Xi].icon[row.icon];   // colored
skill.iconB = iconDTB[row.Xi].icon[row.icon];   // greyscale (locked)
```

Divine Favor choices index the shared sheet instead
(`TalentManager.GetDFIconByIndex`): `SPCA.icon[lit.Icon]` (colored) /
`SPCB.icon[lit.Icon]` (locked).

The extractor replays this offline (UnityPy 1.25.3 + typetrees from
`Managed/*.dll`, same pipeline as `extract-icons.py`), recomposing the
tight-trimmed sprites onto their full 64×64 rect, and writes only the
referenced sprites:

* `web/public/icons/skills/xi{NN}_{i}.png` — `iconDT[NN].icon[i]`
* `web/public/icons/skills/df_{i}.png` — `SPCA.icon[i]`

Node objects in the JSON carry the ready-made relative path
(`icons/skills/xi00_1.png`), so no index math is needed in the web app.
Greyscale variants are not exported (grey out with CSS if needed).

## Node connections (links)

The scene does not encode the connector lines as meaningful objects;
the prerequisite graph lives in the CSV tables, and that is what the UI
lines depict and what `TalentManager` enforces:

* Son tables (`1 SampleS`, `3 CompS`, `5 DotS`) have `FrontSkill`,
  `FrontSkillType`, `FatherSkill` columns. A son is unlockable when its
  `FrontSkill` has points (see `TalentManager.SetSkillBT` /
  `IsNormalSkillUnlocked`). Links are emitted as `[FrontSkill, son]` —
  267 links across the 12 subclass trees. Father-type skills (`sampleF`,
  `compF`, `dotF`) and passives (`bei`) have no parent link; they gate on
  tree points (`UnLock_Point` ≤ points spent in that tree).
* The DF table has `FA`/`FB`/`FC` parent node indices
  (`TalentManager.IsDFFatherRequirementMet`); emitted as
  `["DF_{parent}", "DF_{child}"]` — 56 links.

## Archetype grouping (verified in code)

Four playable characters (PlayerType 0–3) each own three consecutive Xi:

* `TalentManager.SetStart(...)` switches on `PL.PLType` and enables
  `XiCAV[0..2]` for type 0, `[3..5]` for 1, `[6..8]` for 2, `[9..11]` for 3.
* `TalentManager.GetAvailableShortcutTalentPages()` iterates
  `PL.PLType * 3` … `+3`.
* `TalentManager.AddSkillFW(...)` maps `skill.Xi / 3` → character,
  `skill.Xi % 3` → tree (`SkillFWCharCount = 4`, `SkillFWXiPerChar = 3`).

English archetype names come from the `Start_FY` localization TextAsset
(`LOC.MM.GetStart("player_type{N}")`): 0 Mage, 1 Paladin, 2 Ranger,
3 Necromancer. The Divine Favor tree is shared by all four (unlocked at
character level 100, `TalentManager.DFTalentUnlockLevel`).

## `talent-trees.json` schema

```jsonc
{
  "generatedBy": "pipeline/extract-talents.py",
  "source": "…",
  "synthesized": false,          // positions are real scene coordinates
  "coordinateSpace": "…",
  "archetypes": [                // 4 entries
    { "name": "Mage", "classIds": [0, 1, 2] }, …
  ],
  "trees": [                     // 13 entries, xi 0..12
    {
      "xi": 0,
      "name": "Hell Messenger",  // classes.json IndexName (i18n key, English inline)
      "nodes": [
        {
          "skill": "FireBall",   // skills.json IndexName (save key)
          "type": "sampleF",     // sampleF|sampleS|compF|compS|dotF|dotS|bei|df
          "x": 302.0, "y": 1120.0,   // px from the tree's top-left node, y down
          "rawX": -76.5, "rawY": -498.8, // scene-local in Tree (N), y up
          "icon": "icons/skills/xi00_1.png",
          "max": 4,              // Level_Max
          "unlock": 0            // UnLock_Point (DF: points in its lie columns)
        }, …
      ],
      "links": [["FireBall", "Explosion"], …]  // [prerequisite, dependent]
    }, …
    // tree 12 ("Paragon Talents") extras:
    //   nodes[].skill  = "DF_{Index}" (the save key for DF talents)
    //   nodes[].lie    = column ids (0..7) the node belongs to / gates on
    //   nodes[].choices= up to 3 selectable skills {name, info, icon}
    //                    (save SelectedIndex: 0 none, 1..3 = choice)
    //   columns        = [{lie, x, y}] positions of the SKillBT_Lie counters
  ]
}
```

Lie columns 0–5 correspond to the six damage elements in
`TalentManager.GiveElement` order (fire, frozen, thunder, poison, physics,
shadow); 6–7 feed buff types 9/10 (`TalentManager.ApplyDFLieEffect`). Each
DF point also grants +0.2 per point of the node's column bonuses
(`DFLieBonusPerPoint`).

## Regenerating

```
python pipeline/extract-talents.py
```

Requires Python 3.12, UnityPy 1.25.3, Pillow, and the game installed at the
path hardcoded at the top of the script. Runtime ≈ 30 s. The script
cross-verifies against `web/public/data/skills.json` (every row placed in
exactly one tree, icon indices match) and asserts every emitted node has an
icon file on disk.

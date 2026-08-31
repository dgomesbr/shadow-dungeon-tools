# Affix index → display-name mapping

How the game turns a save-file affix tuple into a tooltip line, and the schema of
`web/public/data/affix-names.json` (generated from the decompiled code + localization).

Decompiled sources under
`C:\Users\dgome\AppData\LocalLow\OO Cat\ShadowDungeonSaveTool\decompiled\Assembly-CSharp\`;
localization (English column) from
`C:\Users\dgome\AppData\LocalLow\OO Cat\ShadowDungeonSaveTool\assets\extracted\Main_FY.json`
and `MainDisplay_FY.json`.

## Where the mapping lives in code

| What | Location |
|---|---|
| Main pool (`WPDT_A` in `Main`, Index < 2000) → line | `WeaponClass.cs:802` `GetMainArrayLine` (switch at :807), called from `GetMain` :525 via `AppendMainArrayLines` :774 |
| DOT pool (`WPDT_A` in `DOT`, Index ≥ 2000) → line | `WeaponClass.cs:1812` `GetDotArrayLine` (switch at :1835), called from `GetDot` :1793 |
| SK pool (`WPDT_B` in `SK`, Index 3000–3561) → line | `WeaponClass.cs:2045` `GetSKArrayLine` (switch at :2060), called from `GetSK` :2026 |
| CP pool (`WPDT_B` in `CP`, Index 4000–4417) → line | `WeaponClass.cs:2263` `GetCPArrayLine` (switch at :2278), called from `GetCP` :2244 |
| Set bonuses reuse the same four renderers | `WeaponClass.cs:2489` `GetSetArrayLine` — `lit.MainTP` 0=Main, 1=DOT, 2=SK, 3=CP |
| Value formatting (int / 1 decimal / free) | `ItemManager.cs:3714` `FormatWeaponStatValue`, `IsWeaponFloatWholeIndex` :3724, `IsWeaponFloatOneDecimalIndex` :3782 |
| Element helpers (EL 0–5 → label keys) | `SWS.cs:55` `DMtype`, `El_Name` :69, `El_DMG` :97, `El_Chuan` :125, `El_Anti` :153; `WeaponClass.cs:1647` `GetElementMainLabel`, `GetElementName` :1662 |
| Gem socket color/slot → `WPAocao.Type` 0–25 | `WeaponBaoshiApplyUtil.cs:73` `GetSocketType(baoshiType, weaponType)` |
| Socket `Type` 0–25 → player stat field | `SaveDataEquipmentSanitizer.cs:468` `GemFloatFields` (all applied as +N% of the stat) |
| Gem item tooltip text (labels per color/slot) | `BaoshiClass.cs:68` `GetMain` |
| Stat-application dictionaries (field names per index) | `SaveDataEquipmentSanitizer.cs:16` `MainFloatFields`, :185 `MainIntFields`, :249/:281 `MainElement*Fields`, :329 `MainBoolFields`, :365 `DotIntFields`, :392 `DotBoolFields` |

Localization lookups: `LOC.MM.Get("MainDisplay_FY.<key>")` for templates/labels
(`MainStat_*`, `MainDisplay_Label_*`, `DotStat_*`, `SKStat_*`, `CPStat_*`) and
`LOC.MM.GetMain(<key>)` for stat words from `Main_FY` (`damage`, `AttackSpeed`,
`fire chuan`, …). `ItemDisplayText` (`WeaponClass.cs:1885`) does the
`string.Format` of the template with the args.

## How the tooltip builds each line

### Main array (`GetMainArrayLine`)

For each non-zero `WPDT_A {Index, EL, number}` in `Main`:

1. `valueText = ItemManager.FormatWeaponStatValue(Index, number)` —
   whole-number indexes are `FloorToInt`'d; a second set formats `"0.0"`
   (one decimal); everything else formats `"0.##"`.
2. The `switch (Index)` picks a template. The common shapes (local functions
   at `WeaponClass.cs:1462-1617`):
   * `PlainPercent(label)` → `MainStat_PlainPercent` = `"{label} + {n}%"`
   * `PlainNumber(label)` → `"{label} + {n}"`; `MinusPercent` → `"{label} - {n}%"`
   * `MainText(key, args)` → arbitrary `MainStat_*` template with `valueText` and fixed literals (durations, stack caps, thresholds are hard-coded strings in the switch, e.g. `OnSkillCast(damage,"4","5")`)
   * `Enabled(label)` → `MainStat_Enabled` = `"{0}"` — presence-only line, `number` ignored
   * `ContextPercent/Number(key,label)` for the "for each equipped X" families (400–464)
3. Element substitution: only some indexes use `EL` —
   610–618, 655 (`GetElementMainLabel` → `Main_FY` `"<el> damage/chuan/Anti"`),
   1010/1011, 1040/1041, 1260, 1300–1302 + 1350 (`GetElementName` →
   `MainDisplay_Label_Fire/Frost/Lightning/Poison/Physical/Shadow`), and
   1330 (a *different template per element*, `MainStat_BurnLife_*`).
4. When comparing two items, a green/red diff suffix is appended
   (`GetMainArrayDiffText` :1620) — not part of the base line.

Unknown indexes fall through to `default: return string.Empty` — the game
simply shows no line.

### DOT array (`GetDotArrayLine`)

For each non-zero `WPDT_A` in `DOT`:

* `{0}` in every `DotStat_*` template is the **element's DOT skill name**:
  the game looks up the current class's DOT skill for `EL` in the talent data
  (`TryGetDotSkill` :1922) and colors it with the element color
  (`DamageColor.Colors`); if none is found it falls back to the element name
  (`Main_FY` `fire`/`frozen`/… → "Fire"/"Frost"/…). Indexes 2100–2102 instead
  substitute the DOT skill's death-explosion child skill name
  (`GetDotDeathExplosionLine` :1899) and render nothing if the class has no
  such child skill.
* `{1}` (where present) is `FormatWeaponStatValue(Index, number)`.
* Bool indexes **{2001, 2005, 2100, 2102, 2200, 2201, 2301, 2302, 2304, 2400,
  2604}** have no `{1}` in the template — `number` is ignored (matches
  `DotBoolFields`, `SaveDataEquipmentSanitizer.cs:392`).

### SK / CP arrays (`GetSKArrayLine` / `GetCPArrayLine`)

`WPDT_B {SkillName, Index, GlobleID, EL, number, LinkSK}`:

* `{0}` = the skill name (`SkillName` / pool field `SkN`), localized via
  `LOC.MM.GetSkill` and colored by the skill's damage type.
* 3000/4000 (transform) substitute the transform target resolved from
  `GlobleID`; 3200/3203 substitute the linked skill `LinkSK`; 4101/4201 map
  `number` 1–5 → "x2"…"x5"/"becomes 1" (`GetCPCountModeText` :2409).
* 3530/3535 and 4401 substitute an element label from `EL`.
* The affix pool file `1_2_SK.bin` (rows in `affixes.json`, `pool:"sk"`)
  contains **both** 3xxx (weapon-skill, rendered by `GetSKArrayLine`) and 4xxx
  (companion-skill, rendered by `GetCPArrayLine`) indexes.

### Gem sockets (Aocao)

Socketing a gem calls `GetSocketType(gem color, weapon slot)`
(`WeaponBaoshiApplyUtil.cs:73`) and stores the result in `WPAocaoSaveData.Type`.
At stat-apply time `Type` selects a `+Number%` bonus to the `GemFloatFields`
stat (`SaveDataEquipmentSanitizer.cs:468`). Layout (color × slot):

| color | helmet | armor | gloves | shoes | weapon/offhand |
|---|---|---|---|---|---|
| red | 0 HealthMax | 1 Fire Res | 2 Fire Pen | 0 HealthMax | 3 Fire Dmg |
| yellow | 4 Drop rate | 5 Lightning Res | 6 Lightning Pen | 4 Drop rate | 7 Lightning Dmg |
| green | 8 Comp. Max Life | 9 Poison Res | 10 Poison Pen | 11 Comp. Atk Spd | 12 Poison Dmg |
| blue | 13 ManaMax | 14 Frost Res | 15 Frost Pen | 13 ManaMax | 16 Frost Dmg |
| purple | 17 Comp. Dmg | 18 Shadow Res | 19 Shadow Pen | 20 Move Spd | 21 Shadow Dmg |
| white | 22 Atk Spd | 23 Physical Res | 24 Physical Pen | 22 Atk Spd | 25 Physical Dmg |

("weapon/offhand" = sword/bow/staff/bone/shield/arrow/spell/corpse.)

## affix-names.json schema

`web/public/data/affix-names.json`:

```jsonc
{
  "elements": ["Fire","Frost","Lightning","Poison","Physical","Shadow"], // EL 0-5 display names
  "main": {
    "<index>": {
      "label": "Critical hit chance + {n}%",  // English line template
      "percent": true,          // {n} renders with a % sign in-game
      "bool": true,             // present when number is ignored (presence-only line); label has no {n}
      "el": true,               // present when the line substitutes the element ({el})
      "format": "int"|"1dp"|"float", // FormatWeaponStatValue: floor / "0.0" / "0.##"
      "elVariants": ["...", ...],    // index 1330 only: full template per EL 0-5
      "unmapped": true          // data-only index with no code handler (game shows no line)
    }
  },
  "dot": { /* same shape; every entry uses {el} = element DOT skill/element name */ },
  "sk":  { /* same shape; 3xxx + 4xxx. extra placeholders:
              {skill} = SkN skill name, {link} = linked skill (3200/3203),
              {target} = transform result (3000/4000), {mode} = count mode (4101/4201) */ },
  "aocao": { "<type 0-25>": "Poison Damage" }  // gem socket stat label; always +N%
}
```

Placeholders: `{n}` = the affix `number` formatted per `format`; `{el}` = the
element display name (`elements[EL]`). In-game, DOT `{el}` is actually the
current class's DOT *skill* name (e.g. the ignite/bleed skill) colored by
element — using the element name is the class-independent approximation, and is
also exactly what the game falls back to when the skill isn't found.

Fidelity notes:

* Labels are verbatim English strings from the FY files, including oddities
  (`Character_ManaCostReduction` = "Mana-", `Character_DebuffDurationReduction`
  = "Debuff-").
* The game wraps element/skill words in `<color=#...>` rich-text tags
  (`DamageColor.Colors`) — colors are omitted here.
* Indexes **1057, 1058** (main), **2022** (dot), **4011, 4012, 4037** (sk)
  appear in the shipped affix pool data (`affixes.json`) but have **no handler
  anywhere in the current decompiled assembly** (no tooltip case, no
  stat-application field). The game renders no line for them; entries are
  emitted with `"unmapped": true` so consumers can decide how to show them.
* Coverage (verified programmatically against `affixes.json` pools):
  main 185/185 used indexes mapped (361 total entries — full code switch),
  dot 28/28 (35 total), sk 50/50 (79 total), aocao 26/26.

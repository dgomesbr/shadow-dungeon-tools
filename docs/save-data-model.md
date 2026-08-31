# Shadow Dungeon — Save Data Model

Complete C# data model of the OdinSerializer `.sav` payload, traced from the decompiled game code at
`C:\Users\dgome\AppData\LocalLow\OO Cat\ShadowDungeonSaveTool\decompiled\Assembly-CSharp\`
(all `file:line` references below are relative to that folder). Root type: **`Data.SaveData.SaveData`**.

Game version: Unity 2019.4 Mono, Steam appid 4423580. Saves in
`C:\Users\dgome\AppData\LocalLow\OO Cat\Shadow Dungeon\` — no encryption, no content checksums.

## 1. Containment tree

```
SaveData                                    (Data.SaveData/SaveData.cs:9)
├── GameVersion, BackupKind, SaveCreatedUtcTicks, SessionBaselineUtcTicks,
│   SessionId, SaveTransactionId            (session/backup header — NEVER EDIT)
├── EmbeddedGlobalData : GlobalSaveData     (Data.SaveData.GlobalSave/GlobalSaveData.cs:6)
│   ├── LastWriterSlotId, SaveTransactionId, SaveCreatedUtcTicks
│   └── GlobalChestData : GlobalChestSaveData  (GlobalChestSaveData.cs:48)
│       ├── PageCount                       (global chest pages; this user: 10)
│       └── ChestItems : List<ContainerItemSaveData>
├── PlayerDataSavedWithoutEquipment : bool  (must stay true — see invariants)
├── PlayTimeSeconds : long
├── PlayerData : PlayerSaveData             (Data.SaveData/PlayerSaveData.cs:7, ~450 fields)
│   └── Dot_Fire/Ice/TD/Du/Phy/SD : PlayerDotData   (PlayerDotData.cs:5)
├── TalentData : TalentSaveData             (TalentSaveData.cs:7)
│   ├── P_Base, P_Used, P_Used_DF (+4 bool flags)
│   ├── All_Skill_Datas : Dict<string, SkillSaveData>   {Level_Base, Level_WeaponOn, SelectedIndex}
│   └── All_Xi_Datas   : Dict<string, XiSaveData>       {Level_Base}
├── ActbarData : ActbarSaveData             (ActbarSaveData.cs:7)
│   ├── SkillSlots : List<ActbarSkillSlotSaveData>  {Opened, AutoAttack*, IndexName, Xi, SkillType}
│   └── UseSlots   : List<ActbarUseSlotSaveData>    {Opend, IndexName, Type}
├── InventoryData : InventorySaveData       (InventorySaveData.cs:7)
│   ├── Money : long
│   ├── PageCount                           (inventory pages; this user: 15; grid 15x17 per page)
│   ├── Equipments : List<WeaponSaveData>   (10 entries, index = slot, null = empty slot)
│   └── InventoryItems : List<ContainerItemSaveData>
│       └── ContainerItemSaveData           (ContainerItemSaveData.cs:6)
│           ├── Page, GridX, GridY, ItemType (0/1/2)
│           ├── Weapon : WeaponSaveData     (when ItemType == 0)
│           ├── Baoshi : BaoshiSaveData     (when ItemType == 1: gems, essences, stones, runes)
│           └── UseItem : UseItemSaveData   (when ItemType == 2: potions, keys, scrolls)
├── DialogData : DialogSaveData             {TriggeredEvents, CompletedDialogs : List<string>}
├── UnlockedChapterIds : HashSet<int>       (default {1})
├── UnlockedLevelIds : HashSet<string>      (level ids like "01_01")
├── DefeatedBossLevelIds : HashSet<string>
├── LastPlayLevelId : string
├── UnlockedMijing : bool                   (secret-realm/endless dungeon unlocked)
└── mijingFloor_easy/_medium/_hard/_master : int   (highest reached floor per difficulty, min 1)

WeaponSaveData                              (Data.SaveData/WeaponSaveData.cs:8)
├── counters: RebuildTime, EnhanceTime, HHTime, SkillFWTime, JHEL_Count, JH_Count
├── identity: PLtype, WeaponType, CharType, ItemType, GlobalID, ItemName, Quality, Level,
│   Price, Size, SaveSlot, SoundDrop, SoundUse, RotateType
├── set: Set_Index, SetRuntimeData : Set_DT {SetID, SetName, Lit[3] : Set_DT_Lit, Buff*}, BS_Set_Index
├── mijing: DropScene, MJ_Level
├── base stats: Damage, Health, Mana, BaseValueDoubled, BaseValueMultiplier,
│   Fire, Frozen, Thunder, Poison, Physics, Shadow
├── Main : WPDT_A[]   {Index, EL, number}   (main affixes)
├── DOT  : WPDT_A[]   {Index, EL, number}   (damage-over-time affixes)
├── SK   : WPDT_B[]   {SkillName, Index, GlobleID, EL, number, LinkSK}  (skill-modifier affixes)
├── CP   : WPDT_B[]   (companion-modifier affixes)
├── FW_Base : WPFW_Base {FWname, type, number, price}   (attribute rune, 1 per item)
├── WP_SkillCount, WPSK : List<WPSkillSaveData>  {IndexName, Number, Number2, price}  (6 skill sockets)
├── MaxAocaoCount, AocaoCount, Aocao : List<WPAocaoSaveData>
│   └── {HasAocao, HasBaoshi, Name, Type 0-25, UseType, BS_Quality, Number}  (6 gem sockets)
├── SPC : List<WPSPC> {Index, EL, PRC, price}  ([0]=innate orb proc, [1]=socketed SPC rune)
├── SPCindex, SPC_EL, SPC_PRC               (legacy mirror of SPC[0])
├── SPC_DMG_Bei                             (orb-proc damage multiplier, 100 = neutral)
└── Enchanted : bool
```

Non-payload sibling types: `SaveBackupKind` enum (SaveBackupKind.cs:3), `SaveSlotData`
(SaveSlotData.cs:6 — slot-select UI summary built from manifest/save, never re-serialized into `.sav`),
`IntVector2 {x, y}` (IntVector2.cs:5), `DamageType` enum (DamageType.cs:1).

## 2. File layout & save pipeline

- Slot files per slot N: `slot_N.sav` (EntryBaseline), `slot_N_auto.sav` (AutoBackup),
  `slot_N_exit.sav` (ExitBackup). Suffix constants at SaveManager.cs:252-254. Plus
  `slot_N_recovery.meta`(+`.bak`) (SaveManager.cs:256-258), `save_manifest.meta` (SaveManager.cs:260),
  `global.sav` (SaveManager.cs:316), `last_save_id.sav` (SaveManager.cs:310).
- Every slot `.sav` embeds a `GlobalSaveData` clone as `EmbeddedGlobalData` at write time
  (SaveManager.cs:964-977); `global.sav` holds the same object standalone (SaveManager.cs:1135).
  On load the freshest embedded copy can win over `global.sav` (SaveManager.cs:861-881, keyed on
  `LastWriterSlotId` + `SaveTransactionId` + ticks).
- **Continue load order** (`TryResolveSlotForEntry`, SaveManager.cs:1225-1273):
  baseline is read first only to establish recovery metadata, then the game prefers
  **exit backup → auto backup → baseline** (→ orphan backups as last resort).
  Invalid candidates are skipped silently with a log warning.
- Validation per file (`TryLoadValidSlotData`, SaveManager.cs:1323-1416):
  1. Odin-deserializes; runs `SaveData.PostLoadFix()` (SaveData.cs:87).
  2. `BackupKind` must be a defined value **and equal the kind expected for that filename**
     (SaveManager.cs:1370-1381).
  3. Structural check `IsSaveDataStructurallyValid` (SaveManager.cs:1484-1507):
     `SaveCreatedUtcTicks > 0` and PlayerData/TalentData/ActbarData/InventoryData/DialogData/
     UnlockedChapterIds/UnlockedLevelIds/DefeatedBossLevelIds all non-null.
  4. For auto/exit backups only: `SessionBaselineUtcTicks` must equal the recovery-meta baseline,
     `SessionId` must match, and `SaveCreatedUtcTicks > SessionBaselineUtcTicks`
     (SaveManager.cs:1383-1399). Mismatch = file silently rejected, older file loads instead.
- Every save regenerates `SaveTransactionId` and stamps `BackupKind`, `SessionBaselineUtcTicks`,
  `SessionId` from recovery meta (`BuildSaveSnapshot`, SaveManager.cs:927-994).
- The manifest caches UI summary fields (UiPlayerName/UiLevel/UiDFLevel/UiPlayTimeSeconds, updated at
  SaveManager.cs:722-733); it stores GUIDs and paths, **no hashes** — stale UI values self-heal on
  next save and never invalidate a slot.

## 3. Class reference

### 3.1 SaveData (Data.SaveData/SaveData.cs:9)

| Field | Type | Meaning / valid range |
|---|---|---|
| GameVersion | string | `Application.version` at save; informational (SaveData.cs:61,89) |
| BackupKind | SaveBackupKind | 0 EntryBaseline / 1 AutoBackup / 2 ExitBackup; must match filename (SaveManager.cs:1370-1381). **Never edit** |
| SaveCreatedUtcTicks | long | UTC ticks at write; must be > 0 and > SessionBaselineUtcTicks for backups. **Never edit** |
| SessionBaselineUtcTicks | long | Session baseline ticks from recovery meta; 0 on baseline files. **Never edit** |
| SessionId | string | 32-hex GUID of play session; matched against recovery meta. **Never edit** |
| SaveTransactionId | string | GUID per save transaction; pairs slot file with embedded global data. **Never edit** |
| EmbeddedGlobalData | GlobalSaveData | Global chest snapshot embedded at file start; null allowed (SaveManager.cs:861-881) |
| PlayerDataSavedWithoutEquipment | bool | true = PlayerData stats have equipped-weapon effects stripped out (SaveDataEquipmentSanitizer.cs:502-532). Keep true |
| PlayTimeSeconds | long | Total play time; shown in slot UI |
| PlayerData | PlayerSaveData | Character stats (section 3.4) |
| TalentData | TalentSaveData | Talent points + skill levels (section 3.5) |
| ActbarData | ActbarSaveData | Hotbar assignments (section 3.6) |
| InventoryData | InventorySaveData | Money, equipment, inventory (section 3.7) |
| DialogData | DialogSaveData | `TriggeredEvents`, `CompletedDialogs` string lists (DialogSaveData.cs:7) |
| UnlockedChapterIds | HashSet\<int\> | Campaign chapters; default `{1}` (SaveData.cs:75) |
| UnlockedLevelIds | HashSet\<string\> | Level ids `"CC_LL"`; default `{"01_01"}` |
| DefeatedBossLevelIds | HashSet\<string\> | Boss levels beaten; gates Mijing unlock (Level.LevelStates/BossLevelManager.cs:61-116) |
| LastPlayLevelId | string | Last played level id |
| UnlockedMijing | bool | Secret-realm (endless dungeon) unlocked |
| mijingFloor_easy/_medium/_hard/_master | int | Highest floor per difficulty, clamped ≥ 1 (SaveData.cs:114-129; Mijing/MijingManager.cs:122-141) |

### 3.2 GlobalSaveData (Data.SaveData.GlobalSave/GlobalSaveData.cs:6)

| Field | Type | Meaning |
|---|---|---|
| LastWriterSlotId | int | Slot that last wrote the global chest; -1 = none (GlobalSaveData.cs:20,29) |
| SaveTransactionId | string | Mirrors owning slot's transaction id (SaveManager.cs:974). **Never edit** |
| SaveCreatedUtcTicks | long | Write ticks; used to pick freshest copy. **Never edit** |
| GlobalChestData | GlobalChestSaveData | Shared warehouse |

### 3.3 GlobalChestSaveData (Data.SaveData.GlobalSave/GlobalChestSaveData.cs:48)

| Field | Type | Meaning / valid range |
|---|---|---|
| PageCount | int | Warehouse pages; clamped 1..10000 (`MaxSafePageCount`, :50,67-74). This user: 10. Grown by UseItem `keyA` (UseItemClass.cs:246-253) |
| ChestItems | List\<ContainerItemSaveData\> | Same wrapper format as inventory; sanitized on load (:76 → SaveDataEquipmentSanitizer.SanitizeGlobalChestItems, SaveDataEquipmentSanitizer.cs:534-537) |

### 3.4 PlayerSaveData (Data.SaveData/PlayerSaveData.cs:7)

~450 public fields, all serialized. Because `PlayerDataSavedWithoutEquipment == true`, these are the
character's **bare** stats: every equipped weapon effect is subtracted before write
(`StripEquippedWeaponEffects`, SaveDataEquipmentSanitizer.cs:617-648) and re-added on equip.
Naming glossary (verified against tooltips/usage): `_Bei` 倍 = % bonus; `Chuan` 穿 = penetration %;
`Anti` = resistance %; `BJ` 暴击 = crit (BJrate = crit chance, BJDamage = crit damage);
`JYrate` = stun chance (passed as `yun` vs `YunAnti`, Companion.cs:1183-1232); `GeDang` 格挡 = block;
`CP` = companion; `EL0..EL5` = element index (see section 4.1); `XJ` = trap (BaoshiClass.cs "XJ_DMG" →
"Character_TrapDamage"); `XJL` = summoned wisp entity (XJL.cs; attacks + auto-picks-up loot);
`HH` = transmutation; `ZQ` = enhance; `SK` = skill rune; `BS` = 宝石 gem / blade-soul depending on prefix;
`JH` 聚合 = fusion/essence.

Core identity & progression:

| Field | Type | Meaning / range |
|---|---|---|
| PlayerName | string | Character name |
| PlayerType | int | Class id 0-3 (4 selectable classes, StartPanel.cs:470-474); Necromancer = 3 |
| EquippedSetCounts | Dict\<int,int\> | Runtime set-piece counts; reset to empty on save (Sanitizer:626,646) — leave empty |
| Level | int | 1..100; ≥100 XP overflows to DFLevel (PlayerManager.cs:6749-6763) |
| Xp_Total / Xp_CurrentLevel | float | Lifetime XP / XP into current level. Requirement: `floor(300 * 1.1^level)` (PlayerManager.cs:6714-6719). Early-level gain multipliers 6x/5x/4x/3x/1.5x (PlayerManager.cs:6760) |
| DFLevel, DFXp_Total, DFXp_CurrentLevel | int/float | Post-100 prestige levels; req `floor(req(100)/2 * 1.013^(dfLevel-1))` (PlayerManager.cs:6721-6725) |
| Health / Mana | float | Base max HP/MP (defaults 500/150); clamped ≥ default on strip (Sanitizer:1900-1909) |
| Damage_Base | float | Base damage (default 50); clamped ≥ 50 (Sanitizer:1907) |
| CompCount | int | Companion count |
| AutoAttackEnabled, AutoJH, AutoDrinkH, AutoDrinkM | bool | QoL toggles (AutoJH = auto-fuse; auto-drink HP/MP potions) |
| QH_Price, QH_Bei, Reforge_Inc, QH_Inc, HH_Inc, SK_Inc | int | Crafting economy modifiers; HH_Inc = transmutation cap per item, default 10 (WeaponBaoshiApplyUtil.cs:578-585) |
| Pick_PL_Base/_Bei/_Percent | float | Player pickup radius: `Max = Base * Percent/100` (PlayerManager.cs:3049); Base defaults 0.8, Bei is legacy (zeroed in PostLoadFix, PlayerSaveData.cs:1330-1348) |
| Pick_XJL_Base/_Bei/_Percent | float | Wisp pickup radius, same formula (PlayerManager.cs:3052) |
| XJL_DMG, XJL_UseSKTime, XJL_SellPrice | float | Wisp damage per wisp (PlayerManager.cs:2480), wisp skill time, wisp auto-sell price bonus |

Base combat stats (all additive percentages unless noted):

| Field | Meaning |
|---|---|
| Health_Bei, Mana_Bei, Health_Percent, Mana_Percent | Max HP/MP % bonuses |
| Health_R_Base, Mana_R_Base | HP/MP regen base (defaults 1) |
| Attack_R_health_Base/_Percent, Attack_R_mana_Base/_Percent | On-hit HP/MP restore (flat / %) |
| Damage_Bei, ATSpeed_Bei, MVSpeed_Bei | Damage / attack speed / move speed % |
| BJrate, BJDamage, BJD_Anti | Crit chance %, crit damage %, crit-damage-taken resist |
| JYrate | Stun chance % (see glossary) |
| GeDang | Block % (runtime cap 100) |
| CoolDown | Cooldown reduction % |
| ManaXH | Mana cost reduction % |
| Damage_Anti | Damage reduction % (runtime cap 95) |
| DOTcut | DoT damage taken reduction % |
| AntiSlow | Slow resist % |
| ThroughRate | Pierce rate ("Character_PierceRate") |
| JYBoss_DMG, JYBoss_Anti | Damage dealt to / taken from elites & bosses % |
| ItemDrop_Rate | Drop rate % (feeds quality weights, ItemManager.cs:150-166) |
| FlySpeed | Projectile speed % |
| ORB_Damage, WPSPC_DMG, WPSPC_Rate | Orb(SPC)-proc damage %, weapon-SPC damage/rate bonuses |
| AllChuan, AllAnti | All-element penetration / resistance % |
| {Fire,Frozen,Thunder,Poison,Physics,Shadow}DamageXi | Per-element damage coefficient (talents) |
| {El}Damage_Bei / {El}Chuan / {El}Anti | Per-element damage % / penetration % / resistance % (El = 6 elements) |
| C_Health, C_Damage, C_ATSpeed, C_MVSpeed, C_AllAnti | Companion HP/damage/AS/MS/resist % |
| DMG_R_H, DMG_R_M | Damage scaling from health / mana |
| BS_Add, BS_Multi | Gem stat flat add / % multiplier applied to socketed gem values (BaoshiClass.NumberLast, BaoshiClass.cs:40) |
| Temple_HealPrc, Temple_DMG, Temple_ATS, Temple_MVS, Temple_BS, BuffT_Temple, BuffT_Drink | Temple-shrine buff bonuses; potion buff duration % |
| XP curve helpers: EXP_Range, Buff_Range | XP pickup radius %, buff aura radius % |

Talent-derived effect fields — everything else in the class mirrors a weapon/talent effect index.
The authoritative index→field maps live in SaveDataEquipmentSanitizer.cs and define each field's
meaning; the same indexes appear in `WPDT_A.Index` on items (section 3.9):

| Sanitizer map (file:line) | Index range | Player fields covered |
|---|---|---|
| MainFloatFields (Sanitizer:16-183) | 1-1955 | float stats: Health_Bei(1) … XJL_UseSKTime(1955), incl. BE_ZQ_*/BE_SPC_*/BE_HH_*/BE_SK_*/BE_BS_* (400-463: bonuses per enhanced/orb-runed/transmuted/skill-runed/gem-socketed equipment piece), CP1_*/CLass_* (1000-1054: companion-count & class-skill scaling), EMC_*/JYC_* (1200-1206), Orb_* (1504-1508), Z_* resource-scaling (600-653) |
| MainIntFields (Sanitizer:185-247) | 31-1912 | int stats: JYBoss_DMG(31) … DrinkPre_DMG(1912), incl. LowH_/HighH_/LowM_/HighM_* (500-514: conditional damage at HP/MP thresholds), ST_* (550-559: moving/stationary/charge bonuses), ORB_FQ_* (1500-1505: orb split counts), XJ_DMG(1600)/XJ_Time(1601), TuT_*(1602-1604) |
| MainElementFloatFields (Sanitizer:249-279) | 610-1041 | 6-element float arrays: Z_Hmax_EL0-5(610), Z_Mmax_EL0-5(611), Z_CD_EL0-5(612), CP1_DMG0-5(1010), CP1_Chuan0-5(1011), CLass_DMG0-5(1040), CLass_Chuan0-5(1041) |
| MainElementIntFields (Sanitizer:281-327) | 613-1330 | 6-element int arrays: Z_Anti0(613), Z_Chuan0(614), Z_GD(615), Z_BJR(616), Z_DMGCut(617), Z_Thr(618), Z_Chuan{0-5}_BJD(655), PrcCut0-5(1300), PrcCut5P(1301), PrcCut3P(1302), BurnLife0-5(1330) |
| MainBoolFields (Sanitizer:329-363) | 307-1905 | bool toggles: Dot_MSAll(307), LowH_CritAnti10(508), Z_BJR_BJD(654), AB_DMG_*(750-752), NoGD(753), DeadWD/DeadRageWD/DeadStealthWD(862-864), WS_All(1360), Turtle(1807), BloodLost(1809), NoGround(1810-1812), FT(1805), MoneyTO_DMG(1820), DieEXP(1822), Drink_CP(1905) … |

`WS_Anti0..5` (per-element ward bools) and `Diff_EL`, `DiffDotDMG`, `DiffDebuff_DMG` (different-element
synergy) follow the same pattern. Fields set only by talents keep the same names in TalentManager.

Nested per-element DoT blocks — `Dot_Fire, Dot_Ice, Dot_TD, Dot_Du, Dot_Phy, Dot_SD : PlayerDotData`
(element order 0-5, `GetDotByElement`, Sanitizer:1885-1898). PlayerDotData (PlayerDotData.cs:5) fields
map to DOT effect indexes via DotIntFields (Sanitizer:365-390) and DotBoolFields (Sanitizer:392-405):

| PlayerDotData field | DOT index | Type | Meaning (inferred) |
|---|---|---|---|
| Every_Layer | 2000 | int | Bonus per DoT stack |
| Crit_One | 2001 | bool | DoT can crit once |
| FJ / DMG_AddOne / All_LayerR | 2002/2003/2004 | int | Splash on expiry / +dmg per stack / all-stack rate |
| Double_Layer | 2005 | bool | Stacks apply doubled (`Double_LayerLast`, PlayerDotData.cs:75) |
| Dot_Infect / Dot_Infect_Layer / Dot_Infect_All | 2100/2101/2102 | bool/int/bool | DoT spreads to nearby enemies |
| YB / YB_half / YB_Add / YB_MS | 2200/2201/2202/2203 | bool/bool/int/int | Lingering ground effect variants |
| YS | 2300 | int | Vulnerability amp % (uncapped) |
| SL / CM / MH / ZZ | 2301/2302/2303/2305* | bool/bool/int/bool | Status riders (*ZZ=2304, JY=2305) |
| JY / Dead | 2305/2306 | int | Stun rider / execute threshold |
| Dot_Crit / BoomDMGUp / LayerPRC | 2400/2401/2402 | bool/int/int | DoT crits / explosion dmg / stack proc rate |
| BE_CP / BF_DMG / DMG50 | 2450/2500/2501 | int | Companion rider / burst dmg / +50% condition |
| LowH_50, HighH_100, LowM_40 | 2550/2551/2552 | int | Conditional DoT dmg at HP/MP thresholds (PlayerDotData.cs:87-121) |
| FrozenFoever/FrozenCut/Frozen30/FrozenHurtDMG/FrozenForeverDot | 2600-2604 | int×4/bool | Freeze-specific riders |

### 3.5 TalentSaveData (Data.SaveData/TalentSaveData.cs:7)

| Field | Type | Meaning / range |
|---|---|---|
| P_Base | int | Total talent points earned; +1 per level-up (`TalentManager.LevelUP`, TalentManager.cs:1606-1609); default 1 |
| P_Used | int | Points spent in normal talents. Available = `P_Base - P_Used - P_Used_DF` (`P_Have`, TalentManager.cs:163) |
| P_Used_DF | int | Points spent in DF (prestige) talents; recomputed from skill data on load (TalentManager.cs:311) |
| HasAppliedDFLieBonuses | bool | One-shot migration flag; forced true after first apply (TalentManager.cs:186-189,202) |
| HasOpenedTalentPanel / HasAddedAnySkillPoint / HasOpenedActSkillListAfterFirstSkillPoint | bool | Tutorial-hint flags |
| All_Skill_Datas | Dict\<string, SkillSaveData\> | Key = skill `IndexName`; SkillSaveData {Level_Base: points spent, Level_WeaponOn: weapon-granted levels (always zeroed on save, Sanitizer:1857-1870), SelectedIndex: chosen variant} (SkillSaveData.cs:6) |
| All_Xi_Datas | Dict\<string, XiSaveData\> | Key = talent-tree (Xi 系) IndexName; XiSaveData {Level_Base} = points in tree node (XiSaveData.cs:6) |

Skill categories (`SKindex.type`, used by Sanitizer:1684-1756): 0 sample-father, 1 sample-son,
2 comp-father, 3 comp-son, 4 dot-father, 5 dot-son, 6 "Bei" passive-stat skill (the kind weapon
skill-sockets can amplify — only if `Level_Base > 0`, Sanitizer:796-830).

### 3.6 ActbarSaveData (Data.SaveData/ActbarSaveData.cs:7)

`SkillSlots : List<ActbarSkillSlotSaveData>` (ActbarSkillSlotSaveData.cs:6), restored by matching
IndexName against learned skills (ACTbar.cs:241-278):

| Field | Type | Meaning |
|---|---|---|
| Opened | bool | Slot unlocked/populated; false = skipped on restore |
| AutoAttackEnabled / AutoAttackSettingInitialized | bool | Per-slot auto-cast toggle + first-init flag |
| IndexName | string | Skill IndexName (must exist in learned list or slot restores empty) |
| Xi | int | Talent-tree index of the skill |
| SkillType | int | Skill category (see 3.5; 2 = companion-type, checked at ACTbar.cs:498) |

`UseSlots : List<ActbarUseSlotSaveData>` (ActbarUseSlotSaveData.cs:6), restored by finding a matching
item in inventory (ACTbar.cs:291-320): `Opend` (sic), `IndexName` (item name), `Type` (use-item type string).

### 3.7 InventorySaveData (Data.SaveData/InventorySaveData.cs:7)

| Field | Type | Meaning / range |
|---|---|---|
| Money | long | Gold; clamped ≥ 0 on load (:32-35) |
| PageCount | int | Inventory pages, clamped 1..1000 (`MaxSafePageCount`, :9,36-43). This user: 15. Grown by UseItem `bag` (UseItemClass.cs:240-263) |
| Equipments | List\<WeaponSaveData\> | Exactly one entry per equipment button, written in button order with `null` for empty slots (`SaveEquipments`, InventoryManager.cs:86-105). Restored by `CharType` first, list index as fallback (`GetEquipmentButtonForSave`, InventoryManager.cs:186-203). 10 slots — see CharType enum 4.3. Bare WeaponSaveData, **not** wrapped in ContainerItemSaveData |
| InventoryItems | List\<ContainerItemSaveData\> | Grid items; written via ContainerSaveUtil.SaveContainerItems (Container.Util/ContainerSaveUtil.cs:8-55) |

### 3.8 ContainerItemSaveData (Data.SaveData/ContainerItemSaveData.cs:6)

| Field | Type | Meaning / range |
|---|---|---|
| Page | int | 0-based page; must be `< pages.Count` or item silently dropped (ContainerRestoreUtil.cs:94-97) |
| GridX, GridY | int | Anchor cell (top-left); must be `0 ≤ x < inventorySize.x`, `0 ≤ y < inventorySize.y` (ContainerRestoreUtil.cs:98-101). Grid is a scene-configured `ContainerManager.inventorySize` (Container.Managers/ContainerManager.cs:27); observed 15x17 for inventory & warehouse |
| ItemType | int | 0 weapon/equipment, 1 Baoshi (gem/rune/stone/essence), 2 UseItem. Any other value dropped (ContainerRestoreUtil.cs:113-162) |
| Weapon | WeaponSaveData | Payload iff ItemType 0, else null |
| Baoshi | BaoshiSaveData | Payload iff ItemType 1, else null |
| UseItem | UseItemSaveData | Payload iff ItemType 2, else null |

Restore behaviour (`RestoreOneItemToPage`, ContainerRestoreUtil.cs:81-169): checks **only the anchor
cell** for occupancy (`slotData.isOC`, :102-105) — it does not footprint-check the item rectangle;
region occupation happens afterwards (InventoryManager.cs:218). Occupied anchor, out-of-range
coordinates, null payload, or a removed-rune template miss (:87-90) ⇒ **item silently deleted**.

### 3.9 WeaponSaveData (Data.SaveData/WeaponSaveData.cs:8)

Mirror of runtime `WeaponClass` (WeaponClass.cs:13); round-trips via FromRuntime/ApplyToRuntime
(WeaponSaveData.cs:116-267). Represents ALL equipment, not just weapons.

| Field | Type | Meaning / valid range |
|---|---|---|
| RebuildTime | int | Reforge count (`Reb_CountMax`; incremented UI.Panels/WeaponManager.cs:453,957); ≥ 0 |
| EnhanceTime | int | Enhance count (`ZQ_CountMax`; WeaponManager.cs:1140; shown as gold "+N" in title, WeaponClass.cs:509-511); ≥ 0 |
| HHTime | int | Transmutation-stone uses (`HHCount`; each adds stone Number to SPC_DMG_Bei, cap = PlayerData.HH_Inc default 10; WeaponBaoshiApplyUtil.cs:578-592) |
| SkillFWTime | int | Skill runes socketed so far (`SKCount`; WeaponBaoshiApplyUtil.cs:447-489); compared against SkillFW_CountMax |
| JHEL_Count | int | Elemental essences fused; hard cap 12 (WeaponBaoshiApplyUtil.cs:216-233); each adds +4/+3/+1 %el to Fire..Shadow by slot |
| JH_Count | int | Normal essences fused; hard cap 8 (WeaponBaoshiApplyUtil.cs:235-268); each appends a Main stat (10/1/2/11/101/100) |
| PLtype | int | Owning class 0-3, or 1000 = generic/all-class (WeaponPlayerType.cs:51-58; ItemManager.cs:4808+) |
| WeaponType | string | Model/slot family string — see 4.4 |
| CharType | int | Equipment SLOT 0-9 — see 4.3. **Not** the character class |
| Set_Index | int | Set id; 0 = no set. Alone activates set membership; validated against ItemManager.SET (Sanitizer:1130-1144) |
| SetRuntimeData | Set_DT | Cached copy of the set row (Set_DT.cs:4); null OK — falls back to ItemManager.SET[Set_Index] (WeaponClass.TryGetSetData, WeaponClass.cs:2422-2443). If SetID ≠ Set_Index it is nulled (Sanitizer:1134-1137) |
| BS_Set_Index | int | Gem-set id (matched-gem bonus family) |
| DropScene | int | 0 campaign, 1-4 Mijing difficulty it dropped in; clamped 0..4 (Sanitizer:1008) |
| MJ_Level | int | Mijing floor of drop; forced ≥1 iff DropScene>0 else 0 (Sanitizer:1009) |
| SkillFW_CountMax | int | Skill-rune capacity. Defaults: weapon 8 / armor 4 / accessory 2; hard caps 12/8/4 (WeaponClass.cs:248-271); +1 per Stone_AM (WeaponBaoshiApplyUtil.cs:308) |
| SPC_DMG_Bei | float | Orb-proc damage multiplier in percent; **100 = neutral**; ≤0 normalized to 100 (Sanitizer:1014-1017). Final proc = `SPC[i].PRC * SPC_DMG_Bei / 100` (WeaponClass.GetSPCPRC, WeaponClass.cs:423-430) |
| BaseValueDoubled | bool | Legacy flag: Damage/Health/Mana doubled via Stone_HM/CG/LC |
| BaseValueMultiplier | float | Multiplier on Damage/Health/Mana (`DamageFinal = Damage * mult`, WeaponClass.cs:105-109); normalized ≥1, =2 when Doubled (Sanitizer:1018-1029) |
| Enchanted | bool | Stone_FM enchant applied (WeaponBaoshiApplyUtil.cs:311) |
| Main | WPDT_A[] | Main affixes {Index, EL, number}. Index keys the sanitizer/effect tables (section 3.4); applied verbatim, **no clamps**. number floats; ints floored (Sanitizer:832-860) |
| DOT | WPDT_A[] | DoT affixes; EL **must** be 0-5 or entry removed on save (Sanitizer:968-969,1079-1094); number floored to int; bool indexes {2001,2005,2100,2102,2200,2201,2301,2302,2304,2400,2604} ignore number (DotBoolFields, Sanitizer:392-405) |
| SK | WPDT_B[] | Skill-modifier affixes {SkillName, Index, GlobleID, EL, number, LinkSK} (WPDT_B.cs:4); SkillName must be a real talent skill; Index 3000 requires SkillChange GlobleID (Sanitizer:1096-1111) |
| CP | WPDT_B[] | Companion-modifier affixes; Index 4000 requires CompSkillChange GlobleID |
| FW_Base | WPFW_Base | Attribute rune {FWname, type, number, price} (WPFW_Base.cs:4). One per item (WeaponBaoshiApplyUtil.cs:523-541). `type` ∈ FwBase maps — see 4.7. Unknown type ⇒ blanked (Sanitizer:1146-1155) |
| Damage, Health, Mana | float | Base stat contribution (× BaseValueMultiplier) added to player on equip (Sanitizer:650-656) |
| Fire..Shadow | float | Per-element % whose MEANING depends on slot: damage% on mainhand+orb+jewelry, penetration% on offhand+ring, resist% on armor+amulet (WeaponClass.cs:544-700; Sanitizer:658-695) |
| WP_SkillCount | int | Populated WPSK count; clamped 0..WPSK.Count (Sanitizer:1035-1042); sockets at i ≥ WP_SkillCount are wiped (Sanitizer:1197-1219) |
| WPSK | List\<WPSkillSaveData\> | 6 skill sockets {IndexName, Number, Number2, price} (WPSkillSaveData.cs:6). Number = levels rolled at drop, Number2 = levels added by socketing runes (WeaponBaoshiApplyUtil.cs:466-486). Empty = IndexName "0"/blank or Number+Number2 ≤ 0. Only amplifies skills the player has at Level_Base ≥ 1 (Sanitizer:796-830); uncapped |
| MaxAocaoCount | int | Max gem sockets (= SizeX*SizeY from table, ItemManager.cs:4755) |
| AocaoCount | int | Opened sockets; clamped 0..min(Aocao.Count, MaxAocaoCount) (Sanitizer:1043-1058); +1 per Stone_KZ (WeaponBaoshiApplyUtil.cs:543-560) |
| Aocao | List\<WPAocaoSaveData\> | 6 gem sockets — see 3.10 |
| SPC | List\<WPSPC\> | Orb-proc effects {Index, EL, PRC, price} (WPSPC.cs:4). SPC[0] = innate proc, SPC[1] = socketed SPC rune (WeaponBaoshiApplyUtil.cs:491-521). Index 0 = empty. PRC = proc chance/power |
| SPCindex, SPC_EL, SPC_PRC | int,int,float | Legacy mirror of SPC[0]; kept in sync on save (Sanitizer:1182-1194); used only if SPC list empty (WeaponSaveData.cs:263-266) |
| ItemType | int | Always 0 for equipment |
| GlobalID | int | Row id in weapon table; icon lookup requires (PLtype, CharType, Quality, GlobalID) to match a table row (ItemIconUtil.cs:9-88) else icon = null + error log |
| ItemName | string | Localization key for name (ItemClass.cs:31-35) |
| Price | int | Sell value; grows with socketed rune prices |
| Quality | int | 0-6 (see 4.2). Tooltip color dict has keys 0-8 only (QualityColor.cs:10-21) ⇒ **Quality > 8 throws KeyNotFound in tooltips; keep ≤ 6** |
| Size | IntVector2 | Grid footprint {x,y}. From weapon table (SizeX/SizeY). Known: head 2x2, body 2x3, hand 2x2, leg 2x2 |
| SaveSlot | IntVector2 | Legacy stored grid position (wrapper GridX/Y is authoritative) |
| Level | int | Item level (drop scaling) |
| SoundDrop, SoundUse, RotateType | int | Audio ids / rotation allowance from table |

### 3.10 WPAocaoSaveData — gem sockets (Data.SaveData/WPAocaoSaveData.cs:6)

| Field | Type | Meaning / range |
|---|---|---|
| HasAocao | bool | Socket exists/opened. false ⇒ socket blanked on save (Sanitizer:1234-1236) |
| HasBaoshi | bool | A gem is inserted. false ⇒ gem fields cleared (Sanitizer:1238-1241) |
| Name | string | Gem ItemName; must resolve via ItemManager.TryGetBaoshiByItemName or gem cleared (Sanitizer:1242-1246); also drives socket icon (ContainerRestoreUtil.cs:34) |
| Type | int | **Stat type 0-25** = gem color × host slot (see 4.6); out of 0..25 ⇒ cleared (Sanitizer:1242) |
| UseType | int | Source gem's UseType (0 for stat gems); backfilled from template if 0 (ContainerRestoreUtil.cs:41-59) |
| BS_Quality | int | Gem tier; backfilled from template if 0 |
| Number | float | Stat magnitude added to the GemFloatFields player field (Sanitizer:719-734). Applied verbatim, no clamp |

### 3.11 BaoshiSaveData — gems/essences/stones/runes (Data.SaveData/BaoshiSaveData.cs:6)

Wraps runtime `BaoshiClass` (BaoshiClass.cs:9). Used for ItemType 1 inventory items.

| Field | Type | Meaning / range |
|---|---|---|
| BStype | string | Subtype key — see 4.5 (colors, `JH_*`, `JHEL0-5`, `Stone_*`, attribute-rune stat keys) |
| UseType | int | 0 stat gem, 1 fusion essence, 2 crafting stone, 3 skill rune, 4 SPC(orb) rune, 5 attribute rune (BaoshiClass.GetMain switch, BaoshiClass.cs:66-283) |
| BS_Quality | int | Gem/rune tier |
| Number | int | Stat magnitude; display adds player BS_Add/BS_Multi (`NumberLast`, BaoshiClass.cs:40) |
| MstackSize | int | Max stack. UseType 3 runes saved with MstackSize 1 are renormalized up from the SkillFW template (Sanitizer:941-951) |
| CstackSize | int | Current stack count (1..MstackSize) |
| DropSpriteSize | int | World-drop sprite size |
| SKname | string | Skill rune: granted skill's IndexName (UseType 3); template lookup key (Sanitizer:1383-1417) |
| FWtype | int | Rune socket-target class: UseType 4 → 0 weapon / 1 helmet+armor / 2 gloves+boots / 3 amulet+ring / 4 orb+jewelry; UseType 5 → 0 weapon / 1 armor / 2 accessory (BaoshiClass.cs:203-233; WeaponBaoshiApplyUtil.cs:828-871) |
| Index | int | Template id: UseType 3 = SKFW rune index; UseType 4 = SPC_MB index (Sanitizer:1404-1437) |
| EL | int | Element 0-5 (skill rune color, SPC rune element) |
| PRC | float | SPC rune proc value (defaults to 1 if ≤0 for tooltip, BaoshiClass.cs:322) |
| priceQulity | int | Price-tier index |
| Xi | int | Talent-tree index of SKname's skill (set from rune template, ItemManager.cs:6940,7078) |
| ItemType..RotateType | (base) | Same 11 ItemClass base fields as WeaponSaveData (ItemName is the template key; Size usually 1x1) |

### 3.12 UseItemSaveData — potions/consumables (Data.SaveData/UseItemSaveData.cs:6)

Wraps `UseItemClass` (UseItemClass.cs:12). Used for ItemType 2 inventory items.

| Field | Type | Meaning / range |
|---|---|---|
| InfoType | int | Consumable category, dispatched in `Use()` (UseItemClass.cs:56-263): 0 instant HP/MP restore; 1 timed buff potion; 2 challenge-portal key; 3 permanent stat elixir (adds Number to Health/Mana/Damage_Base/regen/element dmg%); 4 permanent element-resist elixir; 5 talent/level items (respec `yiwang`, DF respec `lunhui`, level-up `shenyou`, XP `juexing`); 6 capacity keys (`bag` +inventory page, `keyA` +warehouse page) |
| UseType | string | Subkey within InfoType (e.g. "health","mana","huoli"; "EL_Damage","EL_Anti","xueshi","xingyun","zhaohuan"; portal colors; elixir families "taitan/zhihui/zhandou/fusu/shanguang(+1-3)","fire".."shadow"; "ST_Fire".."ST_SD"; "yiwang/lunhui/shenyou/juexing"; "bag/keyA") |
| damageType | DamageType | Element for EL_Damage/EL_Anti buff potions (enum fire..shadow) |
| Number | int | Magnitude (heal amount base, buff %, elixir stat points, XP amount) |
| CDTime | float | Use cooldown seconds |
| Duration | int | Buff duration seconds; scaled by BuffT_Drink (`DurationLast`, UseItemClass.cs:33) |
| MstackSize / CstackSize | int | Max / current stack |
| DropSpriteSize | int | World-drop sprite size |
| ItemType..RotateType | (base) | Same 11 ItemClass base fields |

### 3.13 Set_DT / Set_DT_Lit (Set_DT.cs:4, Set_DT_Lit.cs:4)

Loaded from the set table into `ItemManager.SET` keyed by SetID (`LoadData_SET`, ItemManager.cs:5444-5506).

| Set_DT field | Meaning |
|---|---|
| SetID | Set id = WeaponSaveData.Set_Index |
| SetName | Localization key |
| Lit[3] | Piece bonuses: Lit[0] at 2 pieces, Lit[1] at 3, Lit[2] at 4 (count-2 indexing, Sanitizer:751-769; WeaponClass.GetSet, WeaponClass.cs:2455-2470) |
| BuffName, BuffType, BuffTime, LayerMax, TP_Layer, NumberL, TP_Max, NumberM | Set proc-buff definition (stacking buff granted by full set) |

Set_DT_Lit: {MainTP, SkillName, Index, GlobleID, EL, Number, LinkSK}. `MainTP` selects the effect
channel: 0 = Main effect (WPDT_A semantics), 1 = DOT effect, 2 = SK, 3 = CP (Sanitizer:771-794;
ItemManager.cs:2755-2790). Known set data: Necromancer sets = SetID 46-60, 4 armor pieces each
(GlobalID 20000/30000/40000/50000 + SetID+289, PLtype 3).

## 4. Enums & coded values

### 4.1 Element (EL / DamageType) — 0..5

`DamageType` enum (DamageType.cs:1) and every int `EL` field share one order; `IsElement` accepts
0-5 only (Sanitizer:1831-1838); dot blocks map 0→Dot_Fire … 5→Dot_SD (Sanitizer:1885-1898):

| EL | Element | Player field stems | Dot block |
|---|---|---|---|
| 0 | Fire | Fire* | Dot_Fire |
| 1 | Frozen (ice) | Frozen* | Dot_Ice |
| 2 | Thunder | Thunder* | Dot_TD |
| 3 | Poison | Poison* | Dot_Du |
| 4 | Physics | Physics* | Dot_Phy |
| 5 | Shadow | Shadow* | Dot_SD |

### 4.2 Quality — 0..6 (safe), colors defined to 8

Bucket names from drop weights (ItemManager.cs:150-166) and weapon-table buckets (ItemManager.cs:4806+):

| Quality | Name | Tooltip color (QualityColor.cs:10-21) |
|---|---|---|
| 0 | Normal | #ffffffff (white) |
| 1 | Magic | #53FF6B (green) |
| 2 | Rare | #37C5FF (blue) |
| 3 | Exquisite | #B63EFF (purple) |
| 4 | Epic | #FF50B5 (pink) |
| 5 | Legendary | #FF7200 (orange) |
| 6 | Mythical | #FFCA00 (gold) — max obtainable/safe for items |
| 7, 8 | (color-only entries #FFCEE4, #E5CCAB — no drop bucket) |
| >8 | **crashes tooltip** — `QualityColor.Colors[Quality]` KeyNotFound (ItemClass.cs:34) |

`SlotColors` dictionary (QualityColor.cs:23-53) likewise only defines 0-6.

### 4.3 WeaponSaveData.CharType — equipment SLOT 0..9

Verified via IsMainhandWeapon/IsOffhandWeapon/IsArmorEquipment (WeaponBaoshiApplyUtil.cs:884-916),
SPC-rune FWtype gating (WeaponBaoshiApplyUtil.cs:828-860), element semantics (Sanitizer:658-695;
WeaponClass.cs:544-700) and JHEL essence tooltip (BaoshiClass.cs:GetJHELEssenceStats):

| CharType | Slot | WeaponType strings | Fire..Shadow means |
|---|---|---|---|
| 0 | Main hand | staff / sword / bow / **bone** (Necro) | element damage % |
| 1 | Off hand | spell / shield / arrow / **corpse** (Necro) | element penetration % |
| 2 | Head (helmet) | head | element resist % |
| 3 | Body (armor) | body | element resist % |
| 4 | Hand (gloves) | hand | element resist % |
| 5 | Leg (boots) | leg | element resist % |
| 6 | Amulet | little | element resist % |
| 7 | Orb | little | element damage % |
| 8 | Ring | little | element penetration % |
| 9 | Jewelry | little | element damage % |

### 4.4 PLtype — item's class restriction

Weapon.GP is indexed 0-3 (ItemManager.cs:4808-4900); character-select offers 4 classes
(StartPanel.cs:470-474); `PLtype == 1000` = generic, added to all four GP buckets
(WeaponPlayerType.cs:51-58). Necromancer = 3 (bone/corpse weapons, poison sets, Xi 11 skills).
PlayerSaveData.PlayerType uses the same 0-3 ids.

### 4.5 Baoshi UseType / BStype

UseType from `BaoshiClass.GetMain` (BaoshiClass.cs:66-283) + apply logic (WeaponBaoshiApplyUtil.cs):

| UseType | Kind | BStype values | Apply site |
|---|---|---|---|
| 0 | Socketable stat gem | red / yellow / green / blue / purple / white (fire/thunder/poison/frozen/shadow/physics families) | gem socket → Aocao (WeaponBaoshiApplyUtil.cs:37-71) |
| 1 | Fusion essence | JH_damage, JH_ats, JH_CPdamage (weapon); JH_heal, JH_mana, JH_CPheal (armor); JHEL0..JHEL5 (element essence, any equipment) | TryApplyEssence (:209-269) |
| 2 | Crafting stone | Stone_KZ +socket; Stone_FS reroll item; Stone_HH transmute (+SPC_DMG_Bei); Stone_AM +skill-rune cap; Stone_HM/CG/LC base-value double (weapon/armor/accessory); Stone_FM enchant; Stone_HD skill reroll; Stone_XL main reroll; Stone_CL DOT reroll | TryApplyStone (:271-318) |
| 3 | Skill rune (+1 skill level) | — (SKname/Index identify skill) | TryApplySkillRune (:447-489) |
| 4 | SPC (orb-effect) rune | — (Index → SPC_MB template) | TryApplySPCRune (:491-521) |
| 5 | Attribute rune | DMG, ATS, BJD, ALLC, DOT, C_DMG, C_ATS, Heal, Mana, Anti, MVS, C_Heal, C_Anti, ORB_DMG, XJ_DMG, Drop | TryApplyAttributeRune → FW_Base (:523-541) |

### 4.6 Aocao.Type — 0..25 → GemFloatFields

Written at socketing time from gem color × host WeaponType (`GetSocketType`,
WeaponBaoshiApplyUtil.cs:73-207); consumed via GemFloatFields (Sanitizer:468-496):

| Type | Player field | Source (color @ host) | Type | Player field | Source |
|---|---|---|---|---|---|
| 0 | Health_Bei | red @ head/leg | 13 | Mana_Bei | blue @ head/leg |
| 1 | FireAnti | red @ body | 14 | FrozenAnti | blue @ body |
| 2 | FireChuan | red @ hand | 15 | FrozenChuan | blue @ hand |
| 3 | FireDamage_Bei | red @ weapon | 16 | FrozenDamage_Bei | blue @ weapon |
| 4 | ItemDrop_Rate | yellow @ head/leg | 17 | C_Damage | purple @ head |
| 5 | ThunderAnti | yellow @ body | 18 | ShadowAnti | purple @ body |
| 6 | ThunderChuan | yellow @ hand | 19 | ShadowChuan | purple @ hand |
| 7 | ThunderDamage_Bei | yellow @ weapon | 20 | MVSpeed_Bei | purple @ leg |
| 8 | C_Health | green @ head | 21 | ShadowDamage_Bei | purple @ weapon |
| 9 | PoisonAnti | green @ body | 22 | ATSpeed_Bei | white @ head/leg |
| 10 | PoisonChuan | green @ hand | 23 | PhysicsAnti | white @ body |
| 11 | C_ATSpeed | green @ leg | 24 | PhysicsChuan | white @ hand |
| 12 | PoisonDamage_Bei | green @ weapon | 25 | PhysicsDamage_Bei | white @ weapon |

(Full color/host matrix at WeaponBaoshiApplyUtil.cs:73-207; "weapon" here = any of
sword/bow/staff/bone/shield/arrow/spell/corpse, i.e. both hands. purple @ hand = 19.)

## 5. Editing invariants

Rules a save editor must enforce (each verified in code at the cited line):

1. **Never touch the session header**: `SessionId`, `SessionBaselineUtcTicks`, `SaveCreatedUtcTicks`,
   `SaveTransactionId`, `BackupKind` (in SaveData AND EmbeddedGlobalData). Auto/exit backups are
   cross-checked against `slot_N_recovery.meta` (ticks + session id) and against
   `SaveCreatedUtcTicks > SessionBaselineUtcTicks` (SaveManager.cs:1383-1399); `BackupKind` must match
   the filename's kind (SaveManager.cs:1370-1381). Violations are **silently discarded** and an older
   file loads instead. Also never touch Odin `$id/$type/$rlength/$rcontent/$iref` tokens except
   bumping `$rlength` when resizing an array.
2. **Edit all three slot files** (`slot_1.sav`, `slot_1_auto.sav`, `slot_1_exit.sav`) consistently —
   Continue prefers exit → auto → baseline (SaveManager.cs:1225-1273). If the player has played
   between edits the three files snapshot different moments: apply additive deltas per file, not one
   absolute value.
3. **Inventory placement** (ContainerRestoreUtil.cs:81-169): `Page < PageCount`
   (InventoryData.PageCount = 15 here; GlobalChest PageCount = 10), `0 ≤ GridX < 15`,
   `0 ≤ GridY < 17` (grid 15x17; existing items observed up to (14,16)). The loader checks **only the
   anchor cell** — it never footprint-validates the item rectangle, and out-of-range/occupied/broken
   items are **silently deleted**. The editor itself must guarantee non-overlapping item rectangles
   using each item's `Size` (footprints from the weapon table: head 2x2, body 2x3, hand 2x2, leg 2x2;
   gems/potions typically 1x1).
4. **Rune template check**: `IsRemovedRuneContainerItem` (Sanitizer:1369-1381) deletes ItemType 1
   items with `Baoshi.UseType == 3` whose SKname/Index doesn't resolve to a skill-rune template
   (Sanitizer:1383-1417), and UseType 4 whose Index isn't a known SPC_MB (Sanitizer:1419-1438).
   Cloning existing valid runes is safe — Index/SKname are template keys, fine to duplicate.
5. **Stacks**: keep `CstackSize ≤ MstackSize`; the game renormalizes UseType 3 runes' MstackSize from
   the template (Sanitizer:941-951) and MstackSize=1 items (Equipment Skill Rune, Rune of Lightness)
   cannot be overstacked — grant quantity as multiple 1-stack clones on empty cells instead.
6. **Talents**: `P_Used + P_Used_DF ≤ P_Base`; P_Base = 1 + levels gained (TalentManager.cs:1606).
   `Level_WeaponOn` is always zeroed on save (Sanitizer:1857-1870) — leave it 0.
   `EquippedSetCounts` is cleared on save (Sanitizer:646) — leave empty.
7. **XP**: level 1-100, requirement `floor(300 * 1.1^level)` (PlayerManager.cs:6714-6719); overflow
   goes to DFLevel (`floor(req(100)/2 * 1.013^(dfLevel-1))`, PlayerManager.cs:6721-6725). Keep
   `Xp_CurrentLevel` below the current level's requirement.
8. **Quality ≤ 6** (Mythical). 7-8 render but have no drop bucket → icon lookup fails; >8 crashes
   the tooltip (ItemClass.cs:34 + QualityColor.cs:10-21).
9. **Orb procs**: effective proc = `SPC[i].PRC × SPC_DMG_Bei / 100` (WeaponClass.cs:423-430), and
   `SPC_DMG_Bei` 100 = neutral (≤0 reset to 100, Sanitizer:1014-1017). To boost procs multiply
   **only PRC** — scaling both compounds.
10. **Weapon shape counters** (NormalizeWeaponShape, Sanitizer:980-1060): keep
    `WP_SkillCount = number of populated WPSK entries` (excess sockets are wiped, deficit clamps the
    count), `AocaoCount ≤ min(Aocao.Count, MaxAocaoCount)`, all time/count fields ≥ 0,
    `BaseValueMultiplier ≥ 1` (2 when BaseValueDoubled), DropScene 0-4 with MJ_Level ≥ 1 iff
    DropScene > 0.
11. **Icon validity**: (PLtype, CharType, Quality, GlobalID) must match a weapon-table row
    (ItemIconUtil.cs:9-88) or the item renders without icon and logs an error. All data tables are
    plain CSV despite the `.bin` extension (`0_0_Weapon.bin`, `0_5_Set.bin`, …).
12. **Equipments list**: exactly 10 entries in slot order (nulls for empty), restored by CharType
    with index fallback (InventoryManager.cs:86-105,186-203). Sets: `Set_Index` alone activates
    membership; `SetRuntimeData` may be null (falls back to `ItemManager.SET`) but if present its
    SetID must equal Set_Index (Sanitizer:1130-1144).
13. **PlayerDataSavedWithoutEquipment must remain true** and PlayerData must NOT be hand-adjusted to
    compensate for equipment edits — the sanitizer strips/re-applies equipment effects symmetrically
    (Sanitizer:502-532). The save-time sanitizer never prunes weapon effects (`pruneEffects: false`
    everywhere, Sanitizer:512,530,536), so exotic Main/DOT/WPSK values round-trip untouched — there
    are **no clamps** on damage%, crit, YS, skill-socket levels (runtime caps: DR ≤ 95, block ≤ 100).
14. **Effect applicability**: WPSK sockets only amplify skills the player owns at `Level_Base ≥ 1`
    of SKindex type 6 (Sanitizer:796-830); bool-index DOT/Main effects ignore `number`; DOT entries
    need EL 0-5 or they're removed (Sanitizer:968-969).
15. Workflow facts: SaveTool validate infers required BackupKind from the FILENAME — encode/validate
    with real slot filenames. Steam Cloud conflict after external edits → choose "Upload to Steam
    Cloud". `save_manifest.meta` holds GUIDs/paths + UI cache only — never hashes; no re-signing needed.

## 6. ItemManager lookups — save fields → data tables

`ItemManager` (ItemManager.cs:17) loads all CSV tables at Awake (ItemManager.cs:185-193) and is the
authority the sanitizer/restore code checks save fields against:

| Save field | Lookup | Declaration / loader / consumer (file:line) |
|---|---|---|
| Weapon (PLtype, CharType, Quality, GlobalID) | `Weapon : PLtype_Group` — `GP[PLtype].QL[CharType].{Normal..Mythical}` lists of `Item_MB` | decl ItemManager.cs:78; loader `LoadData_WP` ItemManager.cs:4726-4900 (parses `0_0_Weapon.bin` CSV: ItemName, GlobalID, DropLevelStart, Quality, SizeX/Y, sockets, PLtype, WeaponType, CharType, base stats, affix rates, skills, SPC, Set_Index); icon consumer ItemIconUtil.cs:9-88 |
| WeaponSaveData.Set_Index / SetRuntimeData | `SET : Dictionary<int, Set_DT>` keyed by SetID | decl ItemManager.cs:110; loader `LoadData_SET` ItemManager.cs:5444-5506 (`0_5_Set.bin`); fallback resolution WeaponClass.cs:2422-2443; clone-on-drop ItemManager.cs:2747-2751; strip/validate Sanitizer:751-769,1130-1144 |
| WPDT_A.Index (Main) | `WP_Main : Dictionary<int, WPDT_RandomA>` | decl ItemManager.cs:112; loader `LoadData_RandomA` ItemManager.cs:185,5508+; validity check Sanitizer:1249-1260 |
| WPDT_A.Index (DOT) | `WP_DOT : Dictionary<int, WPDT_RandomA>` | decl ItemManager.cs:114; same loader (Dottext); validity Sanitizer:1262-1273 |
| WPDT_B.Index (SK / CP) | `WP_SK` / `WP_CP : Dictionary<int, WPDT_RandomB>` | decl ItemManager.cs:116-118; loader `LoadData_RandomMergedSkillB` ItemManager.cs:187; validity Sanitizer:1275-1327 |
| Baoshi.ItemName (gems, Aocao.Name) | `TryGetBaoshiByItemName` → `baoshiByItemName` dict over `Baoshi`/`BaoshiJH`/`BaoshiSPC` lists | lookup ItemManager.cs:6263-6273; lists decl ItemManager.cs:84-88; loader `LoadData_BS` ItemManager.cs:5298; consumers ContainerRestoreUtil.cs:41-79, Sanitizer:1351-1367 |
| Baoshi UseType 3 (skill rune) SKname/Index | `TalentManager.FW.Char[].Xi[].FW[] : SKFW` templates (SkillName + index + Xi) | ensure TalentManager `EnsureSkillFWLibrary` (Sanitizer:1398); name lookup Sanitizer:1472-1505; index lookup Sanitizer:1507-1540; stack template `ItemManager.SkillFW` decl ItemManager.cs:90, used Sanitizer:941-951 |
| Baoshi UseType 4 (SPC rune) Index / WPSPC.Index | inventory-rune slot: `TryGetSPCMBByIndex` (SPC_Rune + SPCMB group); weapon innate slot: `TryGetWeaponSPCMBByIndex` (SPC dict) | ItemManager.cs:5858 / ItemManager.cs:5799; dict decls ItemManager.cs:80-82; loader `LoadData_SPC` ItemManager.cs:189; consumers Sanitizer:1329-1349,1419-1438, WeaponBaoshiApplyUtil.cs:491-521, tooltip BaoshiClass.cs:316-325 |
| WPSK.IndexName (skill sockets) | `TalentManager.SKI : Dictionary<string, SKindex>` + `XiData[SKindex.Xi]` (Bei dict for type-6 passives) | consumers Sanitizer:796-830,1633-1671; socket apply WeaponBaoshiApplyUtil.cs:447-489 |
| WPDT_B.GlobleID (Index 3000/4000) | `TalentManager.SKC_Data` / `CPC_Data` skill-change tables | Sanitizer:1759-1793 |
| UseItem templates | `Potion`, `BuffPotion`, `PremPotion`, `Scroll`, `SpcPotion`, `SpcItem` | decls ItemManager.cs:96-106; loader `LoadData_USE` ItemManager.cs:191 |
| Quality drop weights | `DR_Normal..DR_Mythical` scale with ItemDrop_Rate | ItemManager.cs:150-166 |

Notes: every `TextAsset` table (`WPtext`, `SPCtext`, `BStext`, `USEtext`, `Skilltext`, `Settext`,
`Maintext`, `Dottext`, `SKtext`; ItemManager.cs:58-76) is plain CSV. When a manager singleton or its
table is absent/empty, all sanitizer template checks **pass permissively** (e.g. Sanitizer:1355-1363,
1389-1401) — items are only dropped when the tables are loaded and the lookup definitively fails.

---
*Generated 2026-08-31 from decompiled Assembly-CSharp (895 files) via ShadowDungeonSaveTool.*

### 4.7 FW_Base.type — attribute-rune stat keys

FwBaseFloatFields (Sanitizer:444-460): DMG→Damage_Bei, ATS→ATSpeed_Bei, BJD→BJDamage, ALLC→AllChuan,
DOT→AllDot_DMG, C_DMG→C_Damage, C_ATS→C_ATSpeed, Heal→Health_Bei, Mana→Mana_Bei, Anti→AllAnti,
MVS→MVSpeed_Bei, C_Heal→C_Health, C_Anti→C_AllAnti, Drop→ItemDrop_Rate.
FwBaseIntFields (Sanitizer:462-466): ORB_DMG→WPSPC_DMG, XJ_DMG→XJ_DMG. Any other string is blanked
on save (Sanitizer:1146-1155).

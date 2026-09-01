#!/usr/bin/env node
// Shadow Dungeon data pipeline: game tables (GBK CSV .bin) + *_FY.json
// localization  ->  compact JSON under web/public/data/.
//
// Usage:  node build-data.mjs
// Config: SD_EXTRACTED_DIR env var overrides SOURCE_DIR below.
//
// Output format: tables with >200 rows are column-oriented
// ({cols:[...], rows:[[...]]}); small tables are plain arrays of objects.
// No pretty-printing. English strings are inline (they double as the i18n
// key); all other languages go to i18n/<locale>.json packs that contain only
// the strings actually referenced by the emitted data.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";
import { fileURLToPath } from "node:url";
import { parseTable, coerce } from "./lib/csv.mjs";

// ---------------------------------------------------------------- config ---

const SOURCE_DIR =
  process.env.SD_EXTRACTED_DIR ??
  path.join(os.homedir(), "AppData", "LocalLow", "OO Cat", "ShadowDungeonSaveTool", "assets", "extracted");

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const OUT_DIR = path.resolve(__dirname, "..", "web", "public", "data");

const TABLES_DIR = path.join(SOURCE_DIR, "tables");

// Localization column -> BCP-47-ish locale code (English stays inline).
const LOCALES = {
  ChineseS: "zh-CN",
  ChineseT: "zh-TW",
  Russian: "ru",
  German: "de",
  French: "fr",
  PortugueseBrazil: "pt-BR",
  PortuguesePortugal: "pt-PT",
  Polish: "pl",
  Korean: "ko",
  SpanishSpain: "es-ES",
  SpanishLatinAmerica: "es-419",
  Turkish: "tr",
  Czech: "cs",
  Swedish: "sv",
  Italian: "it",
  Dutch: "nl",
  Ukrainian: "uk",
  Thai: "th",
  Hungarian: "hu",
  Danish: "da",
  Japanese: "ja",
  Greek: "el",
  Finnish: "fi",
};

// ---------------------------------------------------------------- helpers --

function loadTable(file) {
  const buf = fs.readFileSync(path.join(TABLES_DIR, file));
  return parseTable(buf, file);
}

function loadFY(file) {
  return JSON.parse(fs.readFileSync(path.join(SOURCE_DIR, file), "utf8"));
}

const num = (v) => {
  const c = coerce(v);
  return typeof c === "number" ? c : v === "" ? 0 : c;
};

/** Trim trailing groups (of fixed size) whose members are all zero/empty. */
function trimTrailing(groups) {
  let end = groups.length;
  while (end > 0 && groups[end - 1].every((v) => v === 0 || v === "" || v === "0")) end--;
  return groups.slice(0, end);
}

const groupsOf = (fields, start, size, count) => {
  const out = [];
  for (let g = 0; g < count; g++) {
    out.push(fields.slice(start + g * size, start + (g + 1) * size));
  }
  return out;
};

function writeJson(name, data) {
  const file = path.join(OUT_DIR, name);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const json = JSON.stringify(data);
  fs.writeFileSync(file, json);
  const gz = zlib.gzipSync(json, { level: 9 }).length;
  return { bytes: Buffer.byteLength(json), gzip: gz };
}

const colFormat = (cols, rows) => ({ cols, rows });

function fail(msg) {
  console.error(`SANITY CHECK FAILED: ${msg}`);
  process.exitCode = 1;
  throw new Error(msg);
}
const assert = (cond, msg) => {
  if (!cond) fail(msg);
};

// ---------------------------------------------------------------- weapons --
// 0_0_Weapon.bin — 86 columns:
//  0 nameCN  1 nameCN2  2 ItemName  3 GlobalID  4 DropLevelStart  5 Quality
//  6 SizeX  7 SizeY  8 CurAocaoCount  9 IconType  10 Icon
//  11 SoundDrop  12 SoundUse  13 RotateType (dropped: audio/visual)
//  14 PLtype  15 WeaponType  16 CharType  17 DMG  18 Heal  19 Mana  20 EL
//  21..41  7 x (Index, EL, NB)            main-affix slots
//  42..49  skill-affix group A (nameCN, SkName, Index, ID, EL, NB, LinkCN, Link)
//  50..57  skill-affix group B (same layout)
//  58 MainID -> 1_0_Main.ID   59 DotID -> 1_1_DOT.ID   60 SkID -> 1_2_SK.ID
//  61..78  6 x (nameCN, SkillName, Points) weapon-skill sockets (WPSK)
//  79..84  3 x (labelCN, SPC) — verified identical triplets -> single SPC id
//  85 Set -> 0_5_Set.SetID

function buildWeapons(tbl) {
  assert(tbl.width === 86, `0_0_Weapon.bin width ${tbl.width} != 86`);
  const cols = [
    "GlobalID", "ItemName", "DropLevelStart", "Quality", "SizeX", "SizeY",
    "CurAocaoCount", "IconType", "Icon", "PLtype", "WeaponType", "CharType",
    "DMG", "Heal", "Mana", "EL", "MainID", "DotID", "SkID",
    "Affixes", "SkillAffixes", "Sockets", "SPC", "Set",
  ];
  const rows = tbl.rows.map((r) => {
    const f = r.map(coerce);
    const affixes = trimTrailing(groupsOf(f, 21, 3, 7));
    const skillAffixes = [f.slice(42, 50), f.slice(50, 58)]
      .map((g) => [g[1], g[2], g[3], g[4], g[5], g[7]]) // drop CN labels
      .filter((g) => g.some((v) => v !== 0 && v !== "0" && v !== ""));
    const sockets = [];
    for (let s = 0; s < 6; s++) {
      const name = f[62 + s * 3];
      const pts = f[63 + s * 3];
      if (name !== 0 && name !== "0" && name !== "") sockets.push([name, pts]);
    }
    assert(
      f[80] === f[82] && f[82] === f[84],
      `weapon ${f[3]}: SPC slots differ (${f[80]},${f[82]},${f[84]})`
    );
    return [
      f[3], f[2], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[14], f[15],
      f[16], f[17], f[18], f[19], f[20], f[58], f[59], f[60],
      affixes, skillAffixes, sockets, f[80], f[85],
    ];
  });
  return colFormat(cols, rows);
}

// ------------------------------------------------------------------- gems --
// 0_2_Baoshi.bin — 19 cols. Dropped: SoundDrop(9) SoundUse(10) RotateType(11)
// DropSpriteSize(16).

function buildGems(tbl) {
  assert(tbl.width === 19, `0_2_Baoshi.bin width ${tbl.width} != 19`);
  return tbl.rows.map((r) => {
    const f = r.map(coerce);
    return {
      GlobalID: f[1], ItemName: f[2], Price: f[3], Quality: f[4], Icon: f[5],
      Level: f[6], UseType: f[7], BS_Quality: f[8], Bstype: f[12],
      Number: f[13], MstackSize: f[14], CstackSize: f[15], FWType: f[17],
      DropScene: f[18],
    };
  });
}

// --------------------------------------------------------------- useitems --
// 0_3_UseItem.bin — 20 cols. Dropped: SoundDrop(7) SoundUse(8) RotateType(9)
// DropSpriteSize(18).

function buildUseItems(tbl) {
  assert(tbl.width === 20, `0_3_UseItem.bin width ${tbl.width} != 20`);
  return tbl.rows.map((r) => {
    const f = r.map(coerce);
    return {
      GlobalID: f[1], ItemName: f[2], Price: f[3], Quality: f[4], Icon: f[5],
      Level: f[6], InfoType: f[10], UseType: f[11], damageType: f[12],
      Number: f[13], CDTime: f[14], Duration: f[15], MstackSize: f[16],
      CstackSize: f[17], DropScene: f[19],
    };
  });
}

// ------------------------------------------------------------------- sets --
// 0_5_Set.bin — 39 cols: nameCN, SetID, SetName,
// 3 x (MTP, nameCN, SkN, Index, ID, EL, NB, LinkCN, LinkSK), then buff block:
// buffCN, BuffName, BuffType, BuffTime, LayerMax, TP_Layer, NumberL, TP_Max,
// NumberM. Armor piece GlobalIDs are derivable (20000/30000/40000/50000 +
// SetID + 289 = head/body/hand/leg) and emitted when all 4 exist in weapons.

function buildSets(tbl, weaponIds) {
  assert(tbl.width === 39, `0_5_Set.bin width ${tbl.width} != 39`);
  return tbl.rows.map((r) => {
    const f = r.map(coerce);
    const bonuses = [];
    for (let t = 0; t < 3; t++) {
      const b = f.slice(3 + t * 9, 3 + (t + 1) * 9);
      bonuses.push({
        MTP: b[0], SkN: b[2], Index: b[3], ID: b[4], EL: b[5], NB: b[6],
        LinkSK: b[8],
      });
    }
    const setId = f[1];
    let pieces = null;
    if (typeof setId === "number" && setId > 0) {
      const p = [20000, 30000, 40000, 50000].map((base) => base + setId + 289);
      if (p.every((id) => weaponIds.has(id))) pieces = p;
    }
    return {
      SetID: setId, SetName: f[2], bonuses,
      buff: {
        BuffName: f[31], BuffType: f[32], BuffTime: f[33], LayerMax: f[34],
        TP_Layer: f[35], NumberL: f[36], TP_Max: f[37], NumberM: f[38],
      },
      pieces,
    };
  });
}

// ---------------------------------------------------------------- affixes --
// 1_0_Main.bin: nameCN, ID, 168 x (Index, EL, NB)      pool "main"
// 1_1_DOT.bin:  nameCN, ID,  36 x (Index, EL, NB)      pool "dot"
// 1_2_SK.bin:   nameCN, ID, 148 x (nameCN, SkN, Inx, ID, EL, NB, LinkCN, Link)
//                                                       pool "sk"

function buildAffixes(main, dot, sk) {
  assert(main.width === 2 + 168 * 3, `1_0_Main.bin width ${main.width}`);
  assert(dot.width === 2 + 36 * 3, `1_1_DOT.bin width ${dot.width}`);
  assert(sk.width === 2 + 148 * 8, `1_2_SK.bin width ${sk.width}`);
  const out = [];
  const triplePool = (tbl, pool, count) => {
    for (const r of tbl.rows) {
      const f = r.map(coerce);
      out.push({ pool, id: f[1], entries: trimTrailing(groupsOf(f, 2, 3, count)) });
    }
  };
  triplePool(main, "main", 168);
  triplePool(dot, "dot", 36);
  for (const r of sk.rows) {
    const f = r.map(coerce);
    const entries = groupsOf(f, 2, 8, 148)
      .filter((g) => g.some((v) => v !== 0 && v !== "0" && v !== ""))
      .map((g) => ({ SkN: g[1], Inx: g[2], ID: g[3], EL: g[4], NB: g[5], Link: g[7] }));
    out.push({ pool: "sk", id: f[1], entries });
  }
  return out;
}

// ------------------------------------------------------------------ procs --
// 0_1_SPC.bin — 103 cols of combat-engine params. Kept: identity, display
// and damage-relevant columns; the FX/projectile-geometry/sound block
// (cols 26..102) is engine noise for the item UI.

const SPC_KEEP = [
  [1, "ID"], [2, "TP"], [3, "FW"], [4, "name"], [5, "FStp"], [6, "LockType"],
  [7, "inf"], [8, "Pri"], [10, "SKName"], [12, "ZQName"], [13, "RTy"],
  [14, "Dis"], [15, "Rat"], [16, "DMG"], [17, "DMG_A"], [18, "DMG_B"],
  [19, "ThrT"], [20, "ATT"], [21, "ATT_A"], [22, "ATT_B"], [23, "No"],
  [24, "BF"], [25, "DF"],
];

function buildProcs(tbl) {
  assert(tbl.width === 103, `0_1_SPC.bin width ${tbl.width} != 103`);
  const cols = SPC_KEEP.map(([, name]) => name);
  const rows = tbl.rows.map((r) => SPC_KEEP.map(([i]) => coerce(r[i])));
  return colFormat(cols, rows);
}

// ----------------------------------------------------------------- skills --
// Talent-tree skills referenced by weapon sockets / WPSK save blocks.
// Merged from the seven talent tables; each has intact English headers, so
// columns are located by name. `src` says which table a skill came from.

const TALENT_TABLES = [
  ["0_SampleF.bin", "sampleF"],
  ["1_SampleS.bin", "sampleS"],
  ["2_CompF.bin", "compF"],
  ["3_CompS.bin", "compS"],
  ["4_DotF.bin", "dotF"],
  ["5_DotS.bin", "dotS"],
  ["6_Bei.bin", "bei"],
];

function buildSkills() {
  const cols = ["IndexName", "src", "Xi", "Level_Max", "UnLock_Point", "Price", "icon", "damageType", "Info"];
  const rows = [];
  const perTable = {};
  for (const [file, src] of TALENT_TABLES) {
    const tbl = loadTable(file);
    const at = (name) => tbl.header.indexOf(name);
    const idx = {
      IndexName: at("IndexName"), icon: at("icon"), Price: at("Price"),
      UnLock_Point: at("UnLock_Point"), Xi: at("Xi"), Level_Max: at("Level_Max"),
      Info: at("Info"), damageType: at("damageType"),
    };
    for (const k of ["IndexName", "icon", "Price", "UnLock_Point", "Xi", "Level_Max", "Info"]) {
      assert(idx[k] >= 0, `${file}: missing column ${k}`);
    }
    for (const r of tbl.rows) {
      rows.push([
        r[idx.IndexName], src, num(r[idx.Xi]), num(r[idx.Level_Max]),
        num(r[idx.UnLock_Point]), num(r[idx.Price]), num(r[idx.icon]),
        idx.damageType >= 0 ? num(r[idx.damageType]) : null,
        r[idx.Info],
      ]);
    }
    perTable[file] = tbl.rows.length;
  }
  return { data: colFormat(cols, rows), perTable };
}

// ---------------------------------------------------------------- classes --
// Xi.bin — nameCN, IndexName, icon, Level_Base, damageType, Number.
// Class id = 0-based row order (weapon/talent Xi values are 0-11; row 12
// "Paragon Talents" is the paragon tree, not a playable class).

function buildClasses(tbl) {
  assert(tbl.width === 6, `Xi.bin width ${tbl.width} != 6`);
  return tbl.rows.map((r, i) => {
    const f = r.map(coerce);
    return { id: i, IndexName: f[1], icon: f[2], Level_Base: f[3], damageType: f[4], Number: f[5] };
  });
}

// ------------------------------------------------------------------- i18n --

function buildI18n(usedKeys) {
  const sources = ["Item_FY.json", "Skill_FY.json", "SPC_FY.json", "Buff_FY.json"].map(loadFY);
  const skip = new Set(["", "0", "none", "name", "Link"]);
  const packs = {};
  for (const code of Object.values(LOCALES)) packs[code] = {};
  let resolved = 0;
  const unresolved = [];
  for (const key of usedKeys) {
    if (skip.has(key) || typeof key !== "string") continue;
    const entry = sources.find((s) => s[key])?.[key];
    if (!entry) {
      unresolved.push(key);
      continue;
    }
    resolved++;
    for (const [col, code] of Object.entries(LOCALES)) {
      const v = entry[col];
      if (v && v !== key) packs[code][key] = v; // omit untranslated == English
    }
  }
  return { packs, resolved, unresolved };
}

// ------------------------------------------------------------------- main --

function main() {
  console.log(`source: ${SOURCE_DIR}`);
  console.log(`output: ${OUT_DIR}`);
  fs.mkdirSync(OUT_DIR, { recursive: true });

  const tWeapon = loadTable("0_0_Weapon.bin");
  const tSpc = loadTable("0_1_SPC.bin");
  const tBaoshi = loadTable("0_2_Baoshi.bin");
  const tUseItem = loadTable("0_3_UseItem.bin");
  const tSet = loadTable("0_5_Set.bin");
  const tMain = loadTable("1_0_Main.bin");
  const tDot = loadTable("1_1_DOT.bin");
  const tSk = loadTable("1_2_SK.bin");
  const tXi = loadTable("Xi.bin");

  const weapons = buildWeapons(tWeapon);
  const gems = buildGems(tBaoshi);
  const useitems = buildUseItems(tUseItem);
  const weaponIds = new Set(weapons.rows.map((r) => r[0]));
  const sets = buildSets(tSet, weaponIds);
  const affixes = buildAffixes(tMain, tDot, tSk);
  const procs = buildProcs(tSpc);
  const { data: skills, perTable: talentCounts } = buildSkills();
  const classes = buildClasses(tXi);

  // ---- sanity checks -------------------------------------------------
  const wCol = Object.fromEntries(weapons.cols.map((c, i) => [c, i]));
  assert(weaponIds.size === weapons.rows.length, "duplicate weapon GlobalIDs");
  for (const r of weapons.rows) {
    const q = r[wCol.Quality], ct = r[wCol.CharType];
    assert(q >= 0 && q <= 6, `weapon ${r[0]} Quality ${q} out of 0..6`);
    assert(ct >= 0 && ct <= 9, `weapon ${r[0]} CharType ${ct} out of 0..9`);
  }
  const poolIds = { main: new Set(), dot: new Set(), sk: new Set() };
  for (const a of affixes) poolIds[a.pool].add(a.id);
  const procIds = new Set(procs.rows.map((r) => r[0]));
  for (const r of weapons.rows) {
    assert(poolIds.main.has(r[wCol.MainID]), `weapon ${r[0]}: MainID ${r[wCol.MainID]} not in main pool`);
    assert(poolIds.dot.has(r[wCol.DotID]), `weapon ${r[0]}: DotID ${r[wCol.DotID]} not in dot pool`);
    assert(poolIds.sk.has(r[wCol.SkID]), `weapon ${r[0]}: SkID ${r[wCol.SkID]} not in sk pool`);
    const spc = r[wCol.SPC];
    assert(spc === 0 || procIds.has(spc), `weapon ${r[0]}: SPC ${spc} not in 0_1_SPC`);
  }
  const ruby = gems.find((g) => g.GlobalID === 50001);
  assert(ruby && ruby.ItemName === "Legendary Ruby", "gem 50001 is not Legendary Ruby");
  const set46 = sets.find((s) => s.SetID === 46);
  assert(set46 && set46.SetName === "Vampire King", "set 46 is not Vampire King");
  assert(
    JSON.stringify(set46.pieces) === JSON.stringify([20335, 30335, 40335, 50335]),
    `set 46 pieces wrong: ${JSON.stringify(set46.pieces)}`
  );
  for (const pid of set46.pieces) {
    const piece = weapons.rows.find((r) => r[0] === pid);
    assert(piece[wCol.PLtype] === 3, `set piece ${pid} PLtype != 3`);
    assert(piece[wCol.Set] === 46, `set piece ${pid} Set != 46`);
  }
  const necroSets = sets.filter((s) => s.SetID >= 46 && s.SetID <= 60);
  assert(necroSets.length === 15 && necroSets.every((s) => s.pieces), "Necromancer sets 46-60 incomplete");
  assert(classes.length === 13 && classes[11].damageType === 3, "classes: Xi 11 should be the poison class");
  const skillNames = new Set(skills.rows.map((r) => r[0]));
  for (const r of weapons.rows) {
    for (const [name] of r[wCol.Sockets]) {
      assert(skillNames.has(name), `weapon ${r[0]} socket skill '${name}' not in skills.json`);
    }
  }

  // ---- i18n ----------------------------------------------------------
  const used = new Set();
  for (const r of weapons.rows) {
    used.add(r[wCol.ItemName]);
    for (const [name] of r[wCol.Sockets]) used.add(name);
  }
  for (const g of gems) used.add(g.ItemName);
  for (const u of useitems) used.add(u.ItemName);
  for (const s of sets) {
    used.add(s.SetName);
    used.add(s.buff.BuffName);
    for (const b of s.bonuses) used.add(b.SkN);
  }
  for (const a of affixes) {
    if (a.pool === "sk") for (const e of a.entries) used.add(e.SkN);
  }
  for (const r of procs.rows) {
    used.add(r[3]); // name -> SPC_FY
    used.add(r[8]); // SKName -> Skill_FY
  }
  for (const r of skills.rows) used.add(r[0]);
  for (const c of classes) used.add(c.IndexName);
  const { packs, resolved, unresolved } = buildI18n(used);

  // ---- write ---------------------------------------------------------
  const outputs = {};
  const emit = (name, data, rowCount) => {
    outputs[name] = { rows: rowCount, ...writeJson(name, data) };
  };
  emit("weapons.json", weapons, weapons.rows.length);
  emit("gems.json", gems, gems.length);
  emit("useitems.json", useitems, useitems.length);
  emit("sets.json", sets, sets.length);
  emit("affixes.json", affixes, affixes.length);
  emit("procs.json", procs, procs.rows.length);
  emit("skills.json", skills, skills.rows.length);
  emit("classes.json", classes, classes.length);
  for (const [code, pack] of Object.entries(packs)) {
    emit(`i18n/${code}.json`, pack, Object.keys(pack).length);
  }

  const meta = {
    generatedAt: new Date().toISOString(),
    game: "Shadow Dungeon",
    sourceTables: {
      "0_0_Weapon.bin": tWeapon.rows.length,
      "0_1_SPC.bin": tSpc.rows.length,
      "0_2_Baoshi.bin": tBaoshi.rows.length,
      "0_3_UseItem.bin": tUseItem.rows.length,
      "0_5_Set.bin": tSet.rows.length,
      "1_0_Main.bin": tMain.rows.length,
      "1_1_DOT.bin": tDot.rows.length,
      "1_2_SK.bin": tSk.rows.length,
      "Xi.bin": tXi.rows.length,
      ...talentCounts,
    },
    outputs: Object.fromEntries(
      Object.entries(outputs).map(([k, v]) => [k, { rows: v.rows, bytes: v.bytes }])
    ),
    i18n: {
      locales: Object.values(LOCALES),
      keysResolved: resolved,
      keysUnresolved: unresolved,
    },
  };
  outputs["meta.json"] = { rows: null, ...writeJson("meta.json", meta) };

  // ---- report --------------------------------------------------------
  const fmt = (n) => n.toLocaleString("en-US");
  console.log("\nfile                     rows      raw       gzip");
  let totRaw = 0, totGz = 0;
  for (const [name, o] of Object.entries(outputs)) {
    totRaw += o.bytes;
    totGz += o.gzip;
    console.log(
      `${name.padEnd(22)} ${String(o.rows ?? "-").padStart(6)} ${fmt(o.bytes).padStart(10)} ${fmt(o.gzip).padStart(10)}`
    );
  }
  console.log(`${"TOTAL".padEnd(22)} ${"".padStart(6)} ${fmt(totRaw).padStart(10)} ${fmt(totGz).padStart(10)}`);
  console.log(`\ni18n keys resolved: ${resolved}, unresolved: ${unresolved.length}`);
  if (unresolved.length) console.log("unresolved keys:", unresolved.slice(0, 20).join(", "));
  console.log("all sanity checks passed");
}

main();

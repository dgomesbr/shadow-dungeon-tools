#!/usr/bin/env node
// Shadow Dungeon story-level catalog: LevelData.json + Level_FY.json
// localization  ->  web/public/data/levels.json.
//
// Usage:  node build-levels.mjs
// Config: SD_EXTRACTED_DIR env var overrides SOURCE_DIR below.
//
// Powers the "Unlock Story levels" dialog: chapters 1..7, each with its
// mainline levels in order. Code-grounded semantics (decompiled
// Assembly-CSharp):
//  - Level ids are "CC_NN"; chapter id = int(CC)
//    (LevelManager.TryParseMainLevelId splits on '_').
//  - Story/mainline levels are LevelType Normal(0) | Boss(1)
//    (LevelManager.IsMainlineType). Challenge(3)/Mijing(4) ids (C0xx/M0xx)
//    and the "Home" hub are excluded.
//  - boss  = LevelData.Type == LevelType.Boss (LevelManager.GetIsBoss;
//    BossLevelManager only registers bosses on such levels, and adds the
//    level id to DefeatedBossLevelIds when the last boss dies).
//  - final = LevelData.IsFinal (true only for the game-final level;
//    triggers Mijing unlock in BossLevelManager).
//  - Chapter display name = LOC Level_FY["Chapter"+id] (TeleportStation),
//    level display name = LOC Level_FY[LevelData.LocalName]
//    (TeleportItem via LevelManager.GetLevelLocalKey). English column used.
//
// Unlock model (for the dialog): a level is reachable iff its id is in
// SaveData.UnlockedLevelIds AND its chapter int is in UnlockedChapterIds
// (TeleportPanel lists UnlockedLevelIds filtered by chapter; the chapter's
// TeleportStation in Home is enabled by UnlockedChapterIds). Beating the
// boss of a chapter's last level adds the next chapter id + its first level
// (BossLevelManager.UnregisterBoss).

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";
import { fileURLToPath } from "node:url";

// ---------------------------------------------------------------- config ---

const SOURCE_DIR =
  process.env.SD_EXTRACTED_DIR ??
  path.join(os.homedir(), "AppData", "LocalLow", "OO Cat", "ShadowDungeonSaveTool", "assets", "extracted");

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const OUT_DIR = path.resolve(__dirname, "..", "web", "public", "data");

// LevelType enum (Level.LevelStates.LevelType):
// 0 Normal, 1 Boss, 2 Optional, 3 Challenge, 4 Mijing
const TYPE_NORMAL = 0;
const TYPE_BOSS = 1;

// ---------------------------------------------------------------- helpers --

function loadJson(file) {
  return JSON.parse(fs.readFileSync(path.join(SOURCE_DIR, file), "utf8"));
}

function writeJson(name, data) {
  const file = path.join(OUT_DIR, name);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const json = JSON.stringify(data);
  fs.writeFileSync(file, json);
  const gz = zlib.gzipSync(json, { level: 9 }).length;
  return { bytes: Buffer.byteLength(json), gzip: gz };
}

function fail(msg) {
  console.error(`SANITY CHECK FAILED: ${msg}`);
  process.exitCode = 1;
  throw new Error(msg);
}
const assert = (cond, msg) => {
  if (!cond) fail(msg);
};

// Mirrors LevelManager.TryParseMainLevelId: "CC_NN" -> [chapter, index].
function parseMainLevelId(id) {
  const m = /^(\d{2})_(\d{2})$/.exec(id);
  if (!m) return null;
  return [parseInt(m[1], 10), parseInt(m[2], 10)];
}

// ------------------------------------------------------------------- main --

function main() {
  console.log(`source: ${SOURCE_DIR}`);
  console.log(`output: ${OUT_DIR}`);

  const levelData = loadJson("LevelData.json").items;
  const fy = loadJson("Level_FY.json");

  const english = (key) => fy[key]?.English || null;

  const missingNames = [];
  const byChapter = new Map();
  for (const item of levelData) {
    const parsed = parseMainLevelId(item.GlobalID);
    if (!parsed) continue; // Home hub, C0xx challenges, M0xx mijing
    const [chapterId, index] = parsed;
    assert(
      item.Type === TYPE_NORMAL || item.Type === TYPE_BOSS,
      `level ${item.GlobalID}: CC_NN id but non-mainline Type ${item.Type}`
    );
    let name = english(item.LocalName);
    if (!name) {
      missingNames.push(`${item.GlobalID} (${item.LocalName})`);
      name = item.GlobalID; // fallback
    }
    if (!byChapter.has(chapterId)) byChapter.set(chapterId, []);
    byChapter.get(chapterId).push({
      index,
      level: {
        id: item.GlobalID,
        name,
        boss: item.Type === TYPE_BOSS,
        final: item.IsFinal === true,
      },
    });
  }

  const chapterIds = [...byChapter.keys()].sort((a, b) => a - b);
  const chapters = chapterIds.map((id) => {
    const entries = byChapter.get(id).sort((a, b) => a.index - b.index);
    entries.forEach(({ index }, i) => {
      assert(index === i + 1, `chapter ${id}: level indices not contiguous at NN=${index}`);
    });
    return {
      id,
      name: english(`Chapter${id}`) ?? `Chapter ${id}`,
      levels: entries.map((e) => e.level),
    };
  });

  // ---- sanity checks -------------------------------------------------
  // Expectations cross-checked against a finished save: chapters 1..7,
  // 130 UnlockedLevelIds, 60 DefeatedBossLevelIds (subset of boss levels).
  assert(
    JSON.stringify(chapterIds) === JSON.stringify([1, 2, 3, 4, 5, 6, 7]),
    `chapters ${chapterIds} != 1..7`
  );
  const totalLevels = chapters.reduce((n, c) => n + c.levels.length, 0);
  assert(totalLevels === 130, `total story levels ${totalLevels} != 130`);
  const totalBosses = chapters.reduce((n, c) => n + c.levels.filter((l) => l.boss).length, 0);
  assert(totalBosses === 60, `total boss levels ${totalBosses} != 60`);
  for (const c of chapters) {
    const last = c.levels[c.levels.length - 1];
    assert(last.boss, `chapter ${c.id}: last level ${last.id} is not a boss level`);
  }
  const finals = chapters.flatMap((c) => c.levels.filter((l) => l.final).map((l) => l.id));
  assert(
    JSON.stringify(finals) === JSON.stringify(["07_19"]),
    `IsFinal levels ${finals} != ["07_19"]`
  );

  // ---- write ---------------------------------------------------------
  const { bytes, gzip } = writeJson("levels.json", { chapters });

  // ---- report --------------------------------------------------------
  console.log("\nchapter  name        levels  bosses");
  for (const c of chapters) {
    const bosses = c.levels.filter((l) => l.boss).length;
    console.log(
      `${String(c.id).padEnd(8)} ${c.name.padEnd(11)} ${String(c.levels.length).padStart(6)} ${String(bosses).padStart(7)}`
    );
  }
  console.log(`TOTAL${" ".repeat(15)} ${String(totalLevels).padStart(6)} ${String(totalBosses).padStart(7)}`);
  console.log(`\nlevels.json: ${bytes.toLocaleString("en-US")} bytes (${gzip.toLocaleString("en-US")} gzip)`);
  if (missingNames.length) {
    console.log(`levels missing English names (id used as fallback): ${missingNames.join(", ")}`);
  } else {
    console.log("all levels resolved to English names");
  }
  console.log("all sanity checks passed");
}

main();

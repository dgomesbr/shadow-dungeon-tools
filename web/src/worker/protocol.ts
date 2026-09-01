// Message protocol between the UI thread and the save worker. Parsing and
// encoding run off-main-thread; buffers are transferred, never copied.

export type WorkerRequest =
  | { id: number; op: 'parse'; buffer: ArrayBuffer; fileName: string }
  | { id: number; op: 'set'; fileName: string; sets: SetOp[] }
  | { id: number; op: 'detail'; fileName: string; handle: string }
  | { id: number; op: 'unlock'; fileName: string; unlock: UnlockOp }
  | { id: number; op: 'encode'; fileName: string };

export interface UnlockOp {
  /** Chapter ids to add to UnlockedChapterIds (existing ones are skipped). */
  chapters: number[];
  /** Level ids to add to UnlockedLevelIds. */
  levels: string[];
  /** Level ids to add to DefeatedBossLevelIds (normally empty — unlocking ≠ defeating). */
  bossLevels: string[];
  /** Raise mijing floors (never lowers) and set UnlockedMijing=true. */
  mijing?: { easy?: number; medium?: number; hard?: number; master?: number };
}

export interface MijingState {
  unlocked: boolean;
  easy: number;
  medium: number;
  hard: number;
  master: number;
}

export interface SetOp {
  handle: string;
  value: number | string | boolean;
}

export type WorkerResponse =
  | { id: number; ok: true; op: 'parse'; summary: SaveSummary; roundTrip: boolean; firstDiff: number }
  | { id: number; ok: true; op: 'set'; summary: SaveSummary }
  | { id: number; ok: true; op: 'detail'; leaves: Leaf[] }
  | { id: number; ok: true; op: 'unlock'; summary: SaveSummary; added: { chapters: number; levels: number; bossLevels: number } }
  | { id: number; ok: true; op: 'encode'; buffer: ArrayBuffer }
  | { id: number; ok: false; error: string };

export type LeafKind = 'int' | 'float' | 'long' | 'string' | 'bool';

export interface Leaf {
  /** Member name (or dotted path for deep leaves). */
  name: string;
  /** Child-index path from the root node — pass back in SetOp. */
  handle: string;
  kind: LeafKind;
  /** long values outside Number.MAX_SAFE_INTEGER arrive as decimal strings. */
  value: number | string | boolean;
}

export type Rec = Record<string, number | string | boolean>;

export interface ItemSummary {
  handle: string;
  kind: 'weapon' | 'gem' | 'useitem';
  page: number;   // -1 for equipped items
  gridX: number;
  gridY: number;
  slot: number;   // CharType 0-9 for equipped items, -1 otherwise
  globalId: number;
  quality: number;
  charType: number;
  plType: number;
  stack: number;
  /** Baoshi template key (runes). */
  index: number;
  /** Direct scalar fields of the item payload. */
  leaves: Leaf[];
  /** Weapon-only structured data for game-style tooltips. */
  main?: Rec[];   // WPDT_A {Index, EL, number}
  dot?: Rec[];
  wpsk?: Rec[];   // skill sockets {IndexName, Number, Number2, price}
  aocao?: Rec[];  // gem sockets {Type, ...}
  spc?: Rec[];    // proc instances
}

export interface TalentSkill {
  name: string;
  level: number;
  /** Handle of the Level_Base leaf. */
  handle: string;
}

export interface SaveSummary {
  fileName: string;
  gameVersion: string;
  playTime: number;
  /** Direct scalar fields of PlayerData (protected fields excluded). */
  player: Leaf[];
  money: Leaf | null;
  pageCount: number;
  equipment: ItemSummary[];
  inventory: ItemSummary[];
  chest: ItemSummary[];
  /** TalentData: P_Base/P_Used/P_Used_DF etc. */
  talentPoints: Leaf[];
  /** All_Skill_Datas entries (every skill, invested or not). */
  talents: TalentSkill[];
  /** Story/realm unlock state (root-level fields). */
  unlockedChapters: number[];
  unlockedLevels: string[];
  defeatedBossLevels: string[];
  mijing: MijingState;
}

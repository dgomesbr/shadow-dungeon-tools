// Message protocol between the UI thread and the save worker. Parsing and
// encoding run off-main-thread; buffers are transferred, never copied.

export type WorkerRequest =
  | { id: number; op: 'parse'; buffer: ArrayBuffer; fileName: string }
  | { id: number; op: 'set'; fileName: string; sets: SetOp[] }
  | { id: number; op: 'detail'; fileName: string; handle: string }
  | { id: number; op: 'encode'; fileName: string };

export interface SetOp {
  handle: string;
  value: number | string | boolean;
}

export type WorkerResponse =
  | { id: number; ok: true; op: 'parse'; summary: SaveSummary; roundTrip: boolean; firstDiff: number }
  | { id: number; ok: true; op: 'set'; summary: SaveSummary }
  | { id: number; ok: true; op: 'detail'; leaves: Leaf[] }
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
}

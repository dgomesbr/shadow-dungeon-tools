// Message protocol between the UI thread and the save worker. Parsing and
// encoding run off-main-thread; buffers are transferred, never copied.

export type WorkerRequest =
  | { id: number; op: 'parse'; buffer: ArrayBuffer; fileName: string }
  | { id: number; op: 'encode'; patch: unknown; fileName: string }
  | { id: number; op: 'roundtrip'; buffer: ArrayBuffer };

export type WorkerResponse =
  | { id: number; ok: true; op: 'parse'; summary: SaveSummary }
  | { id: number; ok: true; op: 'encode'; buffer: ArrayBuffer }
  | { id: number; ok: true; op: 'roundtrip'; identical: boolean; firstDiff: number }
  | { id: number; ok: false; error: string };

/** Flat, UI-friendly projection of a parsed save (the full tree stays in the worker). */
export interface SaveSummary {
  fileName: string;
  gameVersion: string;
  backupKind: number;
  player: Record<string, number | string | boolean>;
  equipment: ItemSummary[];
  inventory: ItemSummary[];
  globalChest: ItemSummary[];
  pageCount: number;
}

export interface ItemSummary {
  /** Stable handle for edit operations: index path within the tree. */
  handle: string;
  itemType: number;
  page: number;
  gridX: number;
  gridY: number;
  /** Template key: GlobalID for weapons, Index for gems, etc. */
  templateId: number;
  name: string;
  quality: number;
  charType: number;
  plType: number;
  stack: number;
  /** All numeric/string leaf fields of the payload, path → value. */
  fields: Record<string, number | string | boolean>;
}

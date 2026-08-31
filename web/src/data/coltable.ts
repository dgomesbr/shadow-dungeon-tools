// Column-oriented table codec: {cols: string[], rows: unknown[][]}.
// Access is by pre-resolved column index — no per-row object allocation.
export interface ColTable {
  cols: string[];
  rows: unknown[][];
}

export class Table {
  readonly cols: string[];
  readonly rows: unknown[][];
  private readonly index: Map<string, number>;

  constructor(raw: ColTable) {
    this.cols = raw.cols;
    this.rows = raw.rows;
    this.index = new Map(raw.cols.map((c, i) => [c, i]));
  }

  get length(): number {
    return this.rows.length;
  }

  col(name: string): number {
    const i = this.index.get(name);
    if (i === undefined) throw new Error(`missing column ${name}`);
    return i;
  }

  /** Build a Map from a key column value to row index for O(1) joins. */
  keyBy(name: string): Map<unknown, number> {
    const c = this.col(name);
    const m = new Map<unknown, number>();
    const rows = this.rows;
    for (let i = 0; i < rows.length; i++) m.set(rows[i]![c], i);
    return m;
  }
}

const cache = new Map<string, Promise<unknown>>();

export function loadJSON<T>(path: string): Promise<T> {
  let p = cache.get(path);
  if (!p) {
    p = fetch(`${import.meta.env.BASE_URL}data/${path}`).then((r) => {
      if (!r.ok) throw new Error(`${path}: HTTP ${r.status}`);
      return r.json();
    });
    cache.set(path, p);
  }
  return p as Promise<T>;
}

export async function loadTable(path: string): Promise<Table> {
  const raw = await loadJSON<ColTable | unknown[]>(path);
  if (Array.isArray(raw)) {
    // Small tables may ship as arrays of objects; normalize.
    const cols = raw.length ? Object.keys(raw[0] as object) : [];
    return new Table({ cols, rows: (raw as Record<string, unknown>[]).map((o) => cols.map((c) => o[c])) });
  }
  return new Table(raw);
}

// Unified item catalog built from the generated data files. Loaded once,
// kept as flat parallel arrays for allocation-free filtering.
import { loadJSON, loadTable, Table } from './coltable';

export const QUALITY_NAMES = ['Normal', 'Magic', 'Rare', 'Exquisite', 'Epic', 'Legendary', 'Mythical', 'Q7', 'Q8'] as const;
export const SLOT_NAMES = ['Main Hand', 'Off Hand', 'Head', 'Body', 'Hands', 'Feet', 'Accessory', 'Accessory', 'Accessory', 'Accessory'] as const;
export const ELEMENT_NAMES = ['Fire', 'Frozen', 'Thunder', 'Poison', 'Physics', 'Shadow'] as const;

export type Category = 'weapon' | 'armor' | 'accessory' | 'gem' | 'consumable' | 'set';
export const CATEGORY_LABELS: Record<Category, string> = {
  weapon: 'Weapons', armor: 'Armor', accessory: 'Accessories',
  gem: 'Gems', consumable: 'Consumables', set: 'Sets',
};

export interface IconsIndex {
  cellSizePx: number;
  sprites: Record<string, { path: string; w: number; h: number }>;
  weaponIconTypes: { iconType: number; sheet: string; sprites: string[] }[];
  gemIcons: { sheet: string; sprites: string[] };
  useItemIcons: { sheet: string; sprites: string[] };
  special: { skillRuneByElement: string[]; spcRune: string; baseRune: string; doubleIcons: string[] };
}

export interface CatalogEntry {
  id: number;            // GlobalID (or SetID for sets)
  cat: Category;
  name: string;
  quality: number;
  level: number;
  slot: number;          // CharType, -1 for non-equipment
  plType: number;
  setId: number;         // 0 = none
  icon: string;          // resolved path (relative to BASE_URL)
  iconW: number;
  iconH: number;
  search: string;        // precomputed lowercase key
  /** Source for the detail panel: table+row index or plain object. */
  src: { table: Table; row: number } | Record<string, unknown>;
}

export interface Catalog {
  entries: CatalogEntry[];
  weapons: Table;
  skills: Table;
  procs: Table;
  gems: Record<string, unknown>[];
  useitems: Record<string, unknown>[];
  sets: Record<string, unknown>[];
  affixes: { pool: string; id: number; entries: unknown[] }[];
  icons: IconsIndex;
  weaponById: Map<unknown, number>;
  affixByPoolId: Map<string, { pool: string; id: number; entries: unknown[] }>;
  skillByIndexName: Map<unknown, number>;
  procById: Map<unknown, number>;
  setById: Map<unknown, Record<string, unknown>>;
}

let catalogPromise: Promise<Catalog> | null = null;

export function loadCatalog(): Promise<Catalog> {
  return (catalogPromise ??= buildCatalog());
}

function iconOf(idx: IconsIndex, name: string | undefined): { path: string; w: number; h: number } {
  const s = name ? idx.sprites[name] : undefined;
  return s ?? { path: '', w: 60, h: 60 };
}

async function buildCatalog(): Promise<Catalog> {
  const [weapons, skills, procs, gems, useitems, sets, affixes, icons] = await Promise.all([
    loadTable('weapons.json'),
    loadTable('skills.json'),
    loadTable('procs.json'),
    loadJSON<Record<string, unknown>[]>('gems.json'),
    loadJSON<Record<string, unknown>[]>('useitems.json'),
    loadJSON<Record<string, unknown>[]>('sets.json'),
    loadJSON<{ pool: string; id: number; entries: unknown[] }[]>('affixes.json'),
    loadJSON<IconsIndex>('icons-index.json').catch(() =>
      fetch(`${import.meta.env.BASE_URL}icons/icons-index.json`).then((r) => r.json()),
    ),
  ]);

  const entries: CatalogEntry[] = [];
  const wc = {
    id: weapons.col('GlobalID'), name: weapons.col('ItemName'), q: weapons.col('Quality'),
    lvl: weapons.col('DropLevelStart'), iconType: weapons.col('IconType'), icon: weapons.col('Icon'),
    pl: weapons.col('PLtype'), slot: weapons.col('CharType'), set: weapons.col('Set'),
  };
  for (let i = 0; i < weapons.length; i++) {
    const r = weapons.rows[i]!;
    const slot = r[wc.slot] as number;
    const spriteName = icons.weaponIconTypes[r[wc.iconType] as number]?.sprites[r[wc.icon] as number];
    const ic = iconOf(icons, spriteName);
    const name = String(r[wc.name]);
    entries.push({
      id: r[wc.id] as number,
      cat: slot <= 1 ? 'weapon' : slot <= 5 ? 'armor' : 'accessory',
      name, quality: r[wc.q] as number, level: r[wc.lvl] as number,
      slot, plType: r[wc.pl] as number, setId: (r[wc.set] as number) || 0,
      icon: ic.path, iconW: ic.w, iconH: ic.h,
      search: name.toLowerCase(),
      src: { table: weapons, row: i },
    });
  }
  for (const g of gems) {
    const ic = iconOf(icons, icons.gemIcons.sprites[g['Icon'] as number]);
    const name = String(g['ItemName']);
    entries.push({
      id: g['GlobalID'] as number, cat: 'gem', name,
      quality: Math.min(g['Quality'] as number, 8), level: (g['Level'] as number) || 0,
      slot: -1, plType: -1, setId: 0,
      icon: ic.path, iconW: ic.w, iconH: ic.h,
      search: name.toLowerCase(), src: g,
    });
  }
  for (const u of useitems) {
    const ic = iconOf(icons, icons.useItemIcons.sprites[u['Icon'] as number]);
    const name = String(u['ItemName']);
    entries.push({
      id: u['GlobalID'] as number, cat: 'consumable', name,
      quality: Math.min(u['Quality'] as number, 8), level: (u['Level'] as number) || 0,
      slot: -1, plType: -1, setId: 0,
      icon: ic.path, iconW: ic.w, iconH: ic.h,
      search: name.toLowerCase(), src: u,
    });
  }
  const weaponById = new Map<unknown, number>();
  for (let i = 0; i < weapons.length; i++) weaponById.set(weapons.rows[i]![wc.id], i);
  for (const s of sets) {
    const setId = s['SetID'] as number;
    if (!setId) continue;
    const pieces = (s['pieces'] as number[]) ?? [];
    const head = weaponById.get(pieces[0]);
    let icon = { path: '', w: 60, h: 60 };
    if (head !== undefined) {
      const r = weapons.rows[head]!;
      icon = iconOf(icons, icons.weaponIconTypes[r[wc.iconType] as number]?.sprites[r[wc.icon] as number]);
    }
    const name = String(s['SetName']);
    entries.push({
      id: setId, cat: 'set', name, quality: 6, level: 0, slot: -1, plType: -1, setId,
      icon: icon.path, iconW: icon.w, iconH: icon.h,
      search: name.toLowerCase(), src: s,
    });
  }

  const affixByPoolId = new Map<string, { pool: string; id: number; entries: unknown[] }>();
  for (const a of affixes) affixByPoolId.set(`${a.pool}:${a.id}`, a);

  return {
    entries, weapons, skills, procs, gems, useitems, sets, affixes, icons,
    weaponById, affixByPoolId,
    skillByIndexName: skills.keyBy('IndexName'),
    procById: procs.keyBy('ID'),
    setById: new Map(sets.map((s) => [s['SetID'], s])),
  };
}

/** Materialize a column-table row as an object (detail panel only). */
export function rowObj(table: Table, row: number): Record<string, unknown> {
  const o: Record<string, unknown> = {};
  const r = table.rows[row]!;
  for (let c = 0; c < table.cols.length; c++) o[table.cols[c]!] = r[c];
  return o;
}

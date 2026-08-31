// Unified item catalog built from the generated data files. Loaded once,
// kept as flat parallel arrays for allocation-free filtering.
import { loadJSON, loadTable, Table } from './coltable';

export const QUALITY_NAMES = ['Normal', 'Magic', 'Rare', 'Exquisite', 'Epic', 'Legendary', 'Mythical', 'Q7', 'Q8'] as const;
export const SLOT_NAMES = ['Main Hand', 'Off Hand', 'Head', 'Body', 'Hands', 'Feet', 'Amulet', 'Orb', 'Ring', 'Jewelry'] as const;
export const ELEMENT_NAMES = ['Fire', 'Frozen', 'Thunder', 'Poison', 'Physics', 'Shadow'] as const;

export type Category = 'weapon' | 'armor' | 'accessory' | 'gem' | 'consumable' | 'set';
export const CATEGORY_LABELS: Record<Category, string> = {
  weapon: 'Weapons', armor: 'Armor', accessory: 'Accessories',
  gem: 'Gems', consumable: 'Consumables', set: 'Sets',
};

// Icon paths are fully deterministic (docs/icons.md): sprite name suffix ==
// array index, so no runtime index file is needed. IconType → sheet, in the
// game's serialized IconData order.
export const ICON_SHEETS = [
  'StaffC', 'StaffD', 'StaffB', 'StaffA', 'SpellA', 'SpellB',
  'SwordA', 'SwordB', 'SwordC', 'ShieldA', 'ShieldB', 'ShieldC',
  'BowB', 'BowC', 'BowA', 'ArrowC', 'ArrowB', 'ArrowA',
  'StickB', 'StickD', 'StickC', 'StickA', 'CorpseC', 'CorpseB',
  'CorpseA', 'HeadA', 'ArmorA', 'HandA', 'ShoesA', 'CrossA',
  'PearlA', 'RingA', 'JewelA',
] as const;

export function weaponIconPath(iconType: number, icon: number): string {
  const sheet = ICON_SHEETS[iconType];
  return sheet ? `icons/weapons/${sheet}_${icon}.png` : '';
}
export function gemIconPath(icon: number): string {
  return `icons/gems/LittleC_${icon}.png`;
}
export function useIconPath(icon: number): string {
  return `icons/consumables/LittleA_${icon}.png`;
}
/** ItemIconUtil.GetBaoshiIcon overrides for rune-type gems. */
export const RUNE_ICONS = {
  byElement: [75, 76, 77, 78, 79, 80].map((i) => `icons/gems/LittleC_${i}.png`),
  spc: 'icons/gems/LittleC_81.png',
  base: 'icons/gems/LittleC_82.png',
};

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
  search: string;        // precomputed lowercase key
  /** Source for the detail panel: table+row index or plain object. */
  src: { table: Table; row: number } | Record<string, unknown>;
}

export interface Catalog {
  entries: CatalogEntry[];
  weapons: Table;
  skills: Table;
  /** Loaded on demand — 6.9k rows only the detail panel needs. */
  procs?: Table;
  procById?: Map<unknown, number>;
  loadProcs(): Promise<void>;
  gems: Record<string, unknown>[];
  useitems: Record<string, unknown>[];
  sets: Record<string, unknown>[];
  affixes: { pool: string; id: number; entries: unknown[] }[];
  weaponById: Map<unknown, number>;
  affixByPoolId: Map<string, { pool: string; id: number; entries: unknown[] }>;
  skillByIndexName: Map<unknown, number>;
  setById: Map<unknown, Record<string, unknown>>;
}

let catalogPromise: Promise<Catalog> | null = null;

export function loadCatalog(): Promise<Catalog> {
  return (catalogPromise ??= buildCatalog());
}

async function buildCatalog(): Promise<Catalog> {
  const [weapons, skills, gems, useitems, sets, affixes] = await Promise.all([
    loadTable('weapons.json'),
    loadTable('skills.json'),
    loadJSON<Record<string, unknown>[]>('gems.json'),
    loadJSON<Record<string, unknown>[]>('useitems.json'),
    loadJSON<Record<string, unknown>[]>('sets.json'),
    loadJSON<{ pool: string; id: number; entries: unknown[] }[]>('affixes.json'),
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
    const name = String(r[wc.name]);
    entries.push({
      id: r[wc.id] as number,
      cat: slot <= 1 ? 'weapon' : slot <= 5 ? 'armor' : 'accessory',
      name, quality: r[wc.q] as number, level: r[wc.lvl] as number,
      slot, plType: r[wc.pl] as number, setId: (r[wc.set] as number) || 0,
      icon: weaponIconPath(r[wc.iconType] as number, r[wc.icon] as number),
      search: name.toLowerCase(),
      src: { table: weapons, row: i },
    });
  }
  for (const g of gems) {
    const name = String(g['ItemName']);
    entries.push({
      id: g['GlobalID'] as number, cat: 'gem', name,
      quality: Math.min(g['Quality'] as number, 8), level: (g['Level'] as number) || 0,
      slot: -1, plType: -1, setId: 0,
      icon: gemIconPath(g['Icon'] as number),
      search: name.toLowerCase(), src: g,
    });
  }
  for (const u of useitems) {
    const name = String(u['ItemName']);
    entries.push({
      id: u['GlobalID'] as number, cat: 'consumable', name,
      quality: Math.min(u['Quality'] as number, 8), level: (u['Level'] as number) || 0,
      slot: -1, plType: -1, setId: 0,
      icon: useIconPath(u['Icon'] as number),
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
    let icon = '';
    if (head !== undefined) {
      const r = weapons.rows[head]!;
      icon = weaponIconPath(r[wc.iconType] as number, r[wc.icon] as number);
    }
    const name = String(s['SetName']);
    entries.push({
      id: setId, cat: 'set', name, quality: 6, level: 0, slot: -1, plType: -1, setId,
      icon,
      search: name.toLowerCase(), src: s,
    });
  }

  const affixByPoolId = new Map<string, { pool: string; id: number; entries: unknown[] }>();
  for (const a of affixes) affixByPoolId.set(`${a.pool}:${a.id}`, a);

  let procsLoading: Promise<void> | null = null;
  const catalog: Catalog = {
    entries, weapons, skills, gems, useitems, sets, affixes,
    weaponById, affixByPoolId,
    skillByIndexName: skills.keyBy('IndexName'),
    setById: new Map(sets.map((s) => [s['SetID'], s])),
    loadProcs() {
      return (procsLoading ??= loadTable('procs.json').then((t) => {
        catalog.procs = t;
        catalog.procById = t.keyBy('ID');
      }));
    },
  };
  return catalog;
}

/** Materialize a column-table row as an object (detail panel only). */
export function rowObj(table: Table, row: number): Record<string, unknown> {
  const o: Record<string, unknown> = {};
  const r = table.rows[row]!;
  for (let c = 0; c < table.cols.length; c++) o[table.cols[c]!] = r[c];
  return o;
}

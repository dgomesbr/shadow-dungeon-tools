// Worker-side: projects a parsed OdinDocument into the flat, UI-friendly
// SaveSummary, and applies edits addressed by node handles.
// A handle is a dot-joined child-index path from the root node, e.g. "9.2.0.5".
import { child, isContainer, type AnyNode, type OdinDocument } from '../odin/tree';
import type { ItemSummary, Leaf, LeafKind, SaveSummary } from '../worker/protocol';

const PROTECTED = new Set([
  'GameVersion', 'BackupKind', 'SaveCreatedUtcTicks', 'SessionBaselineUtcTicks',
  'SessionId', 'SaveTransactionId',
]);

export function resolveHandle(doc: OdinDocument, handle: string): AnyNode {
  let cur: AnyNode = doc.root[0]!;
  if (handle === '') return cur;
  for (const part of handle.split('.')) {
    if (!isContainer(cur)) throw new Error(`handle ${handle}: not a container at ${part}`);
    const next = cur.children[Number(part)] as AnyNode | undefined;
    if (!next) throw new Error(`handle ${handle}: missing child ${part}`);
    cur = next;
  }
  return cur;
}

function leafKind(n: AnyNode): LeafKind | null {
  switch (n.kind) {
    case 'prim':
      return n.prim === 'float' || n.prim === 'double' ? 'float'
        : n.prim === 'long' || n.prim === 'ulong' ? 'long' : 'int';
    case 'string': return 'string';
    case 'bool': return 'bool';
    default: return null;
  }
}

function leafValue(n: AnyNode): number | string | boolean {
  if (n.kind === 'prim') {
    if (typeof n.value === 'bigint') {
      return n.value >= BigInt(Number.MIN_SAFE_INTEGER) && n.value <= BigInt(Number.MAX_SAFE_INTEGER)
        ? Number(n.value)
        : n.value.toString();
    }
    return n.value;
  }
  if (n.kind === 'string') return n.value;
  if (n.kind === 'bool') return n.value;
  throw new Error('not a leaf');
}

/** Shallow scalar children of a container, with handles. */
export function shallowLeaves(n: AnyNode, baseHandle: string, includeProtected = false): Leaf[] {
  const out: Leaf[] = [];
  if (!isContainer(n)) return out;
  for (let i = 0; i < n.children.length; i++) {
    const c = n.children[i] as AnyNode;
    const kind = leafKind(c);
    if (!kind || c.name === undefined) continue;
    if (!includeProtected && PROTECTED.has(c.name)) continue;
    out.push({
      name: c.name,
      handle: baseHandle === '' ? String(i) : `${baseHandle}.${i}`,
      kind,
      value: leafValue(c),
    });
  }
  return out;
}

/** All scalar leaves in a subtree (recursive), path-named. Used by the item editor. */
export function deepLeaves(n: AnyNode, baseHandle: string, baseName = '', out: Leaf[] = []): Leaf[] {
  if (!isContainer(n)) return out;
  for (let i = 0; i < n.children.length; i++) {
    const c = n.children[i] as AnyNode;
    const handle = baseHandle === '' ? String(i) : `${baseHandle}.${i}`;
    const label = c.name !== undefined
      ? (baseName ? `${baseName}.${c.name}` : c.name)
      : `${baseName}[${i}]`;
    const kind = leafKind(c);
    if (kind) {
      out.push({ name: label, handle, kind, value: leafValue(c) });
    } else if (isContainer(c)) {
      deepLeaves(c, handle, label, out);
    }
  }
  return out;
}

export function applySet(doc: OdinDocument, handle: string, value: number | string | boolean): void {
  const n = resolveHandle(doc, handle);
  if (n.name !== undefined && PROTECTED.has(n.name)) throw new Error(`${n.name} is protected`);
  switch (n.kind) {
    case 'prim':
      if (n.prim === 'long' || n.prim === 'ulong') n.value = BigInt(value as number | string);
      else if (n.prim === 'float' || n.prim === 'double') n.value = Number(value);
      else n.value = Math.trunc(Number(value));
      return;
    case 'string':
      n.value = String(value);
      return;
    case 'bool':
      n.value = Boolean(value);
      return;
    default:
      throw new Error(`cannot set node kind ${n.kind}`);
  }
}

// ---- item extraction --------------------------------------------------------

function num(n: AnyNode | undefined, name: string): number {
  const c = n ? child(n, name) : undefined;
  if (c && c.kind === 'prim') return typeof c.value === 'bigint' ? Number(c.value) : c.value;
  return 0;
}

const PAYLOAD_NAMES = ['Weapon', 'Baoshi', 'UseItem'] as const;

type Rec = Record<string, number | string | boolean>;

/** Scalar maps for each element of a named list/array member (e.g. Main, WPSK). */
function elementRecords(payload: AnyNode, name: string): Rec[] {
  const holder = child(payload, name);
  if (!holder || !isContainer(holder)) return [];
  const out: Rec[] = [];
  for (const c of holder.children) {
    const arr = c as AnyNode;
    if (arr.kind !== 'array') continue;
    for (const el of arr.children) {
      const e = el as AnyNode;
      if (!isContainer(e)) continue;
      const rec: Rec = {};
      for (const leaf of e.children) {
        const l = leaf as AnyNode;
        if (l.name !== undefined && leafKind(l)) rec[l.name] = leafValue(l);
      }
      out.push(rec);
    }
  }
  return out;
}

function itemFromPayload(
  payload: AnyNode, payloadHandle: string, kind: ItemSummary['kind'],
  pos: { page: number; gridX: number; gridY: number; slot: number },
): ItemSummary {
  const it: ItemSummary = {
    handle: payloadHandle,
    kind,
    ...pos,
    globalId: num(payload, 'GlobalID'),
    quality: num(payload, 'Quality'),
    charType: num(payload, 'CharType'),
    plType: num(payload, 'PLtype'),
    stack: num(payload, 'CstackSize'),
    index: num(payload, 'Index'),
    leaves: shallowLeaves(payload, payloadHandle),
  };
  if (kind === 'weapon') {
    it.main = elementRecords(payload, 'Main');
    it.dot = elementRecords(payload, 'DOT');
    it.wpsk = elementRecords(payload, 'WPSK');
    it.aocao = elementRecords(payload, 'Aocao');
    it.spc = elementRecords(payload, 'SPC');
  }
  return it;
}

function wrappersToItems(listNode: AnyNode, listHandle: string): ItemSummary[] {
  // List<ContainerItemSaveData> → [array] → wrappers
  const out: ItemSummary[] = [];
  if (!isContainer(listNode)) return out;
  for (let a = 0; a < listNode.children.length; a++) {
    const arr = listNode.children[a] as AnyNode;
    if (arr.kind !== 'array') continue;
    const arrHandle = `${listHandle}.${a}`;
    for (let w = 0; w < arr.children.length; w++) {
      const wrap = arr.children[w] as AnyNode;
      if (!isContainer(wrap)) continue;
      const wrapHandle = `${arrHandle}.${w}`;
      const pos = {
        page: num(wrap, 'Page'),
        gridX: num(wrap, 'GridX'),
        gridY: num(wrap, 'GridY'),
        slot: -1,
      };
      for (const pn of PAYLOAD_NAMES) {
        for (let c = 0; c < wrap.children.length; c++) {
          const cand = wrap.children[c] as AnyNode;
          if (cand.name === pn && (cand.kind === 'ref' || cand.kind === 'struct')) {
            out.push(itemFromPayload(cand, `${wrapHandle}.${c}`,
              pn === 'Weapon' ? 'weapon' : pn === 'Baoshi' ? 'gem' : 'useitem', pos));
          }
        }
      }
    }
  }
  return out;
}

// ---- unlock sets (UnlockedChapterIds / UnlockedLevelIds / …) -----------------

/** The array node inside a serialized HashSet/List root child, or null. */
function setArray(root: AnyNode, name: string): Extract<AnyNode, { kind: 'array' }> | null {
  const holder = child(root, name);
  if (!holder || !isContainer(holder)) return null;
  for (const c of holder.children) {
    if ((c as AnyNode).kind === 'array') return c as Extract<AnyNode, { kind: 'array' }>;
  }
  return null;
}

function setValues(root: AnyNode, name: string): (number | string)[] {
  const arr = setArray(root, name);
  if (!arr) return [];
  const out: (number | string)[] = [];
  for (const c of arr.children) {
    const n = c as AnyNode;
    if (n.kind === 'prim' && typeof n.value !== 'bigint') out.push(n.value);
    else if (n.kind === 'string') out.push(n.value);
  }
  return out;
}

/** Append missing entries to the sets and raise mijing floors. Additive only. */
export function applyUnlock(doc: OdinDocument, opts: import('../worker/protocol').UnlockOp):
  { chapters: number; levels: number; bossLevels: number } {
  const root = doc.root[0]!;
  if (!isContainer(root)) throw new Error('unexpected root node');

  const addInts = (name: string, values: number[]): number => {
    if (!values.length) return 0;
    const arr = setArray(root, name);
    if (!arr) throw new Error(`${name} is not present in this save yet — enter the game world once first`);
    const have = new Set(arr.children.map((c) => (c as AnyNode).kind === 'prim' ? Number((c as AnyNode & { value: unknown }).value) : NaN));
    let added = 0;
    for (const v of values) {
      if (have.has(v)) continue;
      arr.children.push({ kind: 'prim', prim: 'int', value: v });
      have.add(v);
      added++;
    }
    arr.length = BigInt(arr.children.length);
    return added;
  };
  const addStrings = (name: string, values: string[]): number => {
    if (!values.length) return 0;
    const arr = setArray(root, name);
    if (!arr) throw new Error(`${name} is not present in this save yet — enter the game world once first`);
    const have = new Set(arr.children.map((c) => (c as AnyNode).kind === 'string' ? (c as AnyNode & { value: string }).value : ''));
    let added = 0;
    for (const v of values) {
      if (have.has(v)) continue;
      arr.children.push({ kind: 'string', value: v, wide: true });
      have.add(v);
      added++;
    }
    arr.length = BigInt(arr.children.length);
    return added;
  };

  const added = {
    chapters: addInts('UnlockedChapterIds', opts.chapters),
    levels: addStrings('UnlockedLevelIds', opts.levels),
    bossLevels: addStrings('DefeatedBossLevelIds', opts.bossLevels),
  };

  if (opts.mijing) {
    const floors: [string, number | undefined][] = [
      ['mijingFloor_easy', opts.mijing.easy],
      ['mijingFloor_medium', opts.mijing.medium],
      ['mijingFloor_hard', opts.mijing.hard],
      ['mijingFloor_master', opts.mijing.master],
    ];
    for (const [name, floor] of floors) {
      if (floor === undefined) continue;
      const n = child(root, name);
      if (!n || n.kind !== 'prim') throw new Error(`${name} missing from save`);
      const cur = typeof n.value === 'bigint' ? Number(n.value) : n.value;
      if (floor > cur) n.value = Math.trunc(floor);
    }
    const um = child(root, 'UnlockedMijing');
    if (um && um.kind === 'bool') um.value = true;
  }
  return added;
}

export function buildSummary(doc: OdinDocument, fileName: string): SaveSummary {
  const root = doc.root[0]!;
  if (!isContainer(root)) throw new Error('unexpected root node');

  const idx = new Map<string, number>();
  root.children.forEach((c, i) => { if (c.name) idx.set(c.name, i); });
  const at = (name: string): { node: AnyNode; handle: string } | null => {
    const i = idx.get(name);
    return i === undefined ? null : { node: root.children[i] as AnyNode, handle: String(i) };
  };

  const gv = child(root, 'GameVersion');
  const player = at('PlayerData');
  const inv = at('InventoryData');
  const global = at('EmbeddedGlobalData');

  const equipment: ItemSummary[] = [];
  let money: Leaf | null = null;
  let pageCount = 0;
  const inventory: ItemSummary[] = [];
  const chest: ItemSummary[] = [];

  if (inv && isContainer(inv.node)) {
    pageCount = num(inv.node, 'PageCount');
    for (let i = 0; i < inv.node.children.length; i++) {
      const c = inv.node.children[i] as AnyNode;
      const handle = `${inv.handle}.${i}`;
      if (c.name === 'Money' && c.kind === 'prim') {
        money = { name: 'Money', handle, kind: 'long', value: leafValue(c) as number };
      } else if (c.name === 'Equipments' && isContainer(c)) {
        for (let a = 0; a < c.children.length; a++) {
          const arr = c.children[a] as AnyNode;
          if (arr.kind !== 'array') continue;
          for (let e = 0; e < arr.children.length; e++) {
            const wp = arr.children[e] as AnyNode;
            if (wp.kind !== 'ref' && wp.kind !== 'struct') continue;
            const h = `${handle}.${a}.${e}`;
            equipment.push(itemFromPayload(wp, h, 'weapon',
              { page: -1, gridX: -1, gridY: -1, slot: num(wp, 'CharType') }));
          }
        }
      } else if (c.name === 'InventoryItems') {
        inventory.push(...wrappersToItems(c, handle));
      }
    }
  }

  if (global && isContainer(global.node)) {
    for (let i = 0; i < global.node.children.length; i++) {
      const c = global.node.children[i] as AnyNode;
      if (c.name === 'GlobalChestData' && isContainer(c)) {
        const gh = `${global.handle}.${i}`;
        for (let j = 0; j < c.children.length; j++) {
          const cc = c.children[j] as AnyNode;
          if (isContainer(cc)) chest.push(...wrappersToItems(cc, `${gh}.${j}`));
        }
      }
    }
  }

  // TalentData: points + the All_Skill_Datas dictionary ($k skill name → $v
  // SkillSaveData with Level_Base).
  const talent = at('TalentData');
  let talentPoints: import('../worker/protocol').Leaf[] = [];
  const talents: import('../worker/protocol').TalentSkill[] = [];
  if (talent && isContainer(talent.node)) {
    talentPoints = shallowLeaves(talent.node, talent.handle);
    for (let i = 0; i < talent.node.children.length; i++) {
      const c = talent.node.children[i] as AnyNode;
      if (c.name !== 'All_Skill_Datas' || !isContainer(c)) continue;
      for (let a = 0; a < c.children.length; a++) {
        const arr = c.children[a] as AnyNode;
        if (arr.kind !== 'array') continue;
        for (let p = 0; p < arr.children.length; p++) {
          const pair = arr.children[p] as AnyNode;
          if (!isContainer(pair)) continue;
          const k = child(pair, '$k');
          if (!k || k.kind !== 'string') continue;
          const vi = pair.children.findIndex((n) => (n as AnyNode).name === '$v');
          const v = vi >= 0 ? (pair.children[vi] as AnyNode) : undefined;
          if (!v || !isContainer(v)) continue;
          let entry: import('../worker/protocol').TalentSkill | null = null;
          let selected: number | undefined;
          for (let l = 0; l < v.children.length; l++) {
            const leaf = v.children[l] as AnyNode;
            if (leaf.kind !== 'prim') continue;
            const val = typeof leaf.value === 'bigint' ? Number(leaf.value) : leaf.value;
            if (leaf.name === 'Level_Base') {
              entry = { name: k.value, level: val, handle: `${talent.handle}.${i}.${a}.${p}.${vi}.${l}` };
            } else if (leaf.name === 'SelectedIndex') {
              selected = val;
            }
          }
          if (entry) {
            if (selected !== undefined) entry.selected = selected;
            talents.push(entry);
          }
        }
      }
    }
  }

  const umNode = child(root, 'UnlockedMijing');
  return {
    fileName,
    gameVersion: gv?.kind === 'string' ? gv.value : '',
    playTime: num(root, 'PlayTimeSeconds'),
    player: player ? shallowLeaves(player.node, player.handle) : [],
    money,
    pageCount,
    equipment,
    inventory,
    chest,
    talentPoints,
    talents,
    unlockedChapters: setValues(root, 'UnlockedChapterIds').map(Number),
    unlockedLevels: setValues(root, 'UnlockedLevelIds').map(String),
    defeatedBossLevels: setValues(root, 'DefeatedBossLevelIds').map(String),
    mijing: {
      unlocked: umNode?.kind === 'bool' ? umNode.value : false,
      easy: num(root, 'mijingFloor_easy'),
      medium: num(root, 'mijingFloor_medium'),
      hard: num(root, 'mijingFloor_hard'),
      master: num(root, 'mijingFloor_master'),
    },
  };
}

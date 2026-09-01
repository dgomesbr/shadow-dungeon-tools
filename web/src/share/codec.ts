// Compact build-share codec. A build (character header + 10 equipped items
// with rolled affixes + invested talents) is binary-packed (varints, skill
// names as skills-table row indexes, affix values quantized to 1/100),
// deflate-compressed and base64url-encoded into the URL hash fragment.
// Result: a link that fits comfortably in a Discord non-Nitro message
// (2000 chars) instead of the multi-KB JSON blobs other tools produce.
import type { ItemSummary, Leaf, Rec, SaveSummary, TalentSkill } from '../worker/protocol';
import { ELEMENT_NAMES, type Catalog } from '../data/catalog';

const VERSION = 1;

export interface ShareItem {
  slot: number;
  globalId: number;
  quality: number;
  charType: number;
  /** Rolled elemental values, indexed by EL 0-5. */
  elements: number[];
  /** [Index, EL, number] triples. */
  main: [number, number, number][];
  dot: [number, number, number][];
  wpsk: { name: string; points: number }[];
  aocaoTypes: number[];
  setIndex: number;
}

export interface ShareBuild {
  name: string;
  level: number;
  dfLevel: number;
  pBase: number;
  pUsed: number;
  pUsedDF: number;
  equipment: ShareItem[];
  talents: { name: string; points: number }[];
  /** Set by unpack when the skills table changed since the link was made —
   *  skill names may be wrong. */
  dataDrift?: boolean;
}

// ---- byte-level primitives ---------------------------------------------------

class Writer {
  private bytes: number[] = [];
  u8(v: number): void { this.bytes.push(v & 0xff); }
  u16(v: number): void { this.u8(v); this.u8(v >>> 8); }
  varint(v: number): void {
    v = Math.max(0, Math.round(v)) >>> 0;
    while (v > 0x7f) { this.u8((v & 0x7f) | 0x80); v >>>= 7; }
    this.u8(v);
  }
  zig(v: number): void {
    v = Math.round(v) | 0;
    this.varint(((v << 1) ^ (v >> 31)) >>> 0);
  }
  str(s: string): void {
    const b = new TextEncoder().encode(s);
    this.varint(b.length);
    for (const x of b) this.bytes.push(x);
  }
  out(): Uint8Array { return new Uint8Array(this.bytes); }
}

class Reader {
  pos = 0;
  constructor(private bytes: Uint8Array) {}
  u8(): number {
    if (this.pos >= this.bytes.length) throw new Error('share payload truncated');
    return this.bytes[this.pos++]!;
  }
  u16(): number { return this.u8() | (this.u8() << 8); }
  varint(): number {
    let v = 0, shift = 0, b;
    do { b = this.u8(); v |= (b & 0x7f) << shift; shift += 7; } while (b & 0x80);
    return v >>> 0;
  }
  zig(): number {
    const v = this.varint();
    return (v >>> 1) ^ -(v & 1);
  }
  str(): string {
    const len = this.varint();
    const s = this.bytes.subarray(this.pos, this.pos + len);
    this.pos += len;
    return new TextDecoder().decode(s);
  }
}

// ---- skills-table row mapping -------------------------------------------------

function strHash(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
  return h >>> 0;
}

/** Guards against links breaking silently when data regenerates: if the
 *  skills table changed shape, row indexes may point at different skills. */
function skillsChecksum(cat: Catalog): number {
  const nameCol = cat.skills.col('IndexName');
  const first = String(cat.skills.rows[0]?.[nameCol] ?? '');
  const last = String(cat.skills.rows[cat.skills.length - 1]?.[nameCol] ?? '');
  return (cat.skills.length * 31 + strHash(first) * 7 + strHash(last)) & 0xffff;
}

function skillName(cat: Catalog, row: number): string {
  const r = cat.skills.rows[row];
  return r ? String(r[cat.skills.col('IndexName')]) : `skill#${row}`;
}

// ---- pack / unpack --------------------------------------------------------------

const Q = 100; // affix/element values quantized to 1/100 — invisible after display rounding

export function pack(b: ShareBuild, cat: Catalog): Uint8Array {
  const w = new Writer();
  w.u8(VERSION);
  w.u16(skillsChecksum(cat));
  w.str(b.name.slice(0, 24));
  w.varint(b.level); w.varint(b.dfLevel);
  w.varint(b.pBase); w.varint(b.pUsed); w.varint(b.pUsedDF);

  w.u8(b.equipment.length);
  for (const it of b.equipment) {
    w.u8(it.slot); w.varint(it.globalId); w.u8(it.quality); w.u8(it.charType);
    let mask = 0;
    for (let i = 0; i < 6; i++) if (it.elements[i]) mask |= 1 << i;
    w.u8(mask);
    for (let i = 0; i < 6; i++) if (it.elements[i]) w.zig(it.elements[i]! * Q);
    w.u8(it.main.length);
    for (const [idx, el, n] of it.main) { w.varint(idx); w.u8(el); w.zig(n * Q); }
    w.u8(it.dot.length);
    for (const [idx, el, n] of it.dot) { w.varint(idx); w.u8(el); w.zig(n * Q); }
    w.u8(it.wpsk.length);
    for (const sk of it.wpsk) {
      const row = cat.skillByIndexName.get(sk.name);
      if (row === undefined) { w.varint(0); w.str(sk.name); } else { w.varint(row + 1); }
      w.zig(sk.points);
    }
    w.u8(it.aocaoTypes.length);
    for (const t of it.aocaoTypes) w.u8(t);
    w.varint(it.setIndex);
  }

  // Talents: table rows delta-encoded (sorted), unknown names as strings.
  const known: [number, number][] = [];
  const extra: { name: string; points: number }[] = [];
  for (const t of b.talents) {
    const row = cat.skillByIndexName.get(t.name);
    if (row === undefined) extra.push(t);
    else known.push([row as number, t.points]);
  }
  known.sort((a, b2) => a[0] - b2[0]);
  w.varint(known.length);
  let prev = 0;
  for (const [row, pts] of known) { w.varint(row - prev); w.varint(pts); prev = row; }
  w.varint(extra.length);
  for (const t of extra) { w.str(t.name); w.varint(t.points); }

  return w.out();
}

export function unpack(bytes: Uint8Array, cat: Catalog): ShareBuild {
  const r = new Reader(bytes);
  const version = r.u8();
  if (version !== VERSION) throw new Error(`unsupported share version ${version}`);
  const dataDrift = r.u16() !== skillsChecksum(cat);
  const name = r.str();
  const level = r.varint(), dfLevel = r.varint();
  const pBase = r.varint(), pUsed = r.varint(), pUsedDF = r.varint();

  const equipment: ShareItem[] = [];
  const equipCount = r.u8();
  for (let e = 0; e < equipCount; e++) {
    const slot = r.u8(), globalId = r.varint(), quality = r.u8(), charType = r.u8();
    const mask = r.u8();
    const elements = Array.from({ length: 6 }, (_, i) => (mask & (1 << i)) ? r.zig() / Q : 0);
    const readTriples = (): [number, number, number][] => {
      const n = r.u8();
      return Array.from({ length: n }, () => [r.varint(), r.u8(), r.zig() / Q]);
    };
    const main = readTriples();
    const dot = readTriples();
    const wpsk: ShareItem['wpsk'] = [];
    const wpskCount = r.u8();
    for (let i = 0; i < wpskCount; i++) {
      const rowPlus1 = r.varint();
      const nm = rowPlus1 === 0 ? r.str() : skillName(cat, rowPlus1 - 1);
      wpsk.push({ name: nm, points: r.zig() });
    }
    const aocaoTypes = Array.from({ length: r.u8() }, () => r.u8());
    const setIndex = r.varint();
    equipment.push({ slot, globalId, quality, charType, elements, main, dot, wpsk, aocaoTypes, setIndex });
  }

  const talents: ShareBuild['talents'] = [];
  const knownCount = r.varint();
  let row = 0;
  for (let i = 0; i < knownCount; i++) {
    row += r.varint();
    talents.push({ name: skillName(cat, row), points: r.varint() });
  }
  const extraCount = r.varint();
  for (let i = 0; i < extraCount; i++) talents.push({ name: r.str(), points: r.varint() });

  return { name, level, dfLevel, pBase, pUsed, pUsedDF, equipment, talents, dataDrift };
}

// ---- compression + base64url ----------------------------------------------------

async function pipe(data: Uint8Array, stream: CompressionStream | DecompressionStream): Promise<Uint8Array> {
  const s = new Blob([data as BlobPart]).stream().pipeThrough(stream as ReadableWritablePair<Uint8Array, Uint8Array>);
  return new Uint8Array(await new Response(s).arrayBuffer());
}

function toB64url(b: Uint8Array): string {
  let s = '';
  for (let i = 0; i < b.length; i++) s += String.fromCharCode(b[i]!);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromB64url(s: string): Uint8Array {
  const bin = atob(s.replace(/-/g, '+').replace(/_/g, '/'));
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

// 'deflate' (zlib-wrapped) rather than 'deflate-raw': +6 bytes, but supported
// by every CompressionStream implementation (deflate-raw is missing in some).
export async function encodeShare(b: ShareBuild, cat: Catalog): Promise<string> {
  return toB64url(await pipe(pack(b, cat), new CompressionStream('deflate')));
}

export async function decodeShare(payload: string, cat: Catalog): Promise<ShareBuild> {
  return unpack(await pipe(fromB64url(payload), new DecompressionStream('deflate')), cat);
}

// ---- adapters to/from the editor's SaveSummary shapes -----------------------------

function leafNum(leaves: Leaf[], name: string): number {
  return Number(leaves.find((l) => l.name === name)?.value) || 0;
}

export function buildFromSummary(s: SaveSummary): ShareBuild {
  const p = new Map(s.player.map((l) => [l.name, l.value]));
  const tp = new Map(s.talentPoints.map((l) => [l.name, l.value]));
  return {
    name: String(p.get('PlayerName') ?? ''),
    level: Number(p.get('Level')) || 0,
    dfLevel: Number(p.get('DFLevel')) || 0,
    pBase: Number(tp.get('P_Base')) || 0,
    pUsed: Number(tp.get('P_Used')) || 0,
    pUsedDF: Number(tp.get('P_Used_DF')) || 0,
    equipment: s.equipment.map((it) => ({
      slot: it.slot,
      globalId: it.globalId,
      quality: it.quality,
      charType: it.charType,
      elements: ELEMENT_NAMES.map((n) => leafNum(it.leaves, n)),
      main: (it.main ?? []).map((m) => [Number(m['Index']), Number(m['EL']) || 0, Number(m['number']) || 0] as [number, number, number]),
      dot: (it.dot ?? []).map((m) => [Number(m['Index']), Number(m['EL']) || 0, Number(m['number']) || 0] as [number, number, number]),
      // Empty socket slots are stored as IndexName "0" — drop them from shares.
      wpsk: (it.wpsk ?? []).filter((w) => w['IndexName'] && String(w['IndexName']) !== '0')
        .map((w) => ({ name: String(w['IndexName']), points: Number(w['Number']) || 0 })),
      aocaoTypes: (it.aocao ?? []).map((a) => Number(a['Type']) || 0),
      setIndex: leafNum(it.leaves, 'Set_Index'),
    })),
    talents: s.talents.filter((t) => t.level > 0).map((t) => ({ name: t.name, points: t.level })),
  };
}

/** Rebuild a render-ready SaveSummary-lite for the shared-build viewer. */
export function summaryFromBuild(b: ShareBuild): SaveSummary {
  const leaf = (name: string, value: number | string, kind: Leaf['kind'] = 'int'): Leaf =>
    ({ name, handle: '', kind, value });
  const equipment: ItemSummary[] = b.equipment.map((it) => {
    const leaves: Leaf[] = ELEMENT_NAMES.map((n, i) => leaf(n, it.elements[i] ?? 0, 'float'));
    leaves.push(leaf('Set_Index', it.setIndex));
    const triple = (t: [number, number, number]): Rec => ({ Index: t[0], EL: t[1], number: t[2] });
    return {
      handle: `share-${it.slot}`,
      kind: 'weapon',
      page: -1, gridX: -1, gridY: -1,
      slot: it.slot,
      globalId: it.globalId,
      quality: it.quality,
      charType: it.charType,
      plType: 0, stack: 0, index: 0,
      leaves,
      main: it.main.map(triple),
      dot: it.dot.map(triple),
      wpsk: it.wpsk.map((w) => ({ IndexName: w.name, Number: w.points })),
      aocao: it.aocaoTypes.map((t) => ({ Type: t })),
      spc: [],
    };
  });
  const talents: TalentSkill[] = b.talents.map((t) => ({ name: t.name, level: t.points, handle: '' }));
  return {
    fileName: 'shared build',
    gameVersion: '',
    playTime: 0,
    player: [
      leaf('PlayerName', b.name, 'string'),
      leaf('Level', b.level),
      leaf('DFLevel', b.dfLevel),
    ],
    money: null,
    pageCount: 0,
    equipment,
    inventory: [],
    chest: [],
    talentPoints: [
      leaf('P_Base', b.pBase), leaf('P_Used', b.pUsed), leaf('P_Used_DF', b.pUsedDF),
    ],
    talents,
  };
}

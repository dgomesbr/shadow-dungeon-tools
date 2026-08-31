// Single-pass reader for OdinSerializer binary streams (little-endian).
// Produces the lossless tree in tree.ts. Throws on malformed input — the app
// treats any throw as "not a valid Shadow Dungeon save".
import { E } from './format';
import type { AnyNode, OdinDocument, PrimKind, TypeRef } from './tree';

const wideDecoder = new TextDecoder('utf-16le');
const narrowDecoder = new TextDecoder('latin1');

class Cursor {
  pos = 0;
  readonly view: DataView;
  constructor(readonly bytes: Uint8Array) {
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }
  u8(): number {
    if (this.pos >= this.bytes.length) throw new Error(`EOF at ${this.pos}`);
    return this.bytes[this.pos++]!;
  }
  peek(): number {
    if (this.pos >= this.bytes.length) return E.EndOfStream;
    return this.bytes[this.pos]!;
  }
  i32(): number { const v = this.view.getInt32(this.pos, true); this.pos += 4; return v; }
  i64(): bigint { const v = this.view.getBigInt64(this.pos, true); this.pos += 8; return v; }
  u64(): bigint { const v = this.view.getBigUint64(this.pos, true); this.pos += 8; return v; }
  f32(): number { const v = this.view.getFloat32(this.pos, true); this.pos += 4; return v; }
  f64(): number { const v = this.view.getFloat64(this.pos, true); this.pos += 8; return v; }
  raw(n: number): Uint8Array {
    if (this.pos + n > this.bytes.length) throw new Error(`EOF reading ${n} bytes at ${this.pos}`);
    const s = this.bytes.slice(this.pos, this.pos + n);
    this.pos += n;
    return s;
  }
  /** [marker u8][charCount i32][chars] — marker 0: 1 byte/char, else UTF-16LE. */
  str(): { value: string; wide: boolean } {
    const wide = this.u8() !== 0;
    const chars = this.i32();
    if (chars < 0) throw new Error(`negative string length at ${this.pos - 4}`);
    const byteLen = wide ? chars * 2 : chars;
    if (this.pos + byteLen > this.bytes.length) throw new Error(`EOF in string at ${this.pos}`);
    let value: string;
    if (chars <= 48) {
      // Short strings (member names): manual decode beats TextDecoder overhead.
      const b = this.bytes;
      let p = this.pos;
      const codes = new Array<number>(chars);
      if (wide) for (let i = 0; i < chars; i++, p += 2) codes[i] = b[p]! | (b[p + 1]! << 8);
      else for (let i = 0; i < chars; i++, p++) codes[i] = b[p]!;
      value = String.fromCharCode(...codes);
    } else {
      const sub = this.bytes.subarray(this.pos, this.pos + byteLen);
      value = (wide ? wideDecoder : narrowDecoder).decode(sub);
    }
    this.pos += byteLen;
    return { value, wide };
  }
}

const PRIM_BY_ENTRY: Record<number, PrimKind> = {
  [E.NamedSByte]: 'sbyte', [E.UnnamedSByte]: 'sbyte',
  [E.NamedByte]: 'byte', [E.UnnamedByte]: 'byte',
  [E.NamedShort]: 'short', [E.UnnamedShort]: 'short',
  [E.NamedUShort]: 'ushort', [E.UnnamedUShort]: 'ushort',
  [E.NamedInt]: 'int', [E.UnnamedInt]: 'int',
  [E.NamedUInt]: 'uint', [E.UnnamedUInt]: 'uint',
  [E.NamedLong]: 'long', [E.UnnamedLong]: 'long',
  [E.NamedULong]: 'ulong', [E.UnnamedULong]: 'ulong',
  [E.NamedFloat]: 'float', [E.UnnamedFloat]: 'float',
  [E.NamedDouble]: 'double', [E.UnnamedDouble]: 'double',
  [E.NamedChar]: 'char', [E.UnnamedChar]: 'char',
};

export function parseOdin(bytes: Uint8Array): OdinDocument {
  const c = new Cursor(bytes);
  const typeNames = new Map<number, { name: string; wide: boolean }>();
  const knownTypeIds = new Set<number>();

  function readTypeEntry(): TypeRef | null {
    const b = c.u8();
    if (b === E.UnnamedNull) return null;
    if (b === E.TypeID) {
      const id = c.i32();
      if (!knownTypeIds.has(id)) throw new Error(`TypeID ${id} before its TypeName at ${c.pos}`);
      return { id };
    }
    if (b === E.TypeName) {
      const id = c.i32();
      const s = c.str();
      typeNames.set(id, { name: s.value, wide: s.wide });
      knownTypeIds.add(id);
      return { id };
    }
    throw new Error(`bad type entry byte ${b} at ${c.pos - 1}`);
  }

  function readChildren(end: E): AnyNode[] {
    const out: AnyNode[] = [];
    for (;;) {
      const b = c.peek();
      if (b === end) { c.pos++; return out; }
      if (b === E.EndOfStream) throw new Error(`unexpected end of stream inside node at ${c.pos}`);
      out.push(readEntry());
    }
  }

  function isNamed(entry: number): boolean {
    switch (entry) {
      case E.NamedStartOfReferenceNode:
      case E.NamedStartOfStructNode:
      case E.NamedInternalReference:
      case E.NamedExternalReferenceByIndex:
      case E.NamedExternalReferenceByGuid:
      case E.NamedExternalReferenceByString:
        return true;
    }
    // Named/unnamed primitive pairs alternate from NamedSByte(15) to UnnamedNull(46).
    return entry >= E.NamedSByte && entry <= E.UnnamedNull && (entry & 1) === 1;
  }

  function readEntry(): AnyNode {
    const entry = c.u8();
    const named = isNamed(entry);
    let name: string | undefined;
    let nameNarrow: true | undefined;
    if (named) {
      const s = c.str();
      name = s.value;
      if (!s.wide) nameNarrow = true;
    }

    switch (entry) {
      case E.NamedStartOfReferenceNode:
      case E.UnnamedStartOfReferenceNode: {
        const type = readTypeEntry();
        const refId = c.i32();
        const children = readChildren(E.EndOfNode);
        return { kind: 'ref', name, nameNarrow, type, refId, children };
      }
      case E.NamedStartOfStructNode:
      case E.UnnamedStartOfStructNode: {
        const type = readTypeEntry();
        const children = readChildren(E.EndOfNode);
        return { kind: 'struct', name, nameNarrow, type, children };
      }
      case E.StartOfArray: {
        const length = c.i64();
        const children = readChildren(E.EndOfArray);
        return { kind: 'array', name, nameNarrow, length, children };
      }
      case E.PrimitiveArray: {
        const count = c.i32();
        const elemSize = c.i32();
        if (count < 0 || elemSize < 0) throw new Error(`bad primitive array at ${c.pos - 8}`);
        return { kind: 'primArray', name, nameNarrow, count, elemSize, data: c.raw(count * elemSize) };
      }
      case E.NamedInternalReference:
      case E.UnnamedInternalReference:
        return { kind: 'iref', name, nameNarrow, refId: c.i32() };
      case E.NamedExternalReferenceByIndex:
      case E.UnnamedExternalReferenceByIndex:
        return { kind: 'extref', name, nameNarrow, refKind: 'index', value: c.i32() };
      case E.NamedExternalReferenceByGuid:
      case E.UnnamedExternalReferenceByGuid:
        return { kind: 'extref', name, nameNarrow, refKind: 'guid', value: c.raw(16) };
      case E.NamedExternalReferenceByString:
      case E.UnnamedExternalReferenceByString: {
        const s = c.str();
        return { kind: 'extref', name, nameNarrow, refKind: 'string', value: s.value, wide: s.wide };
      }
      case E.NamedSByte: case E.UnnamedSByte:
        return { kind: 'prim', name, nameNarrow, prim: 'sbyte', value: (c.u8() << 24) >> 24 };
      case E.NamedByte: case E.UnnamedByte:
        return { kind: 'prim', name, nameNarrow, prim: 'byte', value: c.u8() };
      case E.NamedShort: case E.UnnamedShort: {
        const v = c.view.getInt16(c.pos, true); c.pos += 2;
        return { kind: 'prim', name, nameNarrow, prim: 'short', value: v };
      }
      case E.NamedUShort: case E.UnnamedUShort: {
        const v = c.view.getUint16(c.pos, true); c.pos += 2;
        return { kind: 'prim', name, nameNarrow, prim: 'ushort', value: v };
      }
      case E.NamedInt: case E.UnnamedInt:
        return { kind: 'prim', name, nameNarrow, prim: 'int', value: c.i32() };
      case E.NamedUInt: case E.UnnamedUInt: {
        const v = c.view.getUint32(c.pos, true); c.pos += 4;
        return { kind: 'prim', name, nameNarrow, prim: 'uint', value: v };
      }
      case E.NamedLong: case E.UnnamedLong:
        return { kind: 'prim', name, nameNarrow, prim: 'long', value: c.i64() };
      case E.NamedULong: case E.UnnamedULong:
        return { kind: 'prim', name, nameNarrow, prim: 'ulong', value: c.u64() };
      case E.NamedFloat: case E.UnnamedFloat:
        return { kind: 'prim', name, nameNarrow, prim: 'float', value: c.f32() };
      case E.NamedDouble: case E.UnnamedDouble:
        return { kind: 'prim', name, nameNarrow, prim: 'double', value: c.f64() };
      case E.NamedChar: case E.UnnamedChar: {
        const v = c.view.getUint16(c.pos, true); c.pos += 2;
        return { kind: 'prim', name, nameNarrow, prim: 'char', value: v };
      }
      case E.NamedDecimal: case E.UnnamedDecimal:
        return { kind: 'prim16', name, nameNarrow, prim: 'decimal', data: c.raw(16) };
      case E.NamedGuid: case E.UnnamedGuid:
        return { kind: 'prim16', name, nameNarrow, prim: 'guid', data: c.raw(16) };
      case E.NamedString: case E.UnnamedString: {
        const s = c.str();
        return { kind: 'string', name, nameNarrow, value: s.value, wide: s.wide };
      }
      case E.NamedBoolean: case E.UnnamedBoolean:
        return { kind: 'bool', name, nameNarrow, value: c.u8() === 1 };
      case E.NamedNull: case E.UnnamedNull:
        return { kind: 'null', name, nameNarrow };
      default:
        throw new Error(`unknown entry byte ${entry} at offset ${c.pos - 1}`);
    }
  }

  const root: AnyNode[] = [];
  while (c.pos < bytes.length) {
    if (c.peek() === E.EndOfStream) { c.pos++; continue; }
    root.push(readEntry());
  }
  return { root, typeNames };
}

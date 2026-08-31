// Writer: re-emits an OdinDocument as OdinSerializer binary. Replays the
// original writer's type-caching behavior (TypeName at a type id's first
// appearance, TypeID afterwards), so an unmodified tree round-trips
// byte-identically.
import { E } from './format';
import type { AnyNode, OdinDocument } from './tree';

class Out {
  buf = new Uint8Array(1 << 20);
  view = new DataView(this.buf.buffer);
  pos = 0;

  ensure(n: number): void {
    if (this.pos + n <= this.buf.length) return;
    let cap = this.buf.length * 2;
    while (cap < this.pos + n) cap *= 2;
    const next = new Uint8Array(cap);
    next.set(this.buf.subarray(0, this.pos));
    this.buf = next;
    this.view = new DataView(next.buffer);
  }
  u8(v: number): void { this.ensure(1); this.buf[this.pos++] = v; }
  i32(v: number): void { this.ensure(4); this.view.setInt32(this.pos, v, true); this.pos += 4; }
  u32(v: number): void { this.ensure(4); this.view.setUint32(this.pos, v, true); this.pos += 4; }
  i16(v: number): void { this.ensure(2); this.view.setInt16(this.pos, v, true); this.pos += 2; }
  u16(v: number): void { this.ensure(2); this.view.setUint16(this.pos, v, true); this.pos += 2; }
  i64(v: bigint): void { this.ensure(8); this.view.setBigInt64(this.pos, v, true); this.pos += 8; }
  u64(v: bigint): void { this.ensure(8); this.view.setBigUint64(this.pos, v, true); this.pos += 8; }
  f32(v: number): void { this.ensure(4); this.view.setFloat32(this.pos, v, true); this.pos += 4; }
  f64(v: number): void { this.ensure(8); this.view.setFloat64(this.pos, v, true); this.pos += 8; }
  raw(b: Uint8Array): void { this.ensure(b.length); this.buf.set(b, this.pos); this.pos += b.length; }
  str(value: string, wide: boolean): void {
    this.u8(wide ? 1 : 0);
    this.i32(value.length);
    if (wide) {
      this.ensure(value.length * 2);
      for (let i = 0; i < value.length; i++) {
        this.view.setUint16(this.pos, value.charCodeAt(i), true);
        this.pos += 2;
      }
    } else {
      this.ensure(value.length);
      for (let i = 0; i < value.length; i++) this.buf[this.pos++] = value.charCodeAt(i) & 0xff;
    }
  }
  bytes(): Uint8Array {
    return this.buf.slice(0, this.pos);
  }
}

export function writeOdin(doc: OdinDocument): Uint8Array {
  const o = new Out();
  const emittedTypes = new Set<number>();

  function writeType(type: { id: number } | null): void {
    if (type === null) { o.u8(E.UnnamedNull); return; }
    if (emittedTypes.has(type.id)) {
      o.u8(E.TypeID);
      o.i32(type.id);
      return;
    }
    const t = doc.typeNames.get(type.id);
    if (!t) throw new Error(`no TypeName recorded for type id ${type.id}`);
    emittedTypes.add(type.id);
    o.u8(E.TypeName);
    o.i32(type.id);
    o.str(t.name, t.wide);
  }

  function entryByte(named: number, unnamed: number, n: AnyNode): void {
    if (n.name !== undefined) {
      o.u8(named);
      o.str(n.name, !n.nameNarrow);
    } else {
      o.u8(unnamed);
    }
  }

  function writeNode(n: AnyNode): void {
    switch (n.kind) {
      case 'ref':
        entryByte(E.NamedStartOfReferenceNode, E.UnnamedStartOfReferenceNode, n);
        writeType(n.type);
        o.i32(n.refId);
        for (const ch of n.children) writeNode(ch as AnyNode);
        o.u8(E.EndOfNode);
        return;
      case 'struct':
        entryByte(E.NamedStartOfStructNode, E.UnnamedStartOfStructNode, n);
        writeType(n.type);
        for (const ch of n.children) writeNode(ch as AnyNode);
        o.u8(E.EndOfNode);
        return;
      case 'array':
        o.u8(E.StartOfArray);
        o.i64(n.length);
        for (const ch of n.children) writeNode(ch as AnyNode);
        o.u8(E.EndOfArray);
        return;
      case 'primArray':
        o.u8(E.PrimitiveArray);
        o.i32(n.count);
        o.i32(n.elemSize);
        o.raw(n.data);
        return;
      case 'iref':
        entryByte(E.NamedInternalReference, E.UnnamedInternalReference, n);
        o.i32(n.refId);
        return;
      case 'extref':
        if (n.refKind === 'index') {
          entryByte(E.NamedExternalReferenceByIndex, E.UnnamedExternalReferenceByIndex, n);
          o.i32(n.value as number);
        } else if (n.refKind === 'guid') {
          entryByte(E.NamedExternalReferenceByGuid, E.UnnamedExternalReferenceByGuid, n);
          o.raw(n.value as Uint8Array);
        } else {
          entryByte(E.NamedExternalReferenceByString, E.UnnamedExternalReferenceByString, n);
          o.str(n.value as string, n.wide !== false);
        }
        return;
      case 'prim':
        switch (n.prim) {
          case 'sbyte': entryByte(E.NamedSByte, E.UnnamedSByte, n); o.u8((n.value as number) & 0xff); return;
          case 'byte': entryByte(E.NamedByte, E.UnnamedByte, n); o.u8(n.value as number); return;
          case 'short': entryByte(E.NamedShort, E.UnnamedShort, n); o.i16(n.value as number); return;
          case 'ushort': entryByte(E.NamedUShort, E.UnnamedUShort, n); o.u16(n.value as number); return;
          case 'int': entryByte(E.NamedInt, E.UnnamedInt, n); o.i32(n.value as number); return;
          case 'uint': entryByte(E.NamedUInt, E.UnnamedUInt, n); o.u32(n.value as number); return;
          case 'long': entryByte(E.NamedLong, E.UnnamedLong, n); o.i64(BigInt(n.value)); return;
          case 'ulong': entryByte(E.NamedULong, E.UnnamedULong, n); o.u64(BigInt(n.value)); return;
          case 'float': entryByte(E.NamedFloat, E.UnnamedFloat, n); o.f32(n.value as number); return;
          case 'double': entryByte(E.NamedDouble, E.UnnamedDouble, n); o.f64(n.value as number); return;
          case 'char': entryByte(E.NamedChar, E.UnnamedChar, n); o.u16(n.value as number); return;
        }
        return;
      case 'prim16':
        if (n.prim === 'decimal') entryByte(E.NamedDecimal, E.UnnamedDecimal, n);
        else entryByte(E.NamedGuid, E.UnnamedGuid, n);
        o.raw(n.data);
        return;
      case 'string':
        entryByte(E.NamedString, E.UnnamedString, n);
        o.str(n.value, n.wide);
        return;
      case 'bool':
        entryByte(E.NamedBoolean, E.UnnamedBoolean, n);
        o.u8(n.value ? 1 : 0);
        return;
      case 'null':
        entryByte(E.NamedNull, E.UnnamedNull, n);
        return;
    }
  }

  for (const n of doc.root) writeNode(n);
  return o.bytes();
}

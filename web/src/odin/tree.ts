// Document model for OdinSerializer binary saves. The reader produces this
// tree; the writer must re-emit it byte-identically when unmodified.
// Node kinds mirror the wire format 1:1 so no information is lost.

export type OdinNode =
  | RefNode
  | StructNode
  | ArrayNode
  | PrimitiveArrayNode
  | PrimitiveNode
  | StringNode
  | NullNode
  | InternalRefNode
  | ExternalRefNode
  | BoolNode;

export interface TypeRef {
  /** Numeric id assigned at first appearance in the stream. */
  id: number;
  /** Full type name; undefined when the stream referenced a cached id only. */
  name?: string;
}

export interface BaseNode {
  /** Member name; undefined for unnamed entries (array elements, root). */
  name?: string;
}

export interface RefNode extends BaseNode {
  kind: 'ref';
  type: TypeRef | null;
  /** Reference id other nodes can point at via InternalRefNode. */
  refId: number;
  children: OdinNode[];
}

export interface StructNode extends BaseNode {
  kind: 'struct';
  type: TypeRef | null;
  children: OdinNode[];
}

export interface ArrayNode extends BaseNode {
  kind: 'array';
  length: number;
  children: OdinNode[];
}

export interface PrimitiveArrayNode extends BaseNode {
  kind: 'primArray';
  elementType: PrimKind;
  /** Raw little-endian payload, untouched unless edited. */
  data: Uint8Array;
  length: number;
}

export type PrimKind =
  | 'sbyte' | 'byte' | 'short' | 'ushort' | 'int' | 'uint'
  | 'long' | 'ulong' | 'float' | 'double' | 'decimal' | 'char' | 'guid';

export interface PrimitiveNode extends BaseNode {
  kind: 'prim';
  prim: PrimKind;
  /** number for 32-bit-safe values, bigint for long/ulong, string for decimal/guid. */
  value: number | bigint | string;
}

export interface BoolNode extends BaseNode {
  kind: 'bool';
  value: boolean;
}

export interface StringNode extends BaseNode {
  kind: 'string';
  value: string;
  /** true when the stream stored 16-bit chars; preserved for round-trip. */
  wide: boolean;
}

export interface NullNode extends BaseNode {
  kind: 'null';
}

export interface InternalRefNode extends BaseNode {
  kind: 'iref';
  refId: number;
}

export interface ExternalRefNode extends BaseNode {
  kind: 'extref';
  refKind: 'index' | 'guid' | 'string';
  value: number | string;
}

export interface OdinDocument {
  root: OdinNode[];
  /** Types in first-appearance order; writer re-emits names at same points. */
  types: TypeRef[];
}

// ---- traversal helpers -----------------------------------------------------

export function isContainer(n: OdinNode): n is RefNode | StructNode | ArrayNode {
  return n.kind === 'ref' || n.kind === 'struct' || n.kind === 'array';
}

/** Find first direct child by member name. */
export function child(n: OdinNode, name: string): OdinNode | undefined {
  if (!isContainer(n)) return undefined;
  for (const c of n.children) if (c.name === name) return c;
  return undefined;
}

/** Resolve a path like ["SaveData", "InventoryData", "InventoryItems"]. */
export function path(root: OdinNode, ...names: string[]): OdinNode | undefined {
  let cur: OdinNode | undefined = root;
  for (const nm of names) {
    if (!cur) return undefined;
    cur = child(cur, nm);
  }
  return cur;
}

export function* walk(n: OdinNode): Generator<OdinNode> {
  yield n;
  if (isContainer(n)) for (const c of n.children) yield* walk(c);
}

/** Highest refId in the document, for allocating fresh ids when cloning. */
export function maxRefId(doc: OdinDocument): number {
  let max = 0;
  for (const r of doc.root) {
    for (const n of walk(r)) {
      if (n.kind === 'ref' && n.refId > max) max = n.refId;
      if (n.kind === 'iref' && n.refId > max) max = n.refId;
    }
  }
  return max;
}

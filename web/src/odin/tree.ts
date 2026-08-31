// Lossless document model for OdinSerializer binary saves (FinkFramework fork).
// The reader produces this tree; the writer re-emits it byte-identically when
// unmodified. Node kinds mirror the wire format 1:1.

export type OdinNode =
  | RefNode
  | StructNode
  | ArrayNode
  | PrimitiveArrayNode
  | PrimitiveNode
  | Primitive16Node
  | StringNode
  | BoolNode
  | NullNode
  | InternalRefNode
  | ExternalRefNode;

export interface TypeRef {
  /** Id assigned at the type's first appearance in the stream (types.Count order). */
  id: number;
}

export interface BaseNode {
  /** Member name; undefined for unnamed entries (array elements, root). */
  name?: string;
  /** Set only when the stream stored the NAME as 8-bit chars (never seen in practice). */
  nameNarrow?: true;
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
  /** int64 element count from the stream. Writer emits children.length-derived
   *  value only if `length` is untouched; keep in sync when editing. */
  length: bigint;
  children: OdinNode[];
}

export interface PrimitiveArrayNode extends BaseNode {
  kind: 'primArray';
  count: number;
  elemSize: number;
  /** Raw little-endian payload (count * elemSize bytes). */
  data: Uint8Array;
}

export type PrimKind =
  | 'sbyte' | 'byte' | 'short' | 'ushort' | 'int' | 'uint'
  | 'long' | 'ulong' | 'float' | 'double' | 'char';

export interface PrimitiveNode extends BaseNode {
  kind: 'prim';
  prim: PrimKind;
  /** number for ≤32-bit and floats, bigint for long/ulong, number (code unit) for char. */
  value: number | bigint;
}

/** decimal / guid — kept as opaque 16-byte payloads (never edited). */
export interface StringNode extends BaseNode {
  kind: 'string';
  value: string;
  /** false when stored as 8-bit chars; game default is wide (UTF-16LE). */
  wide: boolean;
}

export interface BoolNode extends BaseNode {
  kind: 'bool';
  value: boolean;
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
  /** int32 for index, 16 raw bytes for guid, string for string refs. */
  value: number | Uint8Array | string;
  wide?: boolean;
}

/** Opaque 16-byte primitives (decimal, guid) ride as PrimitiveNode16. */
export interface Primitive16Node extends BaseNode {
  kind: 'prim16';
  prim: 'decimal' | 'guid';
  data: Uint8Array;
}

export type AnyNode = OdinNode;

export interface OdinDocument {
  root: AnyNode[];
  /** Type id → { name, wide } captured at first appearance (TypeName entries). */
  typeNames: Map<number, { name: string; wide: boolean }>;
}

// ---- traversal helpers -----------------------------------------------------

export function isContainer(n: AnyNode): n is RefNode | StructNode | ArrayNode {
  return n.kind === 'ref' || n.kind === 'struct' || n.kind === 'array';
}

/** Find first direct child by member name. */
export function child(n: AnyNode, name: string): AnyNode | undefined {
  if (!isContainer(n)) return undefined;
  for (const c of n.children) if (c.name === name) return c;
  return undefined;
}

/** Resolve a path of member names. */
export function path(root: AnyNode, ...names: string[]): AnyNode | undefined {
  let cur: AnyNode | undefined = root;
  for (const nm of names) {
    if (!cur) return undefined;
    cur = child(cur, nm);
  }
  return cur;
}

export function* walk(n: AnyNode): Generator<AnyNode> {
  yield n;
  if (isContainer(n)) for (const c of n.children) yield* walk(c as AnyNode);
}

/** Highest reference id in the document, for allocating fresh ids when cloning. */
export function maxRefId(doc: OdinDocument): number {
  let max = 0;
  for (const r of doc.root) {
    for (const n of walk(r)) {
      if ((n.kind === 'ref' || n.kind === 'iref') && n.refId > max) max = n.refId;
    }
  }
  return max;
}

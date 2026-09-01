# Shadow Dungeon `.sav` Binary Wire Format

**Status: verified specification.** Every claim in this document was checked against the
decompiled serializer actually shipped with the game
(`FinkFramework.Odin.OdinSerializer.dll`, a namespace-renamed fork of the open-source
Sirenix OdinSerializer), against the game's own save/load code
(`FinkFramework.Runtime` / `Assembly-CSharp`), and against the real bytes of
`slot_1.sav`. A reference parser built purely from this spec consumes the entire
2,234,290-byte `slot_1.sav` with zero slack bytes and balanced nesting.

Decompiled sources used (kept for reference):
`.tmp\odin-decomp\` (`BinaryDataWriter.cs`,
`BinaryDataReader.cs`, `BinaryEntryType.cs`, `ComplexTypeSerializer.cs`,
`ProperBitConverter.cs`, `SerializationContext.cs`, `DeserializationContext.cs`,
`DictionaryFormatter.cs`, `ListFormatter.cs`, `HashSetFormatter.cs`,
`ArrayFormatter.cs`, `EnumSerializer.cs`, `StringSerializer.cs`,
`ReflectionFormatter.cs`, `FormatterUtilities.cs`, `SerializationPolicies.cs`,
`DefaultSerializationBinder.cs`, `SerializationUtility.cs`, plus
`stock_BinaryDataReader.cs`/`stock_BinaryDataWriter.cs` downloaded from the upstream
TeamSirenix/odin-serializer repo for comparison).

---

## 1. File-level structure

### 1.1 Which files use this format

Save root: `%USERPROFILE%\AppData\LocalLow\OO Cat\Shadow Dungeon\`

| File | Root type serialized | Notes |
|---|---|---|
| `slot_<N>.sav` | `Data.SaveData.SaveData, Assembly-CSharp` | entry baseline (`BackupKind = 0` EntryBaseline) |
| `slot_<N>_auto.sav` | `Data.SaveData.SaveData, Assembly-CSharp` | `BackupKind = AutoBackup` |
| `slot_<N>_exit.sav` | `Data.SaveData.SaveData, Assembly-CSharp` | `BackupKind = ExitBackup` |
| `global.sav` | `Data.SaveData.GlobalSave.GlobalSaveData, Assembly-CSharp` | |
| `*.sav.bak`, `*.replacebak.*` | same | plain byte copies made by `AtomicSave` |
| `last_save_id.sav` | **NOT this format** | custom format with 4-byte magic `46 49 4E 4B` ("FINK"); do not parse as Odin |

### 1.2 Write / read path (confirmed in decompiled game code)

Write: `SaveManager.AtomicSave` → `DataUtil.Save` → (extension is `.sav`, not `.json`)
→ `DataUtil.SavePlain<T>`:

```csharp
byte[] bytes = SerializationUtility.SerializeValue(data, DataFormat.Binary);
File.WriteAllBytes(path, bytes);
```

Read: `DataUtil.LoadPlain<T>`:

```csharp
SerializationUtility.DeserializeValue<T>(File.ReadAllBytes(path), DataFormat.Binary);
```

Both use a **default** context: `SerializationPolicies.Unity` policy, the default
`DefaultSerializationBinder`, no external reference resolvers of any kind, and
`BinaryDataWriter.CompressStringsTo8BitWhenPossible = false` (nothing in the game or
the DLL ever sets it to true).

### 1.3 Overall layout

The file **is** the Odin binary payload of one root value. There is:

- **no wrapper header**, no magic, no version field,
- **no footer**, no checksum, no compression, no encryption,
- **no `EndOfStream` marker byte** — the file ends immediately after the root node's
  `EndOfNode` byte (`0x05` is the last byte of the file; verified).

Because the root is a non-null reference type, byte 0 of every `.sav` is always
`0x02` (`UnnamedStartOfReferenceNode`) followed by a `TypeName` type entry, which is
why every file starts `02 2F 00 00 00 00 01 <len:4> <UTF-16LE type name…>`.

A reader that runs out of data must behave as if it read an `EndOfStream` (49) entry
(that is exactly what `BinaryDataReader.PeekEntry` does at EOF).

---

## 2. Endianness and alignment

- **Everything multi-byte is little-endian.** Entry payloads, string length prefixes,
  type ids, node ids, array lengths, primitive-array headers and primitive-array
  element data: all LE (`ProperBitConverter` byte-swaps on big-endian machines so the
  wire is LE regardless of host).
- **There is no alignment or padding anywhere.** The stream is a byte-packed sequence
  of entries. Multi-byte values start at whatever offset the previous byte ended.
- Floats/doubles are IEEE-754 binary32/binary64, LE. NaN payloads are preserved
  bit-for-bit (raw memory copy), so a byte-identical writer must carry float bits, not
  decoded numbers (use `Float32Array`/`DataView` bit access in JS, never re-derive
  from a rounded decimal).

---

## 3. Lexical layer

### 3.1 String encoding (used for entry names, string values, type names, external string refs)

```
string := marker:u8  charCount:i32le  payload
  marker = 0x00 → 8-bit:    payload = charCount bytes; char i = byte i (Latin-1 / low byte of UTF-16 unit)
  marker = 0x01 → UTF-16LE: payload = charCount * 2 bytes
```

Crucial points:

- `charCount` counts **UTF-16 code units** (C# `string.Length`), **not** bytes and
  **not** Unicode code points. A surrogate pair counts as 2. In JS this is exactly
  `str.length`.
- The writer only uses the 8-bit form when `CompressStringsTo8BitWhenPossible` is
  true **and** every char is ≤ U+00FF. The game never enables it, therefore **every
  string in a real `.sav` uses marker `0x01` (UTF-16LE) — even pure-ASCII strings.**
  (Verified: all 91,643 strings in `slot_1.sav` have marker 1.)
- A reader must still accept marker `0x00` (decode each byte as one char, i.e.
  Latin-1) to be a complete implementation.
- Empty string = `01 00 00 00 00` (marker 1, count 0) as written by the game.

### 3.2 Type entries

A *type entry* is **not** a top-level entry. It appears in exactly one place: inside a
node-start entry (kinds 1/2/3/4), immediately after the optional name string. The
binary reader throws if bytes 47/48 are ever encountered as a top-level entry.

```
type-entry :=
    0x2E                                    ; UnnamedNull (46) → null type
  | 0x2F  id:i32le  name:string             ; TypeName (47) → declares a new type id
  | 0x30  id:i32le                          ; TypeID (48) → back-reference to a declared id
```

Rules (writer, `BinaryDataWriter.WriteType`):

- The writer keeps a `Dictionary<Type,int>` for the whole serialization session
  (one file). First time a type is written: `id = dict.Count` (so ids are
  **0-based, sequential, in order of first appearance in the stream**), emit
  `TypeName` (47) + id + name string. Every later occurrence of the same type emits
  `TypeID` (48) + id. A `null` type (used for dictionary key/value pair wrapper nodes)
  emits the single byte `0x2E`.
- Reader (`ReadTypeEntry`): on `TypeName`, `types.Add(id, boundType)` — a duplicate
  declaration of the same id **throws** (`Dictionary.Add`), so a writer must never
  redeclare an id. On `TypeID`, missing ids log an error and yield null type.
- The type table and the string encoding are the only "caching" mechanisms in the
  format. **There is no general string table** — names like `"GridX"` are re-encoded
  in full at every occurrence.

Type *name* format (produced by `DefaultSerializationBinder.BindToName`):

- Non-generic: `Namespace.TypeName, AssemblySimpleName`
  e.g. `Data.SaveData.SaveData, Assembly-CSharp`
- Arrays: element type name with `[]` before the comma, e.g. `WPDT_A[], Assembly-CSharp`
- Generic: CLR `Type.FullName` with each generic argument's **full** assembly name
  replaced by its **simple** name:
  - ``System.Collections.Generic.List`1[[Data.SaveData.ContainerItemSaveData, Assembly-CSharp]], mscorlib``
  - ``System.Collections.Generic.Dictionary`2[[System.Int32, mscorlib],[System.Int32, mscorlib]], mscorlib``
  - ``System.Collections.Generic.HashSet`1[[System.Int32, mscorlib]], System.Core``
- The game runs on **Mono**, so corelib is spelled `mscorlib` and ``HashSet`1`` lives in
  `System.Core`. Mono's internal string comparer appears as
  `System.Collections.Generic.InternalStringComparer, mscorlib`, and default value
  comparers as ``System.Collections.Generic.GenericEqualityComparer`1[[System.Int32, mscorlib]], mscorlib``.
  A byte-identical writer must reproduce these spellings **verbatim** (including the
  single space after every comma).

---

## 4. `BinaryEntryType` — the full entry-byte enum (verified against the fork DLL)

| Dec | Hex | Name | Dec | Hex | Name |
|---|---|---|---|---|---|
| 0 | 0x00 | Invalid | 26 | 0x1A | UnnamedUInt |
| 1 | 0x01 | NamedStartOfReferenceNode | 27 | 0x1B | NamedLong |
| 2 | 0x02 | UnnamedStartOfReferenceNode | 28 | 0x1C | UnnamedLong |
| 3 | 0x03 | NamedStartOfStructNode | 29 | 0x1D | NamedULong |
| 4 | 0x04 | UnnamedStartOfStructNode | 30 | 0x1E | UnnamedULong |
| 5 | 0x05 | EndOfNode | 31 | 0x1F | NamedFloat |
| 6 | 0x06 | StartOfArray | 32 | 0x20 | UnnamedFloat |
| 7 | 0x07 | EndOfArray | 33 | 0x21 | NamedDouble |
| 8 | 0x08 | PrimitiveArray | 34 | 0x22 | UnnamedDouble |
| 9 | 0x09 | NamedInternalReference | 35 | 0x23 | NamedDecimal |
| 10 | 0x0A | UnnamedInternalReference | 36 | 0x24 | UnnamedDecimal |
| 11 | 0x0B | NamedExternalReferenceByIndex | 37 | 0x25 | NamedChar |
| 12 | 0x0C | UnnamedExternalReferenceByIndex | 38 | 0x26 | UnnamedChar |
| 13 | 0x0D | NamedExternalReferenceByGuid | 39 | 0x27 | NamedString |
| 14 | 0x0E | UnnamedExternalReferenceByGuid | 40 | 0x28 | UnnamedString |
| 15 | 0x0F | NamedSByte | 41 | 0x29 | NamedGuid |
| 16 | 0x10 | UnnamedSByte | 42 | 0x2A | UnnamedGuid |
| 17 | 0x11 | NamedByte | 43 | 0x2B | NamedBoolean |
| 18 | 0x12 | UnnamedByte | 44 | 0x2C | UnnamedBoolean |
| 19 | 0x13 | NamedShort | 45 | 0x2D | NamedNull |
| 20 | 0x14 | UnnamedShort | 46 | 0x2E | UnnamedNull |
| 21 | 0x15 | NamedUShort | 47 | 0x2F | TypeName |
| 22 | 0x16 | UnnamedUShort | 48 | 0x30 | TypeID |
| 23 | 0x17 | NamedInt | 49 | 0x31 | EndOfStream |
| 24 | 0x18 | UnnamedInt | 50 | 0x32 | NamedExternalReferenceByString |
| 25 | 0x19 | NamedUInt | 51 | 0x33 | UnnamedExternalReferenceByString |

Named/unnamed pattern: for every value-carrying kind, `Named*` = `Unnamed*` − 1. A
*named* entry is the entry byte followed immediately by a **name string** (section
3.1), then the payload. An *unnamed* entry goes straight to the payload. Names are
member/field names ("GameVersion", "$k", "comparer", …); array elements and root
values are unnamed.

The reader also folds these into a coarser logical `EntryType` enum
(Invalid=0, String=1, Guid=2, Integer=3, FloatingPoint=4, Boolean=5, Null=6,
StartOfNode=7, EndOfNode=8, InternalReference=9, ExternalReferenceByIndex=10,
ExternalReferenceByGuid=11, StartOfArray=12, EndOfArray=13, PrimitiveArray=14,
EndOfStream=15, ExternalReferenceByString=16). All 8 integer widths → `Integer`;
Float/Double/Decimal → `FloatingPoint`; Char and String → `String`. This only matters
for reader tolerance (section 8.4), not for the wire.

---

## 5. Exact encoding of every entry kind

Notation: `[name]` = name string present only for the Named variant.

### 5.1 Nodes

```
reference-node := (0x01|0x02) [name] type-entry nodeId:i32le  content*  0x05
struct-node    := (0x03|0x04) [name] type-entry               content*  0x05
```

- `EndOfNode` (0x05) has no payload and closes the most recent open node of either kind.
- Reference nodes are used for **class instances** (including strings' containers,
  lists, dictionaries, arrays — anything heap-allocated); they carry an `i32`
  reference id (see section 8). Struct nodes are used for **value types**
  (`DateTime`, `Vector3`, dictionary key/value pair wrappers, …) and carry no id.
- The type entry is **always present**. For dictionary pair wrapper nodes it is the
  null type (`0x2E`). For everything else the game writes the **runtime** type of the
  value (polymorphism is expressed this way; there is no "omit type if it matches the
  declared type" optimization in this pipeline).

### 5.2 Arrays

```
array-block     := 0x06 length:i64le  element*  0x07
primitive-array := 0x08 elementCount:i32le bytesPerElement:i32le  raw-bytes[elementCount*bytesPerElement]
```

- `StartOfArray` (6) / `EndOfArray` (7): used by `List<T>`, `HashSet<T>`, `T[]` of
  non-primitive `T`, `Dictionary<K,V>` pair sequences, etc. Each element is written as
  an **unnamed** value entry. The length is written as **int64** even though counts are
  ints.
- `PrimitiveArray` (8): used only for `T[]` where `T` ∈ {char, sbyte, short, int,
  long, byte, ushort, uint, ulong, decimal, bool, float, double, Guid}. The element
  data is a raw little-endian memory image, densely packed:
  bool=1 byte (0/1), sbyte/byte=1, char/short/ushort=2, int/uint/float=4,
  long/ulong/double=8, decimal/Guid=16 (layouts per 5.4). `bytesPerElement` is always
  the table value above; a reader should trust the header fields and consume
  `elementCount*bytesPerElement` bytes.
- Note the asymmetry: a `List<int>` is an array-block of unnamed Int entries
  (5 bytes/elem), while an `int[]` is one PrimitiveArray (4 bytes/elem). Both are
  wrapped in the collection's reference node.

### 5.3 References

```
internal-ref        := (0x09|0x0A) [name] id:i32le
external-ref-index  := (0x0B|0x0C) [name] index:i32le
external-ref-guid   := (0x0D|0x0E) [name] guid:16 bytes (layout per 5.4)
external-ref-string := (0x32|0x33) [name] id:string (marker+len+payload per 3.1)
```

External references require resolvers on the context. The game registers **none**, so
**external reference entries never occur in `.sav` files** (verified: zero in
slot_1.sav). Implement parsing for completeness; never emit them.

### 5.4 Primitives

All payloads immediately follow the entry byte (+ name for Named variants).

| Kind (Named/Unnamed) | Payload | Encoding |
|---|---|---|
| Boolean 43/44 | 1 byte | `0x01` = true; **reader tests `byte == 1`**, any other value (incl. 2..255) reads as false. Writer emits only 0/1. |
| SByte 15/16 | 1 byte | two's complement |
| Byte 17/18 | 1 byte | unsigned |
| Short 19/20 | 2 bytes | i16 LE |
| UShort 21/22 | 2 bytes | u16 LE |
| Int 23/24 | 4 bytes | i32 LE |
| UInt 25/26 | 4 bytes | u32 LE |
| Long 27/28 | 8 bytes | i64 LE (JS: BigInt or careful Number handling — tick values exceed 2^53) |
| ULong 29/30 | 8 bytes | u64 LE |
| Float 31/32 | 4 bytes | IEEE-754 binary32 LE |
| Double 33/34 | 8 bytes | IEEE-754 binary64 LE |
| Char 37/38 | 2 bytes | one UTF-16 code unit, LE |
| String 39/40 | string | marker+len+payload per 3.1 |
| Guid 41/42 | 16 bytes | .NET `Guid` memory layout = `Guid.ToByteArray()` order: `a:i32le, b:i16le, c:i16le, d,e,f,g,h,i,j,k` — i.e. GUID `00112233-4455-6677-8899-aabbccddeeff` ⇒ bytes `33 22 11 00 55 44 77 66 88 99 AA BB CC DD EE FF` |
| Decimal 35/36 | 16 bytes | .NET `decimal` memory image, LE: bytes 0–3 = `flags` (i32 LE; bits 16–23 = scale 0–28, bit 31 = sign, all other bits zero), bytes 4–7 = `hi32`, bytes 8–11 = `lo32`, bytes 12–15 = `mid32`. Value = (hi·2^64 + mid·2^32 + lo) / 10^scale, negated if sign bit set. |
| Null 45/46 | none | |

### 5.5 EndOfStream (49)

No payload. The game's writer **never emits it** (`SerializeValue` just flushes after
the root value). The reader synthesizes it at EOF. Do not write it; do not require it.

---

## 6. Document grammar (how the entries compose)

```
file        := value                          ; exactly one root value, then EOF
value       := prim | null | ref-node | struct-node | internal-ref | external-ref
content of a node (between node-start and 0x05) is formatter-defined:

object (reflection/emitted formatter):
    one named value per serialized member, in member order (see 10.3)

List<T> / HashSet<T> / T[] (complex T):
    0x06 count:i64le  (unnamed value)*count  0x07

T[] (primitive T):
    one PrimitiveArray entry (0x08 ...)

Dictionary<K,V>:
    named value "comparer"      ; the comparer object (ref-node or internal-ref;
                                ;   written whenever dict.Comparer != null, i.e. always)
    0x06 count:i64le
        ( 0x04 0x2E             ; unnamed struct node, null type — one per pair
              named value "$k"  ; key
              named value "$v"  ; value
          0x05 )*count
    0x07

enum value:
    a ULong entry (named if a member, unnamed if an element). The numeric value is
    the enum's underlying value copied little-endian into 8 bytes and ZERO-extended
    (a negative int-backed enum value becomes 0x00000000FFFFxxxx, not sign-extended).

DateTime:
    struct node of type "System.DateTime, mscorlib" containing a single unnamed
    Long = DateTime.ToBinary() (not observed in current saves, but this is the code path)

string / primitive members: the corresponding primitive entry; null strings and null
    object members are Null entries (45 with the member name).
```

Nesting sanity: node/array starts and ends are strictly balanced; the reference parser
confirms final depth 0 exactly at EOF.

---

## 7. What actually occurs in a real save (slot_1.sav, 2,234,290 bytes)

Entry-byte histogram (top-level entries): `0x01`×4448, `0x02`×11170, `0x03`×1146,
`0x04`×460, `0x05`×17224, `0x06`/`0x07`×3398 each, `0x09`×1, `0x17`(NamedInt)×47881,
`0x18`×7, `0x1B`(NamedLong)×5, `0x1D`(NamedULong/enums)×32, `0x1F`(NamedFloat)×13815,
`0x27`(NamedString)×7522, `0x28`×193, `0x2B`(NamedBool)×6899, `0x2D`(NamedNull)×2135.
44 distinct types (TypeName), 16,720 TypeID back-references, 15,618 reference nodes,
exactly 1 internal reference (a shared dictionary comparer singleton), zero external
references, zero PrimitiveArray/Guid/Decimal/Char/Short/UInt entries, zero 8-bit
strings. Everything else in the spec must still be implemented for robustness, but
this is the working set.

---

## 8. Reference ids (`$id` / internal references)

### 8.1 Assignment (writer)

`SerializationContext` keeps an **identity** map `object → int`
(`ReferenceEqualityComparer`). When `ComplexTypeSerializer.WriteValue` handles a
non-null reference-type value:

1. If the object is **not** in the map: `id = map.Count` (0-based sequential), add it,
   and write a full reference node `(1|2) [name] type-entry id … 5`.
2. If it **is** in the map: write `(9|10) [name] id` — an internal reference — and
   **nothing else** (no type, no content).

Consequences (verified): node ids appear in the stream as 0,1,2,… in exact
depth-first pre-order of first encounter; the root object is always id 0. Ids are
per-file (the map is reset per serialization session).

### 8.2 Resolution (reader)

`DeserializationContext` keeps `int → object`. Formatters register the object under
the current node's id **immediately after allocating it, before parsing the node
content** (`BaseFormatter.Deserialize` → `RegisterReferenceID`; collection formatters
register right after constructing the collection). `ComplexTypeSerializer.ReadValue`
additionally re-registers after the node completes. An internal-reference entry is
resolved by lookup; a missing id yields null plus a warning.

**Implication for the JS reader:** register the placeholder object at node entry, not
node exit. With data produced by this writer, internal references always point
*backward* to an already-started node, but that node may still be **open** (an
ancestor) in the case of cyclic object graphs — early registration is what makes
cycles work. Forward references never occur on the wire.

### 8.3 Where sharing actually happens

In the current saves the only shared object is the per-instantiation default
`EqualityComparer` singleton used by dictionaries: the second
`Dictionary<int,int>` in the file emits
`09 [comparer] <id-of-first-comparer-node>` instead of a node. Any user data that
aliases the same list/object instance twice would do the same. **A byte-identical
writer must preserve exactly which occurrences were full nodes and which were
internal refs** — this cannot be reconstructed from plain JSON without identity
information.

### 8.4 Reader tolerance rules (from the decompiled reader — implement these to match game behavior)

- Numeric reads coerce: e.g. `ReadInt64` accepts any of the 8 integer entry kinds and
  widens; `ReadUInt64` likewise; floating reads accept Float/Double/Decimal. (For
  byte-identical round-trip the writer must still use the original width.)
- `ExitNode`/`ExitArray` skip any remaining entries until the matching end marker.
- Unknown member names are skipped (`SkipEntry` = skip name + payload, recursively
  skipping whole nodes/arrays).
- `EnterNode` on a non-node entry skips it and returns failure; same for `EnterArray`.

---

## 9. Worked example — first 0x24C bytes of `slot_1.sav`, byte-by-byte

(Snapshot with `GameVersion = "1.1.0"`; offsets/bytes verified programmatically.)

```
off      bytes                                  meaning
0x0000   02                                     UnnamedStartOfReferenceNode (root value)
0x0001   2F                                     type entry: TypeName (47)
0x0002   00 00 00 00                            type id = 0 (first type in file)
0x0006   01                                     string marker: UTF-16LE
0x0007   27 00 00 00                            39 chars (78 bytes)
0x000B   44 00 61 00 74 00 61 00 2E 00 …        "Data.SaveData.SaveData, Assembly-CSharp"
0x0059   00 00 00 00                            reference node id = 0 (root object)
─ root node content begins ─
0x005D   27                                     NamedString (39)
0x005E   01 0B 00 00 00                         name: UTF-16, 11 chars
0x0063   47 00 61 00 6D 00 65 00 …              "GameVersion"
0x0079   01 05 00 00 00                         value: UTF-16, 5 chars
0x007E   31 00 2E 00 31 00 2E 00 30 00          "1.1.0"
0x0088   1D                                     NamedULong (29)  ← enum SaveBackupKind
0x0089   01 0A 00 00 00                         name: 10 chars
0x008E   42 00 61 00 63 00 6B 00 …              "BackupKind"
0x00A2   00 00 00 00 00 00 00 00                u64 = 0 (EntryBaseline)
0x00AA   1B                                     NamedLong (27)
0x00AB   01 13 00 00 00                         name: 19 chars
0x00B0   53 00 61 00 76 00 65 00 …              "SaveCreatedUtcTicks"
0x00D6   24 CC 43 14 9F 07 DF 08                i64 LE = 639238051931081764  (.NET ticks)
0x00DE   1B                                     NamedLong
0x00DF   01 17 00 00 00                         name: 23 chars
0x00E4   53 00 65 00 73 00 73 00 …              "SessionBaselineUtcTicks"
0x0112   24 CC 43 14 9F 07 DF 08                i64 = 639238051931081764
0x011A   27                                     NamedString
0x011B   01 09 00 00 00                         name: 9 chars
0x0120   53 00 65 00 73 00 73 00 …              "SessionId"
0x0132   01 20 00 00 00                         value: 32 chars
0x0137   36 00 63 00 65 00 37 00 …              "6ce7da9f933445f88bd7b8c3059371d9"
0x0177   27                                     NamedString
0x0178   01 11 00 00 00                         name: 17 chars
0x017D   53 00 61 00 76 00 65 00 …              "SaveTransactionId"
0x019F   01 00 00 00 00                         value: UTF-16, 0 chars  → ""
0x01A4   01                                     NamedStartOfReferenceNode (nested object)
0x01A5   01 12 00 00 00                         name: 18 chars
0x01AA   45 00 6D 00 62 00 65 00 …              "EmbeddedGlobalData"
0x01CE   2F                                     TypeName (47) — second new type
0x01CF   01 00 00 00                            type id = 1
0x01D3   01 38 00 00 00                         56 chars
0x01D8   44 00 61 00 74 00 61 00 …              "Data.SaveData.GlobalSave.GlobalSaveData, Assembly-CSharp"
0x0248   01 00 00 00                            reference node id = 1
0x024C   17 …                                   NamedInt "LastWriterSlotId" = 1 … (etc.)
```

Later in the same file (illustrating the remaining constructs):

- A list: `01` + name `"ChestItems"` + `2F 03000000` +
  ``"System.Collections.Generic.List`1[[Data.SaveData.ContainerItemSaveData, Assembly-CSharp]], mscorlib"``
  + nodeId + `06 <count:i64>` + count × (`02` + TypeID/TypeName + nodeId + members… `05`) + `07` + `05`.
- A `HashSet<int>` ("UnlockedChapterIds"): ref node of the HashSet type, then
  `06 <count:i64>`, then count × (`18 <i32>` — *unnamed* Int entries), `07`, `05`.
- A `Dictionary<string, SkillSaveData>`: ref node, then
  `01 "comparer" 2F <id> "System.Collections.Generic.InternalStringComparer, mscorlib" <nodeId> 05`
  (an empty object node — the comparer has no serialized members), then
  `06 <count>`, then per pair `04 2E  27 "$k" <string>  <named "$v" value>  05`, then `07 05`.
- The file's single internal reference: a second dictionary of the same instantiation
  emits `09 "comparer" <i32 = node id of the first comparer>`.
- Last byte of the file: `05` (EndOfNode of the root). Nothing follows.

---

## 10. Fork vs. stock OdinSerializer; settings that affect bytes

### 10.1 The fork

`FinkFramework.Odin.OdinSerializer.dll` is the open-source
TeamSirenix/odin-serializer with the namespace renamed
(`OdinSerializer` → `FinkFramework.Odin.OdinSerializer`) plus cosmetic additions
(an `ILogger` hook; `ArchitectureInfo` logs from its static constructor). After a
method-by-method comparison of `BinaryDataWriter`/`BinaryDataReader` against upstream
master: **the wire format is bit-for-bit identical to stock Odin binary format**,
including the two "ByString" external-ref entries 50/51. There are no fork-specific
wire changes. Anything documented for stock Odin binary applies.

### 10.2 Inherited upstream bug (affects only error paths)

`SkipPeekedEntryContent` skips only **8** bytes for the 16-byte payload kinds
(Guid 41/42, Decimal 35/36, ExternalReferenceByGuid 13/14) — in the fork *and* in
upstream master. It never matters for well-formed files (skipping only happens for
unknown/mismatched entries), but if you ever implement "skip unknown member",
skipping the **correct** 16 bytes will diverge from what the game would do with a
malformed file. For well-formed data this is moot; recommended: skip 16 (correct),
note the divergence.

### 10.3 Settings in effect (and non-settings)

- `DataFormat.Binary` (enum value 0). The DLL also contains the Odin JSON and Nodes
  formats; never used for `.sav`.
- Default `SerializationContext`/`DeserializationContext` ⇒ policy
  `SerializationPolicies.Unity`, binder `DefaultSerializationBinder`,
  `AllowDeserializeInvalidData = false`, no external resolvers.
- The Unity policy decides **which members** are serialized (not how): serialize a
  field if public, or `[SerializeField]`, or `[OdinSerialize]`, or a compiler-generated
  backing field situation; skip `[NonSerialized]` unless `[OdinSerialize]`; properties
  only when they have both getter and setter (auto-property backing behavior);
  `allowNonSerializableTypes: true`.
- Member **order** on the wire = `FormatterUtilities.GetSerializableMembers`: recurse
  into the base class **first**, then the type's own members in
  `Type.GetMembers(DeclaredOnly|Instance|Public|NonPublic)` order (declaration order on
  Mono). Since the reader matches by name, a JS *reader* can ignore this; a
  byte-identical *writer* must reproduce the original order (easiest: preserve
  stream order from the file being edited).
- There is **no** `AlwaysFormatData` / `PolymorphicTypeSerialization` knob in
  OdinSerializer (those are options of other libraries). Odin binary always writes the
  runtime type on every node-start — effectively "always polymorphic".
- `CompressStringsTo8BitWhenPossible = false` (default; never changed) ⇒ all strings
  UTF-16LE.
- Mono specifics that leak into bytes: `mscorlib` / `System.Core` assembly names and
  `InternalStringComparer` (Mono-internal type) inside type-name strings. The
  companion SaveTool (`%USERPROFILE%\AppData\LocalLow\OO Cat\ShadowDungeonSaveTool\SaveTool\Program.cs`)
  runs on .NET 8 and needs a custom binder purely to translate
  `System.Private.CoreLib` ↔ `mscorlib` and
  ``GenericEqualityComparer`1[[System.String…]]`` ↔ `InternalStringComparer` so its
  output matches the game's bytes. A JS implementation treats type names as opaque
  strings and has no such problem — **never normalize or re-derive them**.

---

## 11. Writer requirements for byte-identical round-trip

A JS writer that decodes a `.sav` into a document model and re-encodes it must:

1. **Emit exactly one root value and stop.** No EndOfStream byte, no padding.
2. **Preserve entry order and entry kinds exactly.** The reader is tolerant
   (name-matching, numeric coercion) but the writer is deterministic; the only safe
   model for round-trip is: decode to an ordered tree that remembers, for every entry,
   its exact `BinaryEntryType`, name (or namelessness), and payload; re-encode by
   replaying. Do not sort members, do not renumber, do not re-type.
3. **Type table:** insertion-ordered map name→id. First occurrence of a type in
   stream order emits `TypeName(47) + id + name`; every later one emits
   `TypeID(48) + id`. Ids = 0,1,2,… by first appearance. Never redeclare an id (game
   reader throws). If your edit introduces a node whose type already appeared, use its
   existing id; a brand-new type gets `max+1` and a `TypeName` at its first use.
4. **Reference ids:** the game emits 0,1,2,… in depth-first order of node starts.
   The reader does not *require* contiguity — it stores whatever id the node carries —
   but byte-identity requires reproducing the original numbering, and internal-ref
   entries must carry the id of the node they alias. If you insert/remove reference
   nodes, either renumber everything the way the game would (sequential in stream
   order, refs updated), or keep original ids and give new nodes fresh unique ids —
   both load fine; only the former is byte-identical to what the game will write next.
5. **Shared-identity fidelity:** occurrences that were internal references (entry
   9/10) must stay internal references to the same target (notably every dictionary
   "comparer" after the first per instantiation). Expanding an internal ref into a
   full node (or collapsing a duplicate node into a ref) changes bytes and also
   changes deserialized object identity.
6. **Strings:** always marker `0x01` UTF-16LE; length = UTF-16 code-unit count
   (`str.length` in JS); empty string = `01 00 00 00 00`. Never emit marker 0.
7. **Type-name strings verbatim** — byte-preserve them from the input; do not
   regenerate.
8. **Floats:** preserve raw bits (round-trip via `DataView.getFloat32/setFloat32`
   is bit-exact; never go through decimal string formatting). Longs/ULongs via
   BigInt.
9. **Enums** are ULong entries with the value zero-extended to 8 bytes.
10. **Booleans** are exactly `00`/`01`.
11. **Array lengths** are i64; PrimitiveArray headers are the two i32 fields with the
    canonical per-type `bytesPerElement`.
12. **Dictionary pairs** are `04 2E … 05` wrapper nodes with named `"$k"`/`"$v"`
    children; the `"comparer"` entry precedes the array block.
13. Nothing is cleared mid-file: type table and reference table live for the whole
    document (they are reset per file by `PrepareNewSerializationSession`).
14. Validate by byte-diffing your writer's output against the original for an
    unmodified decode → encode pass (this is exactly what
    `SaveTool verify <input.sav>` proves is achievable — it reports
    "bytes IDENTICAL to original" using the game's own DLLs).

### Game-side load constraints (not wire format, but will reject valid streams)

The game additionally checks after deserialization: `SaveCreatedUtcTicks > 0`;
`PlayerData/TalentData/ActbarData/InventoryData/DialogData/UnlockedChapterIds/
UnlockedLevelIds/DefeatedBossLevelIds` non-null; `BackupKind` must agree with the
filename (`slot_N.sav` ⇒ EntryBaseline, `_auto` ⇒ AutoBackup, `_exit` ⇒ ExitBackup);
`Money ≥ 0`, `PageCount` 1..1000, `Level ≥ 1` (clamped). `global.sav` requires
`GlobalChestData` non-null.

---

## 12. Minimal JS reader pseudocode (normative summary)

```js
// state: pos, types = new Map(), refs = new Map()
function readString() {
  const wide = u8(); const n = i32();
  return wide ? utf16le(n * 2) : latin1(n);   // n = UTF-16 code units
}
function readTypeEntry() {              // only called inside node starts
  const b = u8();
  if (b === 0x2E) return null;
  if (b === 0x30) return types.get(i32());
  if (b === 0x2F) { const id = i32(); const name = readString();
                    if (types.has(id)) throw Error("dup type id");
                    types.set(id, name); return name; }
  throw Error("bad type entry " + b);
}
function readEntry() {                  // one top-level entry
  if (eof()) return { kind: EOS };
  const b = u8();
  const named = NAMED_SET.has(b);       // {1,3,9,11,13,15,17,…,45,50}
  const name  = named ? readString() : null;
  switch (canon(b)) {                   // canon = b - (named?0:1) style mapping
    case RefNodeStart:    return { b, name, type: readTypeEntry(), id: i32() };
    case StructNodeStart: return { b, name, type: readTypeEntry() };
    case EndOfNode: case EndOfArray: case Null: return { b, name };
    case ArrayStart:      return { b, len: i64() };
    case PrimArray:  { const n = i32(), per = i32(); return { b, n, per, raw: bytes(n*per) }; }
    case IntRef: case ExtRefIndex: return { b, name, id: i32() };
    case ExtRefGuid: case Guid:    return { b, name, guid: bytes(16) };
    case ExtRefString: case Str:   return { b, name, value: readString() };
    /* fixed-size prims: bool/sbyte/byte:1, short/ushort/char:2,
       int/uint/float:4, long/ulong/double:8, decimal:16 — all LE */
  }
}
// Reading a document: read root value recursively; on RefNodeStart create the
// placeholder object and refs.set(id, obj) BEFORE reading its content.
```

---

*Prepared 2026-08-31. Note that the live `slot_1.sav` changes while the game runs
(the annotated example was taken from a consistent snapshot); `_auto`/`_exit`/`.bak`
variants use the identical format.*

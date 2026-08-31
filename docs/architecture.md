# Web app architecture

Client-side-only SPA (GitHub Pages). No framework, no backend, no telemetry.

## Modules (`web/src/`)

| Module | Responsibility |
| --- | --- |
| `router.ts` | Hash router; views are dynamic imports (code-splitting). |
| `ui/vlist.ts` | Windowed list: renders visible rows only, recycles DOM, rAF-batched. |
| `data/coltable.ts` | Loader for column-oriented JSON tables; index-based access, O(1) joins. |
| `odin/` | OdinSerializer binary reader/writer (see `docs/sav-binary-format.md`) and the lossless document tree (`tree.ts`). |
| `worker/` | Save parse/encode runs in a Web Worker; `client.ts` is a promise RPC wrapper; ArrayBuffers are transferred, not copied. |
| `views/items.ts` | Item library: search, filters, virtualized results, game-tooltip detail panel. |
| `views/editor.ts` | Character-view save editor: hero strip, paper-doll (in-game slot layout), inventory/chest grids, talents panel, save-value game tooltips, multi-file mirroring, download. |

## Data flow

```
pipeline/build-data.mjs  →  web/public/data/*.json  →  fetch + Table  →  views
user .sav  →  ArrayBuffer (transfer)  →  worker: OdinReader → tree → summary
edits (patch ops)  →  worker: apply to tree → OdinWriter  →  ArrayBuffer  →  download
```

The full parsed tree never crosses the worker boundary — only a flat
`SaveSummary` projection the UI needs. Edits are sent back as small patch ops
addressed by node handles.

## Safety invariants

1. On every upload the worker first re-encodes the untouched tree and
   byte-compares against the original. If not identical, editing is disabled
   for that file and a bug report prompt is shown — we never emit a save we
   couldn't round-trip.
2. Protected fields (SessionId, SessionBaselineUtcTicks, SaveCreatedUtcTicks,
   SaveTransactionId, BackupKind) are never exposed as editable.
3. Users are guided to apply identical edits to `slot_1.sav`, `slot_1_auto.sav`
   and `slot_1_exit.sav` (the game falls back silently across them), and to
   answer "Upload to Steam Cloud" on the conflict dialog. When multiple files
   are loaded, character/talent/money edits are mirrored to every file by
   FIELD NAME (handles are per-file index paths and must not cross files).
4. Inventory placement edits validate full item footprints (grid 15×17,
   page count from the save) because the game silently deletes overlapping or
   out-of-range items.

## Performance budget

- First paint < 1s on cold cache: initial JS ≤ ~30 kB gzip, data fetched
  lazily per view, icons lazy-loaded per visible row.
- Item table interactions (search/filter/sort) < 16 ms on 10k rows: typed
  column arrays, precomputed lowercase search keys, no per-row objects.
- Save parse of a 2.5 MB .sav < 250 ms in the worker: single-pass reader over
  a DataView, strings decoded lazily where possible.

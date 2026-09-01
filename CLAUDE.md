# Shadow Dungeon Tools — repo guide

Fan tooling for Shadow Dungeon (Unity 2019.4 Mono, OdinSerializer saves).

## Layout
- `web/` — Vite + vanilla TypeScript SPA (item library + client-side save editor),
  deployed to GitHub Pages by `.github/workflows/deploy.yml`. No framework by design.
- `web/public/data/` — generated game data JSON. Regenerate with the pipeline; do not
  hand-edit.
- `pipeline/` — Node scripts that convert the locally-extracted game tables
  (`%LOCALAPPDATA%Low\OO Cat\ShadowDungeonSaveTool\assets\extracted\`) into
  `web/public/data/`. Python icon extraction reads the Steam install's .assets files.
- `mods/` — BepInEx plugin sources.
- `docs/` — sav-binary-format.md (wire format), save-data-model.md (C# class graph),
  data-schema.md (JSON shapes), ui-reference.md, icons.md, modding/ (BepInEx guide).

## Hard rules
- Every push with user-visible changes MUST add or extend an entry in
  `web/public/data/changelog.json` (newest first: `{date, title, items[]}`,
  plain user-facing language, no commit-message jargon). The home page renders
  it as "What's new". Group same-day work into one entry when it tells one story.
- The save encoder MUST round-trip byte-identically (`web/fixtures/*.sav`, gitignored
  personal saves, are the test corpus — never commit them).
- Never touch save fields: SessionId, SessionBaselineUtcTicks, SaveCreatedUtcTicks,
  SaveTransactionId, BackupKind. All three slot files must receive consistent edits.
- Client-side only: no backend, no telemetry, save bytes never leave the browser.
- Performance first: virtualized lists, Web Worker for parse/encode, transfer (not
  copy) ArrayBuffers, column-oriented JSON for big tables, lazy views.

## Verification
- `cd web && npm run build` (tsc + vite) and `npm test` (vitest round-trip tests).
- Ground truth for save semantics: SaveTool at
  `%LOCALAPPDATA%Low\OO Cat\ShadowDungeonSaveTool\` (dump/encode/validate against the
  game's own DLLs).

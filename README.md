# Shadow Dungeon Tools

Fan-made tooling for **Shadow Dungeon** (OO Cat, Steam appid 4423580):

- **`web/`** — a 100% client-side item library + save editor, hosted on GitHub Pages.
  Upload your `.sav`, browse/edit items, gems, equipment and character stats in the
  browser, download the edited save. No backend, nothing leaves your machine.
  Inspired by [juister.dev/shadowdungeon](https://juister.dev/shadowdungeon/items/) and
  [d2runewizard's hero editor](https://d2runewizard.com/hero-editor).
- **`pipeline/`** — scripts that extract the game's item/affix/set/skill tables and
  localization into the compact JSON the web app ships with.
- **`mods/`** — BepInEx plugins (e.g. [SummonAll](mods/SummonAll/)).
- **`docs/`** — everything reverse-engineered, code-cited:
  - [sav-binary-format.md](docs/sav-binary-format.md) — the OdinSerializer binary wire format, byte by byte
  - [save-data-model.md](docs/save-data-model.md) — every serialized save class, enums, editing invariants
  - [data-schema.md](docs/data-schema.md) — the generated JSON tables and their join keys
  - [icons.md](docs/icons.md) — how item icons are resolved and extracted
  - [architecture.md](docs/architecture.md) — web app design, safety gates, perf budgets
  - [ui-reference.md](docs/ui-reference.md) — research on juister.dev and d2runewizard
  - [modding/](docs/modding/) — BepInEx installation, mod mechanics, plugin authoring, credits

## Credits

- **Max** — author of the *Character Utilities* QoL plugin shared on the Shadow Dungeon
  Discord ([#qol-mod](https://discord.com/channels/1543586564439810138/1543599915006165002)),
  which pioneered the BepInEx modding patterns documented here.
- OO Cat — Shadow Dungeon itself. This project is unaffiliated fan tooling; no game
  assets or code beyond derived data tables are redistributed.

## Web app

Live at: https://dgomesbr.github.io/shadow-dungeon-tools/

```bash
cd web
npm install
npm run dev
```

## Verified against the game

The TypeScript OdinSerializer reader/writer round-trips real saves **byte-identically**,
and saves edited in the browser pass the game's own load checks (replayed via a
validation tool built on the game's DLLs). The editor additionally refuses to enable
editing for any file it cannot round-trip byte-for-byte.

## Design principles

1. **Client-side only.** Static hosting, save parsing/encoding in the browser
   (Web Worker), zero telemetry.
2. **Performance-obsessed.** No framework; vanilla TypeScript, virtualized lists,
   pre-baked binary-friendly data, `requestAnimationFrame`-budgeted rendering.
3. **Never corrupt a save.** The encoder must round-trip byte-identically before any
   edit is applied; all three slot files (`slot_1.sav`, `slot_1_auto.sav`,
   `slot_1_exit.sav`) are edited consistently.

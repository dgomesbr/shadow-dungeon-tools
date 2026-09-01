import type { View } from '../router';
import { applySets, applyUnlock, encodeSave, itemDetail, parseSave, type ParseResult } from '../worker/client';
import type { ItemSummary, Leaf, SaveSummary, SetOp, UnlockOp } from '../worker/protocol';
import { loadCatalog, ELEMENT_NAMES, QUALITY_NAMES, SLOT_NAMES } from '../data/catalog';
import { loadJSON } from '../data/coltable';
import { createCharacterRenderer, esc, loadAffixNames } from '../ui/character';
import { buildFromSummary, encodeShare } from '../share/codec';

const BASE = import.meta.env.BASE_URL;
const CELL = 32; // px per inventory grid cell (game uses 60)
const GRID_W = 15;
const GRID_H = 17;

interface OpenFile {
  name: string;
  size: number;
  result: ParseResult;
}

interface LevelEntry { id: string; name: string; boss: boolean; final?: boolean }
interface LevelsFile { chapters: { id: number; name: string; levels: LevelEntry[] }[] }

// Display names of the four Corrupted Realm tiers (save fields easy…master).
const REALM_TIERS = [
  ['easy', 'Normal'], ['medium', 'Hard'], ['hard', 'Nightmare'], ['master', 'Inferno'],
] as const;

interface TreeChoice { name: string; label?: string; icon?: string; desc?: string }
interface TreeNode {
  skill: string; x: number; y: number; icon: string; max: number; unlock: number;
  /** Divine Favor nodes: the up-to-3 selectable skills. */
  choices?: TreeChoice[];
}
interface TalentTrees {
  archetypes: { name: string; classIds: number[] }[];
  trees: { xi: number; name: string; nodes: TreeNode[]; links?: [string, string][] }[];
  synthesized?: boolean;
}

interface State {
  files: Map<string, OpenFile>;
  active: string | null;
  container: 'inventory' | 'chest' | 'talents';
  page: number;
  selected: ItemSummary | null;
  pending: Map<string, SetOp>;
  /** Recompose mode: apply edits to every loaded file, not just the active one. */
  mirror: boolean;
  /** Skill-tree tabs: archetype index (archetypes.length = Divine Favor) and tree xi. */
  talentArch: number;
  talentXi: number;
}

export async function editorView(): Promise<View> {
  const cat = await loadCatalog();
  const affixNames = await loadAffixNames();
  const R = createCharacterRenderer(cat, affixNames);
  const itemInfo = R.itemInfo;

  return {
    mount(container) {
      const st: State = {
        files: new Map(), active: null, container: 'inventory', page: 0,
        selected: null, pending: new Map(), mirror: true,
        talentArch: -1, talentXi: -1, // -1 = follow the character's class
      };
      // Deep-handle maps per file+item; index paths are stable across value
      // edits, so entries stay valid until files are (re)loaded.
      const hmapCache = new Map<string, Promise<Map<string, string>>>();

      container.innerHTML = `<div class="ed"></div>`;
      const host = container.firstElementChild as HTMLElement;

      async function loadFiles(list: FileList | File[]): Promise<void> {
        for (const f of list) {
          if (!f.name.endsWith('.sav')) continue;
          try {
            const result = await parseSave(await f.arrayBuffer(), f.name);
            st.files.set(f.name, { name: f.name, size: f.size, result });
            st.active = f.name;
          } catch (e) {
            alert(`${f.name}: not a valid save (${e instanceof Error ? e.message : e})`);
          }
        }
        hmapCache.clear();
        st.selected = null;
        st.pending.clear();
        render();
      }

      /** slot_N.sav / slot_N_auto.sav / slot_N_exit.sav that are not loaded
       *  yet, for any slot the user has started loading. The game silently
       *  falls back across the three, so edits must land in all of them. */
      function missingSlotFiles(): string[] {
        const names = [...st.files.keys()].map((n) => n.toLowerCase());
        const slots = new Set<string>();
        for (const n of names) {
          const m = /^slot_(\d+)(?:_auto|_exit)?\.sav$/.exec(n);
          if (m) slots.add(m[1]!);
        }
        const missing: string[] = [];
        for (const s of slots) {
          for (const suffix of ['', '_auto', '_exit']) {
            const want = `slot_${s}${suffix}.sav`;
            if (!names.includes(want)) missing.push(want);
          }
        }
        return missing;
      }

      function active(): OpenFile | null {
        return st.active ? st.files.get(st.active) ?? null : null;
      }

      function stage(handle: string, value: number | string | boolean): void {
        st.pending.set(handle, { handle, value });
        renderPendingBar();
      }

      async function commit(): Promise<void> {
        const f = active();
        if (!f || st.pending.size === 0) return;
        const sets = [...st.pending.values()];
        const summary = await applySets(f.name, sets);
        f.result.summary = summary;

        // The game falls back silently across slot/auto/exit backups, so
        // character-level edits should hit every loaded file. Handles are
        // per-file, so mirror by FIELD NAME (player fields, money, talents).
        if (st.mirror) {
          const src = f.result.summary;
          const nameOf = new Map<string, string>(); // handle → semantic key
          for (const l of src.player) nameOf.set(l.handle, `p:${l.name}`);
          for (const l of src.talentPoints) nameOf.set(l.handle, `tp:${l.name}`);
          for (const t of src.talents) nameOf.set(t.handle, `t:${t.name}`);
          if (src.money) nameOf.set(src.money.handle, 'money');
          for (const other of st.files.values()) {
            if (other.name === f.name || !other.result.roundTrip) continue;
            const os = other.result.summary;
            const byKey = new Map<string, string>(); // semantic key → other handle
            for (const l of os.player) byKey.set(`p:${l.name}`, l.handle);
            for (const l of os.talentPoints) byKey.set(`tp:${l.name}`, l.handle);
            for (const t of os.talents) byKey.set(`t:${t.name}`, t.handle);
            if (os.money) byKey.set('money', os.money.handle);
            const mirrored: SetOp[] = [];
            for (const op of sets) {
              const key = nameOf.get(op.handle);
              const h = key ? byKey.get(key) : undefined;
              if (h) mirrored.push({ handle: h, value: op.value });
            }
            if (mirrored.length) other.result.summary = await applySets(other.name, mirrored);
          }
        }

        const sel = st.selected?.handle;
        st.pending.clear();
        st.selected = sel
          ? [...summary.equipment, ...summary.inventory, ...summary.chest].find((i) => i.handle === sel) ?? null
          : null;
        render();
      }

      // ---- unlock modals ---------------------------------------------------
      let levelsCache: Promise<LevelsFile | null> | null = null;
      const levelsData = (): Promise<LevelsFile | null> =>
        (levelsCache ??= loadJSON<LevelsFile>('levels.json').catch(() => null));

      function openModal(title: string): { body: HTMLElement; close: () => void } {
        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        overlay.innerHTML = `<div class="modal">
          <div class="modal-head"><h3>${esc(title)}</h3><button class="modal-x" title="Close">✕</button></div>
          <div class="modal-body"></div>
        </div>`;
        document.body.appendChild(overlay);
        const close = (): void => overlay.remove();
        overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
        overlay.querySelector('.modal-x')!.addEventListener('click', close);
        return { body: overlay.querySelector<HTMLElement>('.modal-body')!, close };
      }

      /** Apply an unlock op to every loaded round-trip-safe file. */
      async function unlockAll(op: UnlockOp): Promise<string> {
        let files = 0, levels = 0, chapters = 0;
        for (const o of st.files.values()) {
          if (!o.result.roundTrip) continue;
          const r = await applyUnlock(o.name, op);
          o.result.summary = r.summary;
          files++;
          levels += r.added.levels;
          chapters += r.added.chapters;
        }
        const sel = st.selected?.handle;
        const s = active()?.result.summary;
        st.selected = sel && s
          ? [...s.equipment, ...s.inventory, ...s.chest].find((i) => i.handle === sel) ?? null
          : null;
        render();
        return `Added ${levels} level unlock(s), ${chapters} chapter(s) across ${files} file(s).`;
      }

      async function openStoryModal(): Promise<void> {
        const f = active();
        if (!f) return;
        const data = await levelsData();
        const m = openModal('Unlock story levels');
        if (!data) {
          m.body.innerHTML = `<p class="dim">Level catalog (levels.json) is not available in this build.</p>`;
          return;
        }
        const unlocked = new Set(f.result.summary.unlockedLevels);
        m.body.innerHTML = `
          <p class="dim">Tick the levels to unlock — applies to all loaded files.
          Already-unlocked levels are checked and locked. 💀 marks a boss level.</p>
          ${data.chapters.map((ch) => {
            const done = ch.levels.filter((l) => unlocked.has(l.id)).length;
            return `<details ${done < ch.levels.length ? 'open' : ''}>
              <summary><label><input type="checkbox" class="ch-all" data-ch="${ch.id}">
                <b>${esc(ch.name)}</b> <span class="dim">(${done}/${ch.levels.length} unlocked · ends at ${esc(ch.levels[ch.levels.length - 1]?.name ?? '')})</span></label></summary>
              <div class="lvl-grid">${ch.levels.map((l) => `
                <label class="lvl"><input type="checkbox" data-lvl="${esc(l.id)}" data-ch="${ch.id}"
                  ${unlocked.has(l.id) ? 'checked disabled' : ''}>
                  ${esc(l.name)}${l.boss ? ' <span class="boss-ic" title="Boss level">(💀)</span>' : ''}</label>`).join('')}
              </div></details>`;
          }).join('')}
          <div class="modal-foot">
            <button id="m-all">Select all</button>
            <span id="m-status" class="dim"></span>
            <button id="m-apply" class="primary">Unlock selected</button>
          </div>`;
        m.body.querySelectorAll<HTMLInputElement>('.ch-all').forEach((cb) =>
          cb.addEventListener('change', () => {
            m.body.querySelectorAll<HTMLInputElement>(`input[data-lvl][data-ch="${cb.dataset['ch']}"]:not(:disabled)`)
              .forEach((l) => { l.checked = cb.checked; });
          }));
        m.body.querySelector('#m-all')!.addEventListener('click', () => {
          m.body.querySelectorAll<HTMLInputElement>('input[data-lvl]:not(:disabled)').forEach((l) => { l.checked = true; });
        });
        m.body.querySelector('#m-apply')!.addEventListener('click', async () => {
          const boxes = [...m.body.querySelectorAll<HTMLInputElement>('input[data-lvl]:not(:disabled)')]
            .filter((b) => b.checked);
          if (!boxes.length) { m.close(); return; }
          const levels = boxes.map((b) => b.dataset['lvl']!);
          const chapters = [...new Set(boxes.map((b) => Number(b.dataset['ch'])))];
          const status = m.body.querySelector<HTMLElement>('#m-status')!;
          status.textContent = 'Applying…';
          status.textContent = await unlockAll({ chapters, levels, bossLevels: [] });
          setTimeout(m.close, 1400);
        });
      }

      async function openRealmModal(): Promise<void> {
        const f = active();
        if (!f) return;
        const data = await levelsData();
        const mj = f.result.summary.mijing;
        const m = openModal('Unlock Corrupted Realm');
        m.body.innerHTML = `
          <p class="dim">Pick a difficulty and the floor to unlock up to. Floors are never lowered.
          Unlocking the Corrupted Realm also unlocks <b>all story chapters and levels</b>${data ? '' : ' (unavailable: level catalog missing)'} — applies to all loaded files.</p>
          <div class="realm-tiers">${REALM_TIERS.map(([key, label], i) => `
            <label class="realm-tier"><input type="radio" name="tier" value="${key}" ${i === 3 ? 'checked' : ''}>
              <b>${label}</b> <span class="dim">currently floor ${esc(mj[key])}</span></label>`).join('')}
          </div>
          <label class="frow" style="max-width:260px"><span>Unlock up to floor</span>
            <input id="m-floor" type="number" min="1" step="1" value="${Math.max(mj.master, 1)}"></label>
          <div class="modal-foot">
            <span id="m-status" class="dim"></span>
            <button id="m-apply" class="primary">Unlock</button>
          </div>`;
        m.body.querySelector('#m-apply')!.addEventListener('click', async () => {
          const tier = m.body.querySelector<HTMLInputElement>('input[name="tier"]:checked')!.value as
            'easy' | 'medium' | 'hard' | 'master';
          const floor = Math.max(1, Math.trunc(Number(m.body.querySelector<HTMLInputElement>('#m-floor')!.value) || 1));
          const op: UnlockOp = {
            chapters: data ? data.chapters.map((c) => c.id) : [],
            levels: data ? data.chapters.flatMap((c) => c.levels.map((l) => l.id)) : [],
            bossLevels: [],
            mijing: { [tier]: floor },
          };
          const status = m.body.querySelector<HTMLElement>('#m-status')!;
          status.textContent = 'Applying…';
          status.textContent = await unlockAll(op);
          setTimeout(m.close, 1400);
        });
      }

      function saveBlob(buf: ArrayBuffer, name: string): void {
        const a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([buf], { type: 'application/octet-stream' }));
        a.download = name;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 5000);
      }

      // Compact build link: binary-packed + deflated + base64url in the URL
      // hash — short enough for Discord's 2000-char non-Nitro limit, and the
      // fragment never reaches any server.
      async function share(): Promise<void> {
        const f = active();
        if (!f) return;
        const btn = host.querySelector<HTMLButtonElement>('#share');
        try {
          const payload = await encodeShare(buildFromSummary(f.result.summary), cat);
          const url = `${location.origin}${location.pathname}#/build?d=${payload}`;
          await navigator.clipboard.writeText(url);
          if (btn) {
            btn.textContent = `Copied! (${url.length} chars)`;
            setTimeout(() => { btn.textContent = 'Share build'; }, 2500);
          }
        } catch (e) {
          alert(`Share failed: ${e instanceof Error ? e.message : e}`);
        }
      }

      async function download(all: boolean): Promise<void> {
        const f = active();
        if (!f) return;
        if (st.pending.size && confirm(`Apply ${st.pending.size} pending change(s) before downloading?`)) {
          await commit();
        }
        const targets = all ? [...st.files.values()].filter((o) => o.result.roundTrip) : [f];
        for (const t of targets) saveBlob(await encodeSave(t.name), t.name);
      }

      // ---- rendering ----
      function render(): void {
        const f = active();
        if (!f) { renderDropzone(); return; }
        const s = f.result.summary;
        const p = new Map(s.player.map((l) => [l.name, l.value]));
        host.innerHTML = `
          <div class="ed-top">
            <div class="ed-tabs">${[...st.files.values()].map((o) => `
              <button class="ed-tab ${o.name === st.active ? 'on' : ''}" data-file="${esc(o.name)}">
                ${esc(o.name)} ${o.result.roundTrip ? '<span class="ok">✓</span>' : '<span class="bad">✗</span>'}
              </button>`).join('')}
              <label class="ed-add">+ add file<input type="file" accept=".sav" multiple hidden></label>
              <button class="tab-act" id="unlock-story">Unlock story…</button>
              <button class="tab-act" id="unlock-realm">Unlock Corrupted Realm…</button>
            </div>
            <div class="ed-actions">
              <span class="dim">v${esc(s.gameVersion)} · ${(f.size / 1024 / 1024).toFixed(2)} MB</span>
              ${st.files.size > 1 ? `<label class="dim mirror-lbl" title="Recompose: every edit is applied to all loaded files so the game can't fall back to an unedited backup">
                <input type="checkbox" id="mirror" ${st.mirror ? 'checked' : ''}> apply to all ${st.files.size} files</label>` : ''}
              <button id="share" title="Copy a compact build link (fits in a Discord message)">Share build</button>
              ${st.files.size > 1
                ? `<button id="dl">Download ${esc(f.name)}</button><button id="dl-all" class="primary">Download all ${st.files.size}</button>`
                : `<button id="dl" class="primary" ${f.result.roundTrip ? '' : 'disabled title="round-trip check failed — editing disabled"'}>Download</button>`}
            </div>
          </div>
          ${f.result.roundTrip ? '' : `<p class="ed-warn">⚠ This file did not re-encode byte-identically (first difference at byte ${f.result.firstDiff}). Editing is disabled to protect your save — please report this on GitHub.</p>`}
          ${missingSlotFiles().length ? `<p class="ed-missing">⚠ The game keeps <b>three</b> copies of every slot and silently falls back to the <code>_auto</code>/<code>_exit</code> backups —
            load them all so your character is recomposed consistently. Missing:
            ${missingSlotFiles().map((n) => `<b>${esc(n)}</b>`).join(', ')}
            <label class="ed-add-inline">+ add them<input type="file" accept=".sav" multiple hidden></label></p>` : ''}
          <div class="ed-hero">
            <h2>${esc(p.get('PlayerName') ?? '—')}</h2>
            <span class="ed-hero-stat"><b>${esc(p.get('Level') ?? '?')}</b> level</span>
            <span class="ed-hero-stat"><b>${esc(p.get('DFLevel') ?? 0)}</b> divine favor</span>
            <span class="ed-hero-stat gold"><b>${Number(s.money?.value ?? 0).toLocaleString()}</b> gold</span>
            <span class="ed-hero-stat"><b>${Math.floor(s.playTime / 3600)}h ${Math.floor((s.playTime % 3600) / 60)}m</b> played</span>
          </div>
          <div class="ed-cols">
            <section class="ed-char">
              <h4>Equipment</h4>
              <div class="ed-doll" id="equip"></div>
              <h4>Character</h4>
              <div id="char-fields"></div>
              <h4>Talents</h4>
              <div id="talents"></div>
              <details><summary>All player fields (${s.player.length})</summary><div id="char-all"></div></details>
            </section>
            <section class="ed-inv">
              <div class="ed-inv-head">
                <button class="seg ${st.container === 'inventory' ? 'on' : ''}" data-cont="inventory">Inventory</button>
                <button class="seg ${st.container === 'chest' ? 'on' : ''}" data-cont="chest">Global Chest</button>
                <button class="seg ${st.container === 'talents' ? 'on' : ''}" data-cont="talents">Skill Trees</button>
                <span id="pages"></span>
              </div>
              ${st.container === 'talents'
                ? '<div id="talent-trees"><p class="hint">Loading skill trees…</p></div>'
                : `<div class="ed-grid" id="grid" style="width:${GRID_W * CELL}px;height:${GRID_H * CELL}px"></div>`}
            </section>
            <aside class="ed-item" id="item-panel"><p class="hint">Select an item — its in-game tooltip renders here.</p></aside>
          </div>
          <div class="ed-pending" id="pending" hidden>
            <span id="pending-n"></span>
            <span id="pending-scope" class="dim"></span>
            <button id="apply">Apply</button>
            <button id="discard">Discard</button>
          </div>`;

        host.querySelectorAll<HTMLButtonElement>('.ed-tab').forEach((b) =>
          b.addEventListener('click', () => { st.active = b.dataset['file']!; st.selected = null; st.pending.clear(); render(); }));
        host.querySelectorAll<HTMLInputElement>('.ed-add input, .ed-add-inline input').forEach((inp) =>
          inp.addEventListener('change', (e) => void loadFiles((e.target as HTMLInputElement).files!)));
        host.querySelector<HTMLInputElement>('#mirror')?.addEventListener('change', (e) => {
          st.mirror = (e.target as HTMLInputElement).checked;
          renderPendingBar();
        });
        host.querySelector('#unlock-story')!.addEventListener('click', () => void openStoryModal());
        host.querySelector('#unlock-realm')!.addEventListener('click', () => void openRealmModal());
        host.querySelector('#share')!.addEventListener('click', () => void share());
        host.querySelector('#dl')!.addEventListener('click', () => void download(false));
        host.querySelector('#dl-all')?.addEventListener('click', () => void download(true));
        host.querySelectorAll<HTMLButtonElement>('.seg').forEach((b) =>
          b.addEventListener('click', () => { st.container = b.dataset['cont'] as State['container']; st.page = 0; render(); }));
        host.querySelector('#apply')?.addEventListener('click', () => void commit());
        host.querySelector('#discard')?.addEventListener('click', () => { st.pending.clear(); render(); });

        renderChar(s, f.result.roundTrip);
        renderTalents(s, f.result.roundTrip);
        renderDoll(s);
        if (st.container === 'talents') void renderTalentTrees(s, f);
        else renderGrid(s);
        renderItemPanel();
        renderPendingBar();
      }

      function fieldRow(l: Leaf, editable: boolean): string {
        const input = l.kind === 'bool'
          ? `<input type="checkbox" data-h="${l.handle}" data-k="bool" ${l.value ? 'checked' : ''} ${editable ? '' : 'disabled'}>`
          : `<input type="${l.kind === 'string' ? 'text' : 'number'}" data-h="${l.handle}" data-k="${l.kind}"
               value="${esc(l.value)}" ${l.kind === 'float' ? 'step="any"' : ''} ${editable ? '' : 'disabled'}>`;
        return `<label class="frow"><span>${esc(l.name)}</span>${input}</label>`;
      }

      function bindInputs(el: HTMLElement): void {
        el.querySelectorAll<HTMLInputElement>('input[data-h]').forEach((inp) => {
          inp.addEventListener('change', () => {
            const k = inp.dataset['k'];
            const v = k === 'bool' ? inp.checked : k === 'string' ? inp.value : Number(inp.value);
            stage(inp.dataset['h']!, v);
          });
        });
      }

      function renderChar(s: SaveSummary, editable: boolean): void {
        const KEY = ['PlayerName', 'Level', 'Xp_Total', 'Health', 'Mana', 'DFLevel', 'DFXp_Total'];
        const byName = new Map(s.player.map((l) => [l.name, l]));
        const rows: string[] = [];
        for (const k of KEY) {
          const l = byName.get(k);
          if (l) rows.push(fieldRow(l, editable));
        }
        if (s.money) rows.push(fieldRow({ ...s.money, name: 'Money (gold)' }, editable));
        const cf = host.querySelector<HTMLElement>('#char-fields')!;
        cf.innerHTML = rows.join('');
        bindInputs(cf);
        const all = host.querySelector<HTMLElement>('#char-all')!;
        all.innerHTML = s.player.filter((l) => !KEY.includes(l.name)).map((l) => fieldRow(l, editable)).join('');
        bindInputs(all);
      }

      function renderTalents(s: SaveSummary, editable: boolean): void {
        const el = host.querySelector<HTMLElement>('#talents');
        if (!el) return;
        const pts = new Map(s.talentPoints.map((l) => [l.name, l]));
        const invested = s.talents.filter((t) => t.level > 0);
        const skillClass = (name: string): number => {
          const i = cat.skillByIndexName.get(name);
          if (i === undefined) return -1;
          return Number(cat.skills.rows[i]![cat.skills.col('Xi')]) || -1;
        };
        const talentRow = (t: { name: string; level: number; handle: string }): string => `
          <label class="frow"><span title="class ${skillClass(t.name)}">${esc(t.name)}</span>
          <input type="number" data-h="${t.handle}" data-k="int" value="${t.level}" ${editable ? '' : 'disabled'}></label>`;
        el.innerHTML = `
          <p class="dim">Points: ${esc(pts.get('P_Used')?.value ?? 0)} / ${esc(pts.get('P_Base')?.value ?? 0)} used
            · DF ${esc(pts.get('P_Used_DF')?.value ?? 0)}</p>
          ${pts.get('P_Base') ? fieldRow({ ...pts.get('P_Base')!, name: 'P_Base (total points)' }, editable) : ''}
          <div id="talent-rows">${invested.map(talentRow).join('')}</div>
          <input id="talent-search" type="search" placeholder="Find any of ${s.talents.length} skills…">
          <div id="talent-found"></div>`;
        bindInputs(el);
        const search = el.querySelector<HTMLInputElement>('#talent-search')!;
        const found = el.querySelector<HTMLElement>('#talent-found')!;
        search.addEventListener('input', () => {
          const q = search.value.trim().toLowerCase();
          if (!q) { found.innerHTML = ''; return; }
          const hits = s.talents.filter((t) => t.level === 0 && t.name.toLowerCase().includes(q)).slice(0, 20);
          found.innerHTML = hits.map(talentRow).join('');
          bindInputs(found);
        });
      }

      // Paper-doll arranged like the in-game character screen.
      function renderDoll(s: SaveSummary): void {
        const el = host.querySelector<HTMLElement>('#equip')!;
        el.innerHTML = R.dollHTML(s.equipment, st.selected?.handle);
        el.querySelectorAll<HTMLButtonElement>('.eq-slot[data-h]').forEach((b) =>
          b.addEventListener('click', () => selectItem(b.dataset['h']!)));
      }

      // ---- skill trees (talent view) ---------------------------------------
      let treesCache: Promise<TalentTrees | null> | null = null;
      const treesData = (): Promise<TalentTrees | null> =>
        (treesCache ??= loadJSON<TalentTrees>('talent-trees.json').catch(() => null));

      /** Add/subtract talent points. Adjusts the matching pool (P_Used or
       *  P_Used_DF — class trees cost 1 point/level and the game trusts the
       *  saved P_Used; P_Used_DF is recomputed on load but kept in sync for
       *  display). Mirrors additively to other files by skill name. */
      async function applyTalentEdit(f: OpenFile, skillName: string, xi: number, delta: number, max: number): Promise<void> {
        const cap = max > 0 ? max : 999;
        const poolName = xi === 12 ? 'P_Used_DF' : 'P_Used';
        const mkSets = (sum: SaveSummary): SetOp[] => {
          const t = sum.talents.find((x) => x.name === skillName);
          if (!t) return [];
          const newLevel = Math.max(0, Math.min(cap, t.level + delta));
          const d = newLevel - t.level;
          if (!d) return [];
          const sets: SetOp[] = [{ handle: t.handle, value: newLevel }];
          const pool = sum.talentPoints.find((l) => l.name === poolName);
          if (pool) sets.push({ handle: pool.handle, value: Math.max(0, Number(pool.value) + d) });
          return sets;
        };
        const sets = mkSets(f.result.summary);
        if (!sets.length) return;
        f.result.summary = await applySets(f.name, sets);
        if (st.mirror) {
          for (const other of st.files.values()) {
            if (other.name === f.name || !other.result.roundTrip) continue;
            const osets = mkSets(other.result.summary);
            if (osets.length) other.result.summary = await applySets(other.name, osets);
          }
        }
        renderChar(f.result.summary, f.result.roundTrip);
        renderTalents(f.result.summary, f.result.roundTrip);
        await renderTalentTrees(f.result.summary, f);
      }

      async function renderTalentTrees(s: SaveSummary, f: OpenFile): Promise<void> {
        const el = host.querySelector<HTMLElement>('#talent-trees');
        if (!el) return;
        const data = await treesData();
        if (!data) {
          el.innerHTML = '<p class="hint">Skill-tree layout data is not available in this build.</p>';
          return;
        }
        const lvl = new Map(s.talents.map((t) => [t.name, t.level]));
        const tp = new Map(s.talentPoints.map((l) => [l.name, Number(l.value) || 0]));
        const pBase = tp.get('P_Base') ?? 0;
        const pUsed = tp.get('P_Used') ?? 0;
        const pDF = tp.get('P_Used_DF') ?? 0;
        const nArch = data.archetypes.length;

        // Only the character's own class trees are editable; Divine Favor
        // unlocks at level 100 (TalentManager gates it the same way).
        const pmap = new Map(s.player.map((l) => [l.name, l.value]));
        const playerType = Math.min(Math.max(Number(pmap.get('PlayerType')) || 0, 0), nArch - 1);
        const charLevel = Number(pmap.get('Level')) || 0;
        const dfAvailable = charLevel >= 100;
        if (st.talentArch < 0
          || (st.talentArch < nArch && st.talentArch !== playerType)
          || (st.talentArch >= nArch && !dfAvailable)) {
          st.talentArch = playerType;
          st.talentXi = data.archetypes[playerType]?.classIds[0] ?? 0;
        }
        const isDF = st.talentArch >= nArch;
        if (st.talentXi < 0) st.talentXi = isDF ? 12 : data.archetypes[st.talentArch]?.classIds[0] ?? 0;
        const curXi = isDF ? 12 : st.talentXi;
        const tree = data.trees.find((t) => t.xi === curXi);
        const investedIn = (t: { nodes: TreeNode[] }): number =>
          t.nodes.reduce((a, n) => a + (lvl.get(n.skill) ?? 0), 0);

        let html = `
          <div class="tt-head">
            <span class="ed-hero-stat"><b>${pBase}</b> total</span>
            <span class="ed-hero-stat"><b>${pUsed}</b> class trees</span>
            <span class="ed-hero-stat"><b>${pDF}</b> divine favor</span>
            <span class="ed-hero-stat"><b>${pBase - pUsed - pDF}</b> available</span>
            <span class="dim">click +1 · right-click or Shift+click −1</span>
          </div>
          <div class="tt-tabs">
            ${data.archetypes.map((a, i) => {
              const mine = i === playerType;
              return `<button class="seg ${!isDF && st.talentArch === i ? 'on' : ''}" data-arch="${i}"
                ${mine ? '' : `disabled title="This character is a ${esc(data.archetypes[playerType]?.name ?? '')}"`}>${esc(a.name)}</button>`;
            }).join('')}
            <button class="seg ${isDF ? 'on' : ''}" data-arch="${nArch}"
              ${dfAvailable ? '' : `disabled title="Divine Favor unlocks at level 100 (character is level ${charLevel})"`}>Divine Favor</button>
          </div>`;
        if (!isDF) {
          const arch = data.archetypes[st.talentArch];
          html += `<div class="tt-tabs sub">${(arch?.classIds ?? []).map((xi) => {
            const t = data.trees.find((x) => x.xi === xi);
            return `<button class="seg ${st.talentXi === xi ? 'on' : ''}" data-xi="${xi}">
              ${esc(t?.name ?? `Tree ${xi}`)} <span class="dim">${t ? investedIn(t) : 0}</span></button>`;
          }).join('')}</div>`;
        }

        if (!tree || !tree.nodes.length) {
          el.innerHTML = `${html}<p class="hint">No nodes in this tree.</p>`;
        } else {
          const inv = investedIn(tree);
          const xs = tree.nodes.map((n) => n.x);
          const ys = tree.nodes.map((n) => n.y);
          const minX = Math.min(...xs), maxX = Math.max(...xs);
          const minY = Math.min(...ys), maxY = Math.max(...ys);
          const NODE = 52, W = 680;
          const spanX = Math.max(1, maxX - minX);
          const spanY = Math.max(1, maxY - minY);
          const innerW = W - NODE - 24;
          const H = Math.min(1600, Math.max(340, (spanY / spanX) * innerW + NODE + 44));
          const px = (x: number): number => 12 + ((x - minX) / spanX) * innerW;
          const py = (y: number): number => 8 + ((y - minY) / spanY) * (H - NODE - 40);
          const pos = new Map(tree.nodes.map((n) => [n.skill, { x: px(n.x), y: py(n.y) }]));

          const linkSvg = (tree.links ?? []).map(([a, b]) => {
            const pa = pos.get(a), pb = pos.get(b);
            if (!pa || !pb) return '';
            return `<line x1="${pa.x + NODE / 2}" y1="${pa.y + NODE / 2}" x2="${pb.x + NODE / 2}" y2="${pb.y + NODE / 2}"/>`;
          }).join('');

          const talByName = new Map(s.talents.map((t) => [t.name, t]));
          html += `<div class="tree-canvas" style="width:${W}px;height:${H}px">
            <svg width="${W}" height="${H}">${linkSvg}</svg>
            ${tree.nodes.map((n) => {
              const tal = talByName.get(n.skill);
              const cur = tal?.level ?? 0;
              const locked = n.unlock > 0 && inv < n.unlock;
              const p = pos.get(n.skill)!;
              // Divine Favor nodes display the picked choice (SelectedIndex),
              // falling back to the first choice's English label.
              let label = n.skill;
              let icon = n.icon;
              let choiceLines = '';
              if (n.choices?.length) {
                const sel = tal?.selected ?? -1;
                const act = (sel >= 0 && n.choices[sel]) ? n.choices[sel] : n.choices[0];
                label = act?.label ?? n.skill;
                if (sel >= 0 && n.choices[sel]?.icon) icon = n.choices[sel]!.icon!;
                choiceLines = '\n' + n.choices.map((c, i) =>
                  `${i === sel ? '▶' : '·'} ${c.label ?? c.name}${c.desc ? ` — ${c.desc}` : ''}`).join('\n');
              }
              return `<button class="tnode ${locked ? 'locked' : ''} ${cur > 0 ? 'has' : ''}"
                style="left:${p.x}px;top:${p.y}px" data-skill="${esc(n.skill)}" data-max="${n.max}"
                title="${esc(label)} — ${cur}/${n.max}${locked ? ` · locked (needs ${n.unlock} pts in tree, has ${inv})` : ''}${esc(choiceLines)}">
                ${icon ? `<img src="${BASE + icon}" alt="" loading="lazy" onerror="this.style.display='none'">` : ''}
                <span class="tn-badge">${cur}/${n.max}</span>
                <span class="tn-name">${esc(label)}</span>
              </button>`;
            }).join('')}
          </div>`;
          el.innerHTML = html;

          const editable = f.result.roundTrip;
          el.querySelectorAll<HTMLButtonElement>('.tnode').forEach((b) => {
            const doEdit = (delta: number): void => {
              if (!editable) return;
              const locked = b.classList.contains('locked');
              if (delta > 0 && locked) return;
              void applyTalentEdit(f, b.dataset['skill']!, curXi, delta, Number(b.dataset['max']));
            };
            b.addEventListener('click', (e) => doEdit(e.shiftKey ? -1 : 1));
            b.addEventListener('contextmenu', (e) => { e.preventDefault(); doEdit(-1); });
          });
        }
        el.querySelectorAll<HTMLButtonElement>('[data-arch]').forEach((b) =>
          b.addEventListener('click', () => {
            st.talentArch = Number(b.dataset['arch']);
            st.talentXi = st.talentArch >= nArch ? 12 : data.archetypes[st.talentArch]?.classIds[0] ?? 0;
            void renderTalentTrees(active()!.result.summary, active()!);
          }));
        el.querySelectorAll<HTMLButtonElement>('[data-xi]').forEach((b) =>
          b.addEventListener('click', () => {
            st.talentXi = Number(b.dataset['xi']);
            void renderTalentTrees(active()!.result.summary, active()!);
          }));
      }

      function renderGrid(s: SaveSummary): void {
        const items = st.container === 'inventory' ? s.inventory : s.chest;
        const pages = st.container === 'inventory' ? Math.max(s.pageCount, 1) : 1;
        const pagesEl = host.querySelector<HTMLElement>('#pages')!;
        pagesEl.innerHTML = pages > 1
          ? Array.from({ length: pages }, (_, p) =>
              `<button class="pg ${p === st.page ? 'on' : ''}" data-p="${p}">${p + 1}</button>`).join('')
          : '';
        pagesEl.querySelectorAll<HTMLButtonElement>('.pg').forEach((b) =>
          b.addEventListener('click', () => { st.page = Number(b.dataset['p']); renderGrid(s); }));

        const grid = host.querySelector<HTMLElement>('#grid')!;
        const cells: string[] = [];
        for (const it of items) {
          if (st.container === 'inventory' && it.page !== st.page) continue;
          const info = itemInfo(it);
          cells.push(`<button class="gi q${it.quality}" data-h="${it.handle}"
            style="left:${it.gridX * CELL}px;top:${it.gridY * CELL}px;width:${info.w * CELL}px;height:${info.h * CELL}px;--quality:var(--q${it.quality})"
            title="${esc(info.name)}">
            ${info.icon ? `<img src="${BASE + info.icon}" alt="" loading="lazy" decoding="async">` : `<span class="gi-id">#${it.globalId}</span>`}
            ${it.stack > 1 ? `<b class="gi-n">${it.stack}</b>` : ''}
          </button>`);
        }
        grid.innerHTML = cells.join('');
        grid.querySelectorAll<HTMLButtonElement>('.gi').forEach((b) =>
          b.addEventListener('click', () => selectItem(b.dataset['h']!)));
      }

      function selectItem(handle: string): void {
        const s = active()?.result.summary;
        if (!s) return;
        st.selected = [...s.equipment, ...s.inventory, ...s.chest].find((i) => i.handle === handle) ?? null;
        host.querySelectorAll('.eq-slot.sel').forEach((e) => e.classList.remove('sel'));
        host.querySelector(`.eq-slot[data-h="${handle}"]`)?.classList.add('sel');
        renderItemPanel();
      }

      // ---- click-to-edit inside the tooltip --------------------------------
      // Elemental leaves already carry handles; nested affix/socket handles are
      // resolved once per item from the worker's deep-leaf listing (dotted
      // names like "Main[0][2].number").
      function buildHandleMap(it: ItemSummary, deep: Leaf[]): Map<string, string> {
        const m = new Map<string, string>();
        const q = it.leaves.find((l) => l.name === 'Quality');
        if (q) m.set('q', q.handle);
        ELEMENT_NAMES.forEach((n, e) => {
          const l = it.leaves.find((x) => x.name === n);
          if (l) m.set(`el:${e}`, l.handle);
        });
        for (const l of deep) {
          const mt = /^(Main|DOT|WPSK)\[\d+\]\[(\d+)\]\.(IndexName|Index|EL|number|Number)$/.exec(l.name);
          if (!mt) continue;
          const group = mt[1] === 'Main' ? 'main' : mt[1] === 'DOT' ? 'dot' : 'wpsk';
          const part = mt[3] === 'number' || mt[3] === 'Number' ? 'n'
            : mt[3] === 'EL' ? 'el'
            : mt[3] === 'IndexName' ? 'skill' : 'idx';
          m.set(`${group}:${mt[2]}:${part}`, l.handle);
        }
        return m;
      }

      function handleMapFor(fileName: string, item: ItemSummary): Promise<Map<string, string>> {
        const key = `${fileName}::${item.handle}`;
        let p = hmapCache.get(key);
        if (!p) {
          p = itemDetail(fileName, item.handle).then((deep) => buildHandleMap(item, deep));
          hmapCache.set(key, p);
        }
        return p;
      }

      /** Apply semantic token edits ([tokenKey, value] pairs) to the active
       *  file's item — and, in recompose mode, to the same equipped item
       *  (matched by slot + GlobalID) in every other loaded file, so the game
       *  can't fall back to a backup with the old item. */
      async function applyTokenEdits(
        f: OpenFile, it: ItemSummary, edits: [string, number | string][],
      ): Promise<void> {
        const resolve = async (fileName: string, item: ItemSummary): Promise<SetOp[]> => {
          const map = await handleMapFor(fileName, item);
          const sets: SetOp[] = [];
          for (const [k, v] of edits) {
            const h = map.get(k);
            if (h) sets.push({ handle: h, value: v });
          }
          return sets;
        };
        const sets = await resolve(f.name, it);
        if (sets.length) f.result.summary = await applySets(f.name, sets);
        if (st.mirror && it.slot >= 0) {
          for (const other of st.files.values()) {
            if (other.name === f.name || !other.result.roundTrip) continue;
            const twin = other.result.summary.equipment
              .find((e) => e.slot === it.slot && e.globalId === it.globalId);
            if (!twin) continue;
            const osets = await resolve(other.name, twin);
            if (osets.length) other.result.summary = await applySets(other.name, osets);
          }
        }
        const sel = st.selected?.handle;
        const s = f.result.summary;
        st.selected = sel
          ? [...s.equipment, ...s.inventory, ...s.chest].find((i) => i.handle === sel) ?? null
          : null;
        render();
      }

      function bindTooltipEditing(panel: HTMLElement, it: ItemSummary, f: OpenFile): void {
        const leafVal = (name: string): number =>
          Number(it.leaves.find((l) => l.name === name)?.value) || 0;

        panel.querySelectorAll<HTMLElement>('.tt .tok').forEach((tokEl) => {
          tokEl.addEventListener('click', (ev) => {
            ev.stopPropagation();
            if (tokEl.querySelector('select, input')) return; // already editing
            const [group, a, b] = tokEl.dataset['tok']!.split(':');
            const commit = (edits: [string, number | string][]): void => {
              if (edits.length) void applyTokenEdits(f, it, edits);
            };
            const restore = (): void => renderItemPanel();
            const mount = (ctrl: HTMLElement): void => {
              tokEl.textContent = '';
              tokEl.appendChild(ctrl);
              (ctrl as HTMLInputElement).focus();
              ctrl.addEventListener('keydown', (e) => { if ((e as KeyboardEvent).key === 'Escape') restore(); });
              ctrl.addEventListener('blur', () => setTimeout(restore, 150));
            };
            const select = (options: [string, string][], current: string, onPick: (v: string) => void): void => {
              const s = document.createElement('select');
              s.innerHTML = options
                .map(([v, l]) => `<option value="${esc(v)}" ${v === current ? 'selected' : ''}>${esc(l)}</option>`)
                .join('');
              s.addEventListener('change', () => onPick(s.value));
              mount(s);
            };
            const numberInput = (current: number, onPick: (v: number) => void): void => {
              const i = document.createElement('input');
              i.type = 'number';
              i.step = 'any';
              i.value = String(current);
              i.addEventListener('change', () => onPick(Number(i.value)));
              mount(i);
            };

            if (group === 'q') {
              // Quality stays ≤ 6 — the game's own maximum (higher crashes tooltips).
              select(QUALITY_NAMES.slice(0, 7).map((n, i) => [String(i), n] as [string, string]), String(it.quality),
                (v) => commit([['q', Number(v)]]));
            } else if (group === 'elv') {
              const el = Number(a);
              numberInput(leafVal(ELEMENT_NAMES[el]!), (v) => commit([[`el:${el}`, v]]));
            } else if (group === 'elname') {
              const el = Number(a);
              select(ELEMENT_NAMES.map((n, i) => [String(i), n] as [string, string]), String(el), (v) => {
                const t = Number(v);
                if (t === el) { restore(); return; }
                // Swap the two elemental leaves: moves this line to the new
                // element, preserving any roll the target element already had.
                commit([
                  [`el:${el}`, leafVal(ELEMENT_NAMES[t]!)],
                  [`el:${t}`, leafVal(ELEMENT_NAMES[el]!)],
                ]);
              });
            } else if (group === 'main' || group === 'dot') {
              const i = Number(a);
              const rec = (group === 'main' ? it.main : it.dot)?.[i];
              if (!rec) return;
              if (b === 'n') {
                numberInput(Number(rec['number']) || 0, (v) => commit([[`${group}:${i}:n`, v]]));
              } else if (b === 'el') {
                select(ELEMENT_NAMES.map((n, ix) => [String(ix), n] as [string, string]), String(Number(rec['EL']) || 0),
                  (v) => commit([[`${group}:${i}:el`, Number(v)]]));
              } else {
                const pool = group === 'main' ? affixNames.main : affixNames.dot;
                const opts = Object.entries(pool)
                  .filter(([, s]) => !s.unmapped)
                  .sort((x, y) => Number(x[0]) - Number(y[0]))
                  .map(([idx, s]) =>
                    [idx, `#${idx} ${s.label.replace('{n}', 'X').replace('{el}', 'EL').slice(0, 52)}`] as [string, string]);
                select(opts, String(Number(rec['Index'])), (v) => commit([[`${group}:${i}:idx`, Number(v)]]));
              }
            } else if (group === 'wpsk') {
              const i = Number(a);
              const rec = it.wpsk?.[i];
              if (!rec) return;
              if (b === 'n') {
                numberInput(Number(rec['Number']) || 0, (v) => commit([[`wpsk:${i}:n`, v]]));
              } else {
                let dl = panel.querySelector<HTMLDataListElement>('#skill-names');
                if (!dl) {
                  dl = document.createElement('datalist');
                  dl.id = 'skill-names';
                  const nameCol = cat.skills.col('IndexName');
                  dl.innerHTML = cat.skills.rows.map((r) => `<option value="${esc(r[nameCol])}">`).join('');
                  panel.appendChild(dl);
                }
                const inp = document.createElement('input');
                inp.type = 'text';
                inp.setAttribute('list', 'skill-names');
                inp.value = String(rec['IndexName'] ?? '');
                inp.addEventListener('change', () => {
                  if (inp.value) commit([[`wpsk:${i}:skill`, inp.value]]);
                });
                mount(inp);
              }
            }
          });
        });
      }

      function renderItemPanel(): void {
        const panel = host.querySelector<HTMLElement>('#item-panel');
        if (!panel) return;
        const it = st.selected;
        const f = active();
        if (!it || !f) {
          panel.innerHTML = `<p class="hint">Select an item — its in-game tooltip renders here.</p>`;
          return;
        }
        const info = itemInfo(it);
        const editable = f.result.roundTrip;
        const tooltipEditable = editable && it.kind === 'weapon';
        panel.innerHTML = `
          ${R.saveTooltip(it, info, f.result.summary.equipment, tooltipEditable)}
          ${tooltipEditable ? '<p class="dim tt-hint">Click any value, element, affix, skill or the quality above to change it.</p>' : ''}
          <section class="dcard"><h4>Edit fields</h4>
            <div id="item-fields">${it.leaves.map((l) => fieldRow(l, editable)).join('')}</div>
            <button id="deep" class="ghost">Show all fields (affixes, sockets, procs…)</button>
            <div id="item-deep"></div>
          </section>`;
        if (tooltipEditable) bindTooltipEditing(panel, it, f);
        bindInputs(panel.querySelector<HTMLElement>('#item-fields')!);
        panel.querySelector('#deep')!.addEventListener('click', async () => {
          const leaves = await itemDetail(f.name, it.handle);
          const deep = panel.querySelector<HTMLElement>('#item-deep')!;
          deep.innerHTML = leaves.map((l) => fieldRow(l, editable)).join('');
          bindInputs(deep);
          (panel.querySelector('#deep') as HTMLElement).hidden = true;
        });
      }

      function renderPendingBar(): void {
        const bar = host.querySelector<HTMLElement>('#pending');
        if (!bar) return;
        bar.hidden = st.pending.size === 0;
        const n = host.querySelector<HTMLElement>('#pending-n');
        if (n) n.textContent = `${st.pending.size} pending change${st.pending.size === 1 ? '' : 's'}`;
        const scope = host.querySelector<HTMLElement>('#pending-scope');
        if (scope) {
          scope.textContent = st.files.size > 1
            ? (st.mirror ? `— will be applied to all ${st.files.size} files` : `— ${st.active ?? ''} only`)
            : '';
        }
      }

      function renderDropzone(): void {
        host.innerHTML = `
          <div class="ed-drop" id="drop">
            <h2>Drop all <u>three</u> save files here</h2>
            <p class="file-chips">
              <code>slot_1.sav</code><code>slot_1_auto.sav</code><code>slot_1_exit.sav</code>
            </p>
            <p>from <code>%USERPROFILE%\\AppData\\LocalLow\\OO Cat\\Shadow Dungeon\\</code><br>
            <span class="dim">(select all three at once — Ctrl+click or Ctrl+A in the file picker)</span></p>
            <p class="dim">The game keeps three copies of your character and silently falls back to the
            <code>_auto</code>/<code>_exit</code> backups, so the editor recomposes your edits into every
            file and you download all three back. Close the game before editing. Nothing is uploaded —
            everything happens in your browser.</p>
            <label class="big-btn">Choose files<input type="file" accept=".sav" multiple hidden></label>
          </div>`;
        const drop = host.querySelector<HTMLElement>('#drop')!;
        drop.querySelector('input')!.addEventListener('change', (e) =>
          void loadFiles((e.target as HTMLInputElement).files!));
        drop.addEventListener('dragover', (e) => { e.preventDefault(); drop.classList.add('over'); });
        drop.addEventListener('dragleave', () => drop.classList.remove('over'));
        drop.addEventListener('drop', (e) => {
          e.preventDefault();
          drop.classList.remove('over');
          if (e.dataTransfer) void loadFiles([...e.dataTransfer.files]);
        });
      }

      render();
      return () => {};
    },
  };
}

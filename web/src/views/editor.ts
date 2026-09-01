import type { View } from '../router';
import { applySets, encodeSave, itemDetail, parseSave, type ParseResult } from '../worker/client';
import type { ItemSummary, Leaf, SaveSummary, SetOp } from '../worker/protocol';
import { loadCatalog, ELEMENT_NAMES, QUALITY_NAMES, SLOT_NAMES } from '../data/catalog';
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

interface State {
  files: Map<string, OpenFile>;
  active: string | null;
  container: 'inventory' | 'chest';
  page: number;
  selected: ItemSummary | null;
  pending: Map<string, SetOp>;
  /** Recompose mode: apply edits to every loaded file, not just the active one. */
  mirror: boolean;
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
                <span id="pages"></span>
              </div>
              <div class="ed-grid" id="grid" style="width:${GRID_W * CELL}px;height:${GRID_H * CELL}px"></div>
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
        renderGrid(s);
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

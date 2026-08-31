import type { View } from '../router';
import { applySets, encodeSave, itemDetail, parseSave, type ParseResult } from '../worker/client';
import type { ItemSummary, Leaf, Rec, SaveSummary, SetOp } from '../worker/protocol';
import { loadCatalog, QUALITY_NAMES, SLOT_NAMES, ELEMENT_NAMES } from '../data/catalog';
import { loadJSON } from '../data/coltable';

const BASE = import.meta.env.BASE_URL;
const CELL = 32; // px per inventory grid cell (game uses 60)
const GRID_W = 15;
const GRID_H = 17;

interface AffixEntry { label: string; percent?: boolean; bool?: boolean }
interface AffixNames {
  main: Record<string, AffixEntry>;
  dot: Record<string, AffixEntry>;
  aocao: Record<string, string>;
}

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
}

function esc(s: unknown): string {
  return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
}

function fmt(n: number): string {
  if (!Number.isFinite(n)) return String(n);
  return Math.abs(n) >= 10 || Number.isInteger(n) ? String(Math.round(n)) : n.toFixed(1);
}

// Elemental value meaning depends on the equipment slot (see docs/save-data-model.md).
function elementSuffix(charType: number): string {
  if (charType === 0 || charType === 7 || charType === 9) return 'damage';
  if (charType === 1 || charType === 8) return 'penetration';
  return 'resistance';
}

export async function editorView(): Promise<View> {
  const cat = await loadCatalog();
  const affixNames = await loadJSON<AffixNames>('affix-names.json')
    .catch(() => ({ main: {}, dot: {}, aocao: {} } as AffixNames));
  const gemsById = new Map(cat.gems.map((g) => [g['GlobalID'] as number, g]));
  const useById = new Map(cat.useitems.map((u) => [u['GlobalID'] as number, u]));

  const wc = {
    iconType: cat.weapons.col('IconType'), icon: cat.weapons.col('Icon'),
    sizeX: cat.weapons.col('SizeX'), sizeY: cat.weapons.col('SizeY'),
    name: cat.weapons.col('ItemName'),
  };

  function itemInfo(it: ItemSummary): { name: string; icon: string; w: number; h: number } {
    if (it.kind === 'weapon') {
      const i = cat.weaponById.get(it.globalId);
      if (i !== undefined) {
        const r = cat.weapons.rows[i]!;
        const sprite = cat.icons.weaponIconTypes[r[wc.iconType] as number]?.sprites[r[wc.icon] as number];
        return {
          name: String(r[wc.name]),
          icon: sprite ? cat.icons.sprites[sprite]!.path : '',
          w: (r[wc.sizeX] as number) || 1,
          h: (r[wc.sizeY] as number) || 1,
        };
      }
      return { name: `Weapon #${it.globalId}`, icon: '', w: 1, h: 1 };
    }
    if (it.kind === 'gem') {
      const g = gemsById.get(it.globalId);
      // Rune-type gems use dedicated sprites, not their Icon column (docs/icons.md).
      const useType = g ? Number(g['UseType']) : 0;
      let sprite: string | undefined;
      if (useType === 3) {
        const el = it.leaves.find((l) => l.name === 'EL')?.value;
        sprite = cat.icons.special.skillRuneByElement[Number(el) || 0];
      } else if (useType === 4) {
        sprite = cat.icons.special.spcRune;
      } else if (useType === 5) {
        sprite = cat.icons.special.baseRune;
      } else if (g) {
        sprite = cat.icons.gemIcons.sprites[g['Icon'] as number];
      }
      return {
        name: g ? String(g['ItemName']) : `Gem #${it.globalId}`,
        icon: sprite ? cat.icons.sprites[sprite]!.path : '', w: 1, h: 1,
      };
    }
    const u = useById.get(it.globalId);
    const sprite = u ? cat.icons.useItemIcons.sprites[u['Icon'] as number] : undefined;
    return {
      name: u ? String(u['ItemName']) : `Item #${it.globalId}`,
      icon: sprite ? cat.icons.sprites[sprite]!.path : '', w: 1, h: 1,
    };
  }

  function affixLine(pool: 'main' | 'dot', rec: Rec): string {
    const index = Number(rec['Index']);
    const el = Number(rec['EL']) || 0;
    const n = Number(rec['number']) || 0;
    const spec = affixNames[pool][String(index)];
    if (spec) {
      if (spec.bool) return esc(spec.label.replace('{el}', ELEMENT_NAMES[el] ?? ''));
      const v = fmt(n) + (spec.percent ? '%' : '');
      return esc(spec.label.replace('{n}', v).replace('{el}', ELEMENT_NAMES[el] ?? ''));
    }
    return `+${fmt(n)} <span class="dim">(#${index}${el ? ' ' + (ELEMENT_NAMES[el] ?? '') : ''})</span>`;
  }

  return {
    mount(container) {
      const st: State = {
        files: new Map(), active: null, container: 'inventory', page: 0,
        selected: null, pending: new Map(),
      };

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
        st.selected = null;
        st.pending.clear();
        render();
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
        const summary = await applySets(f.name, [...st.pending.values()]);
        f.result.summary = summary;
        const sel = st.selected?.handle;
        st.pending.clear();
        st.selected = sel
          ? [...summary.equipment, ...summary.inventory, ...summary.chest].find((i) => i.handle === sel) ?? null
          : null;
        render();
      }

      async function download(): Promise<void> {
        const f = active();
        if (!f) return;
        if (st.pending.size && confirm(`Apply ${st.pending.size} pending change(s) before downloading?`)) {
          await commit();
        }
        const buf = await encodeSave(f.name);
        const a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([buf], { type: 'application/octet-stream' }));
        a.download = f.name;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 5000);
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
              <button id="dl" ${f.result.roundTrip ? '' : 'disabled title="round-trip check failed — editing disabled"'}>Download</button>
            </div>
          </div>
          ${f.result.roundTrip ? '' : `<p class="ed-warn">⚠ This file did not re-encode byte-identically (first difference at byte ${f.result.firstDiff}). Editing is disabled to protect your save — please report this on GitHub.</p>`}
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
            <button id="apply">Apply</button>
            <button id="discard">Discard</button>
          </div>`;

        host.querySelectorAll<HTMLButtonElement>('.ed-tab').forEach((b) =>
          b.addEventListener('click', () => { st.active = b.dataset['file']!; st.selected = null; st.pending.clear(); render(); }));
        host.querySelector<HTMLInputElement>('.ed-add input')!
          .addEventListener('change', (e) => void loadFiles((e.target as HTMLInputElement).files!));
        host.querySelector('#dl')!.addEventListener('click', () => void download());
        host.querySelectorAll<HTMLButtonElement>('.seg').forEach((b) =>
          b.addEventListener('click', () => { st.container = b.dataset['cont'] as State['container']; st.page = 0; render(); }));
        host.querySelector('#apply')?.addEventListener('click', () => void commit());
        host.querySelector('#discard')?.addEventListener('click', () => { st.pending.clear(); render(); });

        renderChar(s, f.result.roundTrip);
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

      // Paper-doll arranged like the in-game character screen.
      function renderDoll(s: SaveSummary): void {
        const el = host.querySelector<HTMLElement>('#equip')!;
        const bySlot = new Map(s.equipment.map((it) => [it.slot, it]));
        el.innerHTML = Array.from({ length: 10 }, (_, slot) => {
          const it = bySlot.get(slot);
          if (!it) return `<div class="eq-slot empty" style="grid-area:s${slot}"><span>${SLOT_NAMES[slot]}</span></div>`;
          const info = itemInfo(it);
          return `<button class="eq-slot ${st.selected?.handle === it.handle ? 'sel' : ''}" data-h="${it.handle}"
            style="grid-area:s${slot};--quality:var(--q${it.quality})" title="${esc(info.name)}">
            ${info.icon ? `<img src="${BASE + info.icon}" alt="" loading="lazy">` : ''}
            <span>${SLOT_NAMES[slot]}</span>
          </button>`;
        }).join('');
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

      // ---- in-game tooltip rendered from the SAVE's rolled values ----
      function saveTooltip(it: ItemSummary, info: { name: string; icon: string }, s: SaveSummary): string {
        const leaves = new Map(it.leaves.map((l) => [l.name, l.value]));
        const lines: string[] = [];

        if (it.kind === 'weapon') {
          const suffix = elementSuffix(it.charType);
          ELEMENT_NAMES.forEach((elName, el) => {
            const v = Number(leaves.get(elName)) || 0;
            if (v) lines.push(`<p class="tt-line el-${el}">+${fmt(v)}% ${elName} ${suffix}</p>`);
          });
          for (const m of it.main ?? []) lines.push(`<p class="tt-line mod">${affixLine('main', m)}</p>`);
          for (const d of it.dot ?? []) lines.push(`<p class="tt-line el-${Number(d['EL']) || 0}">${affixLine('dot', d)}</p>`);
          for (const sk of it.wpsk ?? []) {
            if (sk['IndexName']) lines.push(`<p class="tt-line skill">+${fmt(Number(sk['Number']))} to ${esc(sk['IndexName'])}</p>`);
          }
          const sockets = it.aocao ?? [];
          if (sockets.length) {
            lines.push(`<p class="tt-line sockets">${sockets.map((a) => {
              const label = affixNames.aocao[String(a['Type'])] ?? `socket type ${a['Type']}`;
              return `<span class="socket" title="${esc(label)}">◆</span>`;
            }).join(' ')}</p>`);
          }
          const setIndex = Number(leaves.get('Set_Index')) || 0;
          if (setIndex) {
            const set = cat.setById.get(setIndex);
            if (set) {
              const equippedCount = s.equipment.filter((e) =>
                Number(e.leaves.find((l) => l.name === 'Set_Index')?.value) === setIndex).length;
              lines.push(`<div class="tt-rule"></div><p class="tt-line set">${esc(set['SetName'])} (${equippedCount} equipped)</p>`);
              for (const gid of (set['pieces'] as number[] | undefined) ?? []) {
                const i = cat.weaponById.get(gid);
                if (i === undefined) continue;
                const pieceName = String(cat.weapons.rows[i]![wc.name]);
                const worn = s.equipment.some((e) => e.globalId === gid);
                lines.push(`<p class="tt-line set-piece ${worn ? 'worn' : 'unworn'}">${esc(pieceName)}</p>`);
              }
              // Tier requirement is positional: bonuses[i] activates at i+2 pieces
              // (Set_DT.Lit[count-2], see docs/save-data-model.md).
              const bonuses = (set['bonuses'] as Record<string, unknown>[] | undefined) ?? [];
              bonuses.forEach((b, i) => {
                if (!b['SkN'] && !b['Index']) return;
                const need = i + 2;
                const on = equippedCount >= need;
                lines.push(`<p class="tt-line set-bonus ${on ? 'worn' : 'unworn'}">(${need}) ${esc(b['SkN'] || `#${b['Index']}`)} +${esc(b['NB'])}</p>`);
              });
            }
          }
        } else {
          const g = it.kind === 'gem' ? gemsById.get(it.globalId) : useById.get(it.globalId);
          const skname = leaves.get('SKname');
          if (skname) lines.push(`<p class="tt-line skill">Rune of ${esc(skname)}</p>`);
          if (g?.['Bstype'] && g['Number']) lines.push(`<p class="tt-line mod">+${esc(g['Number'])} ${esc(g['Bstype'])}</p>`);
          else if (g?.['Number']) lines.push(`<p class="tt-line">Effect value +${esc(g['Number'])}</p>`);
          if (it.stack) lines.push(`<p class="tt-line dim">Stack: ${it.stack}${g?.['MstackSize'] ? ` / ${g['MstackSize']}` : ''}</p>`);
        }

        const place = it.page >= 0
          ? `page ${it.page + 1} · (${it.gridX},${it.gridY})`
          : it.slot >= 0 ? `equipped · ${SLOT_NAMES[it.slot]}` : 'chest';
        return `
          <div class="tt" style="--quality: var(--q${it.quality})">
            <i class="tt-rivet tl"></i><i class="tt-rivet tr"></i><i class="tt-rivet bl"></i><i class="tt-rivet br"></i>
            <h3 class="tt-name">${esc(info.name)}</h3>
            <p class="tt-kind">${QUALITY_NAMES[it.quality] ?? `Q${it.quality}`} ${it.kind === 'weapon' ? SLOT_NAMES[it.charType] ?? '' : it.kind}</p>
            ${info.icon ? `<img class="tt-icon" src="${BASE + info.icon}" alt="" decoding="async">` : ''}
            <div class="tt-rule"></div>
            ${lines.join('')}
            <div class="tt-rule"></div>
            <p class="tt-foot">#${it.globalId} · ${place}</p>
          </div>`;
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
        panel.innerHTML = `
          ${saveTooltip(it, info, f.result.summary)}
          <section class="dcard"><h4>Edit fields</h4>
            <div id="item-fields">${it.leaves.map((l) => fieldRow(l, editable)).join('')}</div>
            <button id="deep" class="ghost">Show all fields (affixes, sockets, procs…)</button>
            <div id="item-deep"></div>
          </section>`;
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
      }

      function renderDropzone(): void {
        host.innerHTML = `
          <div class="ed-drop" id="drop">
            <h2>Drop your save files here</h2>
            <p>slot_1.sav, slot_1_auto.sav, slot_1_exit.sav — from<br>
            <code>%USERPROFILE%\\AppData\\LocalLow\\OO Cat\\Shadow Dungeon\\</code></p>
            <p class="dim">Load all three slot files and apply the same edits to each — the game silently
            falls back to older backups. Close the game before editing. Nothing is uploaded.</p>
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

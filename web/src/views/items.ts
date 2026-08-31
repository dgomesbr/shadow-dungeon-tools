import type { View } from '../router';
import { VList } from '../ui/vlist';
import {
  loadCatalog, rowObj, CATEGORY_LABELS, QUALITY_NAMES, SLOT_NAMES, ELEMENT_NAMES,
  type Catalog, type CatalogEntry, type Category,
} from '../data/catalog';

const BASE = import.meta.env.BASE_URL;
const CATS = Object.keys(CATEGORY_LABELS) as Category[];

interface Filters {
  cat: Category | '';
  text: string;
  quality: number; // -1 = all
  slot: number;    // -1 = all
  sort: 'name' | 'quality' | 'level';
}

export async function itemsView(): Promise<View> {
  const cat = await loadCatalog();

  return {
    mount(container) {
      container.innerHTML = `
        <div class="items-layout">
          <div class="items-main">
            <div class="toolbar">
              <input id="q" type="search" placeholder="Search ${cat.entries.length.toLocaleString()} items…" autocomplete="off" />
              <select id="f-cat"><option value="">All categories</option>${CATS.map((c) => `<option value="${c}">${CATEGORY_LABELS[c]}</option>`).join('')}</select>
              <select id="f-quality"><option value="-1">Any quality</option>${QUALITY_NAMES.map((n, i) => `<option value="${i}">${n}</option>`).join('')}</select>
              <select id="f-slot"><option value="-1">Any slot</option>${[...new Set(SLOT_NAMES)].map((n) => `<option value="${SLOT_NAMES.indexOf(n)}">${n}</option>`).join('')}</select>
              <select id="f-sort"><option value="quality">Sort: Quality</option><option value="name">Sort: Name</option><option value="level">Sort: Level</option></select>
              <span id="count" class="count"></span>
            </div>
            <div id="list-host" class="list-host"></div>
          </div>
          <aside id="detail" class="detail"><p class="hint">Select an item to inspect it.</p></aside>
        </div>`;

      const f: Filters = { cat: '', text: '', quality: -1, slot: -1, sort: 'quality' };
      const countEl = container.querySelector<HTMLElement>('#count')!;
      const detail = container.querySelector<HTMLElement>('#detail')!;

      const vlist = new VList<CatalogEntry>({
        rowHeight: 56,
        createRow() {
          const el = document.createElement('div');
          el.className = 'item-row';
          el.innerHTML = `<span class="cell-icon"><img loading="lazy" decoding="async" alt=""></span>
            <span class="cell-name"></span><span class="cell-cat"></span>
            <span class="cell-q"></span><span class="cell-lvl"></span>`;
          return el;
        },
        renderRow(el, it) {
          const img = el.firstElementChild!.firstElementChild as HTMLImageElement;
          const src = it.icon ? BASE + it.icon : '';
          if (img.dataset['p'] !== src) {
            img.dataset['p'] = src;
            img.src = src;
            img.style.visibility = src ? 'visible' : 'hidden';
          }
          const name = el.children[1] as HTMLElement;
          name.textContent = it.name;
          name.className = `cell-name q${it.quality}`;
          (el.children[2] as HTMLElement).textContent =
            it.slot >= 0 ? SLOT_NAMES[it.slot]! : CATEGORY_LABELS[it.cat];
          (el.children[3] as HTMLElement).textContent = QUALITY_NAMES[it.quality] ?? `Q${it.quality}`;
          (el.children[4] as HTMLElement).textContent = it.level ? `Lv ${it.level}` : '';
        },
        onRowClick(it) {
          renderDetail(detail, it, cat);
        },
      });
      container.querySelector('#list-host')!.appendChild(vlist.root);

      // Filtering: single synchronous pass over the flat entries array —
      // ~2 ms on the full catalog, so no debounce/deferral needed.
      const apply = () => {
        const t0 = performance.now();
        const out: CatalogEntry[] = [];
        const { cat: fc, text, quality, slot } = f;
        for (const e of cat.entries) {
          if (fc && e.cat !== fc) continue;
          if (quality >= 0 && e.quality !== quality) continue;
          if (slot >= 0 && e.slot !== slot) continue;
          if (text && !e.search.includes(text)) continue;
          out.push(e);
        }
        if (f.sort === 'name') out.sort((a, b) => (a.search < b.search ? -1 : 1));
        else if (f.sort === 'level') out.sort((a, b) => b.level - a.level || b.quality - a.quality);
        else out.sort((a, b) => b.quality - a.quality || a.level - b.level);
        vlist.setItems(out);
        countEl.textContent = `${out.length.toLocaleString()} items · ${(performance.now() - t0).toFixed(1)} ms`;
      };

      const on = <T extends HTMLElement>(sel: string, ev: string, fn: (el: T) => void) => {
        const el = container.querySelector<T>(sel)!;
        el.addEventListener(ev, () => { fn(el); apply(); });
      };
      on<HTMLInputElement>('#q', 'input', (el) => (f.text = el.value.trim().toLowerCase()));
      on<HTMLSelectElement>('#f-cat', 'change', (el) => (f.cat = el.value as Filters['cat']));
      on<HTMLSelectElement>('#f-quality', 'change', (el) => (f.quality = Number(el.value)));
      on<HTMLSelectElement>('#f-slot', 'change', (el) => (f.slot = Number(el.value)));
      on<HTMLSelectElement>('#f-sort', 'change', (el) => (f.sort = el.value as Filters['sort']));
      apply();

      return () => {};
    },
  };
}

// ---- detail panel ----------------------------------------------------------

function esc(s: unknown): string {
  return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
}

function statRows(pairs: [string, unknown][]): string {
  return pairs
    .filter(([, v]) => v !== undefined && v !== null && v !== 0 && v !== '')
    .map(([k, v]) => `<div class="stat"><span>${esc(k)}</span><b>${esc(v)}</b></div>`)
    .join('');
}

function affixList(cat: Catalog, pool: string, id: unknown): string {
  const a = cat.affixByPoolId.get(`${pool}:${id}`);
  if (!a || !a.entries.length) return '';
  const lines = a.entries.map((e) => {
    if (Array.isArray(e)) {
      const [index, el, nb] = e as [number, number, number];
      return `<li>#${index} ${ELEMENT_NAMES[el] ?? ''} +${nb}</li>`;
    }
    const o = e as Record<string, unknown>;
    return `<li>${esc(o['SkN'])} #${esc(o['Inx'])} +${esc(o['NB'])}</li>`;
  });
  return `<h4>${pool === 'main' ? 'Main affix pool' : pool === 'dot' ? 'DOT affix pool' : 'Skill affix pool'}</h4><ul class="affixes">${lines.join('')}</ul>`;
}

function renderDetail(host: HTMLElement, it: CatalogEntry, cat: Catalog): void {
  const icon = it.icon
    ? `<img class="detail-icon" src="${BASE + it.icon}" width="${it.iconW}" height="${it.iconH}" alt="">`
    : '';
  let body = '';

  const src = it.src as { table?: import('../data/coltable').Table; row?: number };
  if (src.table !== undefined && src.row !== undefined) {
    const w = rowObj(src.table, src.row);
    body += statRows([
      ['Slot', SLOT_NAMES[it.slot]], ['Drop level', w['DropLevelStart']],
      ['Damage', w['DMG']], ['Health', w['Heal']], ['Mana', w['Mana']], ['Elemental', w['EL']],
      ['Gem sockets', w['CurAocaoCount']],
      ['Size', `${w['SizeX']}×${w['SizeY']}`],
    ]);
    const sockets = (w['Sockets'] as [string, number][] | undefined) ?? [];
    if (sockets.length) {
      body += `<h4>Skill sockets</h4><ul class="affixes">${sockets
        .map(([sk, pts]) => `<li>${esc(sk)} +${pts}</li>`).join('')}</ul>`;
    }
    const fixed = (w['Affixes'] as [number, number, number][] | undefined) ?? [];
    if (fixed.length) {
      body += `<h4>Fixed affixes</h4><ul class="affixes">${fixed
        .map(([i, el, nb]) => `<li>#${i} ${ELEMENT_NAMES[el] ?? ''} +${nb}</li>`).join('')}</ul>`;
    }
    body += affixList(cat, 'main', w['MainID']);
    body += affixList(cat, 'dot', w['DotID']);
    body += affixList(cat, 'sk', w['SkID']);
    const spc = w['SPC'] as number;
    if (spc) {
      const p = cat.procById.get(spc);
      if (p !== undefined) body += statRows([['Proc', cat.procs.rows[p]![cat.procs.col('name')]]]);
    }
    if (it.setId) body += setBlock(cat, it.setId);
  } else if (it.cat === 'set') {
    body += setBlock(cat, it.id);
  } else {
    const o = it.src as Record<string, unknown>;
    body += statRows(Object.entries(o).filter(([k]) => !['ItemName', 'Icon'].includes(k)) as [string, unknown][]);
  }

  host.innerHTML = `
    <div class="detail-head">${icon}<div><h3 class="q${it.quality}">${esc(it.name)}</h3>
    <p class="sub">${QUALITY_NAMES[it.quality] ?? `Q${it.quality}`} · ${CATEGORY_LABELS[it.cat]} · #${it.id}</p></div></div>
    ${body}`;
}

function setBlock(cat: Catalog, setId: number): string {
  const s = cat.setById.get(setId);
  if (!s) return '';
  let html = `<h4>Set: ${esc(s['SetName'])}</h4>`;
  const pieces = (s['pieces'] as number[] | undefined) ?? [];
  if (pieces.length) {
    const nameC = cat.weapons.col('ItemName');
    const qC = cat.weapons.col('Quality');
    html += `<ul class="affixes">${pieces.map((gid) => {
      const i = cat.weaponById.get(gid);
      if (i === undefined) return '';
      const r = cat.weapons.rows[i]!;
      return `<li class="q${r[qC]}">${esc(r[nameC])}</li>`;
    }).join('')}</ul>`;
  }
  const bonuses = (s['bonuses'] as Record<string, unknown>[] | undefined) ?? [];
  html += bonuses.filter((b) => b['MTP']).map((b) =>
    `<div class="stat"><span>${esc(b['MTP'])} pieces</span><b>${esc(b['SkN'] || `#${b['Index']}`)} +${esc(b['NB'])}</b></div>`,
  ).join('');
  return html;
}

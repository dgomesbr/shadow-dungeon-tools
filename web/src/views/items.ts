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
  return `<ul class="affixes">${lines.join('')}</ul>`;
}

function tooltipFrame(it: CatalogEntry, lines: string[], footer: string): string {
  const icon = it.icon
    ? `<img class="tt-icon" src="${BASE + it.icon}" width="${it.iconW}" height="${it.iconH}" alt="" decoding="async">`
    : '';
  return `
    <div class="tt" style="--quality: var(--q${it.quality})">
      <i class="tt-rivet tl"></i><i class="tt-rivet tr"></i><i class="tt-rivet bl"></i><i class="tt-rivet br"></i>
      <h3 class="tt-name">${esc(it.name)}</h3>
      <p class="tt-kind">${QUALITY_NAMES[it.quality] ?? `Q${it.quality}`} ${it.slot >= 0 ? SLOT_NAMES[it.slot] : CATEGORY_LABELS[it.cat]}</p>
      ${icon}
      <div class="tt-rule"></div>
      ${lines.join('')}
      <div class="tt-rule"></div>
      <p class="tt-foot">${footer}</p>
    </div>`;
}

function ttLine(text: string, cls = ''): string {
  return `<p class="tt-line${cls ? ' ' + cls : ''}">${text}</p>`;
}

function card(title: string, inner: string): string {
  return inner ? `<section class="dcard"><h4>${title}</h4>${inner}</section>` : '';
}

function renderDetail(host: HTMLElement, it: CatalogEntry, cat: Catalog): void {
  const src = it.src as { table?: import('../data/coltable').Table; row?: number };
  const lines: string[] = [];
  const cards: string[] = [];

  if (src.table !== undefined && src.row !== undefined) {
    const w = rowObj(src.table, src.row);
    if (w['DMG']) lines.push(ttLine(`${esc(w['DMG'])} damage`));
    if (w['Heal']) lines.push(ttLine(`+${esc(w['Heal'])} health`));
    if (w['Mana']) lines.push(ttLine(`+${esc(w['Mana'])} mana`));
    if (w['EL']) lines.push(ttLine(`+${esc(w['EL'])}% elemental`, 'el'));
    const fixed = (w['Affixes'] as [number, number, number][] | undefined) ?? [];
    for (const [i, el, nb] of fixed) {
      lines.push(ttLine(`+${nb} ${ELEMENT_NAMES[el] ?? ''} <span class="dim">(affix #${i})</span>`, 'mod'));
    }
    const sockets = (w['Sockets'] as [string, number][] | undefined) ?? [];
    for (const [sk, pts] of sockets) lines.push(ttLine(`+${pts} to ${esc(sk)}`, 'skill'));
    const aocao = Number(w['CurAocaoCount']) || 0;
    if (aocao) lines.push(ttLine(`◆ ${aocao} socket slot${aocao > 1 ? 's' : ''}`, 'sockets'));
    const spc = w['SPC'] as number;
    if (spc) {
      if (cat.procById && cat.procs) {
        const p = cat.procById.get(spc);
        if (p !== undefined) lines.push(ttLine(`Proc: ${esc(cat.procs.rows[p]![cat.procs.col('name')])}`, 'proc'));
      } else {
        // procs.json loads on demand; re-render this panel when it arrives
        lines.push(ttLine('Proc: …', 'proc'));
        void cat.loadProcs().then(() => renderDetail(host, it, cat));
      }
    }

    cards.push(card('Summary', statRows([
      ['GlobalID', w['GlobalID']], ['Drop level', w['DropLevelStart']],
      ['Grid size', `${w['SizeX']}×${w['SizeY']}`], ['Class group', w['PLtype']],
      ['Weapon type', w['WeaponType']],
    ])));
    cards.push(card('Fixed affixes', fixed.length
      ? `<ul class="affixes">${fixed.map(([i, el, nb]) => `<li>#${i} ${ELEMENT_NAMES[el] ?? ''} +${nb}</li>`).join('')}</ul>` : ''));
    cards.push(card('Main affix pool', affixList(cat, 'main', w['MainID'])));
    cards.push(card('DOT affix pool', affixList(cat, 'dot', w['DotID'])));
    cards.push(card('Skill affix pool', affixList(cat, 'sk', w['SkID'])));
    if (it.setId) cards.push(card('Set', setBlock(cat, it.setId)));
  } else if (it.cat === 'set') {
    const pieces = ((it.src as Record<string, unknown>)['pieces'] as number[]) ?? [];
    lines.push(ttLine(`${pieces.length}-piece armor set`, 'dim'));
    cards.push(card('Set', setBlock(cat, it.id)));
  } else {
    const o = it.src as Record<string, unknown>;
    if (o['Number']) lines.push(ttLine(`Effect value +${esc(o['Number'])}`));
    if (o['Bstype']) lines.push(ttLine(`${esc(o['Bstype'])}`, 'dim'));
    if (o['MstackSize']) lines.push(ttLine(`Stacks to ${esc(o['MstackSize'])}`, 'dim'));
    cards.push(card('Summary', statRows(
      Object.entries(o).filter(([k]) => !['ItemName', 'Icon'].includes(k)) as [string, unknown][],
    )));
  }

  const footer = it.level ? `Drop level ${it.level} · #${it.id}` : `#${it.id}`;
  host.innerHTML = tooltipFrame(it, lines, footer) + cards.join('');
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

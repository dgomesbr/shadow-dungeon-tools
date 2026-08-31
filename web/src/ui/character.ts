// Shared character-view rendering: item visuals, game tooltips from save
// values, and the paper-doll. Used by the save editor and the build-share
// viewer (#/build), which feeds it synthetic ItemSummary objects.
import type { ItemSummary, Rec } from '../worker/protocol';
import {
  ELEMENT_NAMES, QUALITY_NAMES, SLOT_NAMES,
  weaponIconPath, gemIconPath, useIconPath, RUNE_ICONS, type Catalog,
} from '../data/catalog';
import { loadJSON } from '../data/coltable';

export interface AffixEntry {
  label: string;
  percent?: boolean;
  bool?: boolean;
  el?: boolean;
  format?: string;
  elVariants?: string[];
  unmapped?: boolean;
}
export interface AffixNames {
  main: Record<string, AffixEntry>;
  dot: Record<string, AffixEntry>;
  sk: Record<string, AffixEntry>;
  aocao: Record<string, string>;
}

let affixCache: Promise<AffixNames> | null = null;
export function loadAffixNames(): Promise<AffixNames> {
  return (affixCache ??= loadJSON<AffixNames>('affix-names.json')
    .catch(() => ({ main: {}, dot: {}, sk: {}, aocao: {} } as AffixNames)));
}

export function esc(s: unknown): string {
  return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
}

export function fmt(n: number): string {
  if (!Number.isFinite(n)) return String(n);
  return Math.abs(n) >= 10 || Number.isInteger(n) ? String(Math.round(n)) : n.toFixed(1);
}

// Elemental value meaning depends on the equipment slot (see docs/save-data-model.md).
export function elementSuffix(charType: number): string {
  if (charType === 0 || charType === 7 || charType === 9) return 'damage';
  if (charType === 1 || charType === 8) return 'penetration';
  return 'resistance';
}

export interface ItemVisual {
  name: string;
  icon: string;
  w: number;
  h: number;
}

export function createCharacterRenderer(cat: Catalog, affixNames: AffixNames) {
  const BASE = import.meta.env.BASE_URL;
  const gemsById = new Map(cat.gems.map((g) => [g['GlobalID'] as number, g]));
  const useById = new Map(cat.useitems.map((u) => [u['GlobalID'] as number, u]));
  const wc = {
    iconType: cat.weapons.col('IconType'), icon: cat.weapons.col('Icon'),
    sizeX: cat.weapons.col('SizeX'), sizeY: cat.weapons.col('SizeY'),
    name: cat.weapons.col('ItemName'), skId: cat.weapons.col('SkID'),
  };

  function itemInfo(it: ItemSummary): ItemVisual {
    if (it.kind === 'weapon') {
      const i = cat.weaponById.get(it.globalId);
      if (i !== undefined) {
        const r = cat.weapons.rows[i]!;
        return {
          name: String(r[wc.name]),
          icon: weaponIconPath(r[wc.iconType] as number, r[wc.icon] as number),
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
      let icon = '';
      if (useType === 3) {
        const el = it.leaves.find((l) => l.name === 'EL')?.value;
        icon = RUNE_ICONS.byElement[Number(el) || 0] ?? RUNE_ICONS.base;
      } else if (useType === 4) {
        icon = RUNE_ICONS.spc;
      } else if (useType === 5) {
        icon = RUNE_ICONS.base;
      } else if (g) {
        icon = gemIconPath(g['Icon'] as number);
      }
      return { name: g ? String(g['ItemName']) : `Gem #${it.globalId}`, icon, w: 1, h: 1 };
    }
    const u = useById.get(it.globalId);
    return {
      name: u ? String(u['ItemName']) : `Item #${it.globalId}`,
      icon: u ? useIconPath(u['Icon'] as number) : '', w: 1, h: 1,
    };
  }

  function fmtBy(n: number, format?: string): string {
    if (format === 'int') return String(Math.floor(n));
    if (format === '0.0') return n.toFixed(1);
    return fmt(n);
  }

  function affixLine(pool: 'main' | 'dot', rec: Rec, skillName?: string): string {
    const index = Number(rec['Index']);
    const el = Number(rec['EL']) || 0;
    const n = Number(rec['number']) || 0;
    // 3xxx/4xxx indexes are skill/companion affixes fed by the SK pool.
    const spec = affixNames[pool][String(index)]
      ?? (index >= 3000 ? affixNames.sk[String(index)] : undefined);
    if (spec && !spec.unmapped) {
      let label = spec.elVariants?.[el] ?? spec.label;
      label = label
        .replace('{el}', ELEMENT_NAMES[el] ?? '')
        .replace(/\{skill\}|\{target\}|\{link\}|\{mode\}/g, skillName ?? 'skill');
      if (spec.bool) return esc(label);
      // Percent labels already carry their % sign after {n}.
      const v = fmtBy(n, spec.format) + (spec.percent && !label.includes('{n}%') ? '%' : '');
      return esc(label.replace('{n}', v));
    }
    return `+${fmt(n)} <span class="dim">(#${index}${el ? ' ' + (ELEMENT_NAMES[el] ?? '') : ''})</span>`;
  }

  /** In-game tooltip rendered from the item's rolled values. `equipment` is
   *  the full equipped list, used for set-piece/bonus activation dimming. */
  function saveTooltip(it: ItemSummary, info: ItemVisual, equipment: ItemSummary[]): string {
    const leaves = new Map(it.leaves.map((l) => [l.name, l.value]));
    const lines: string[] = [];

    if (it.kind === 'weapon') {
      // Resolve {skill} for 3xxx/4xxx affixes from the template's SK pool.
      const skNameFor = (index: number): string | undefined => {
        const ti = cat.weaponById.get(it.globalId);
        if (ti === undefined) return undefined;
        const skId = cat.weapons.rows[ti]![wc.skId];
        const pool = cat.affixByPoolId.get(`sk:${skId}`);
        const e = pool?.entries.find(
          (x) => Number((x as Record<string, unknown>)['Inx']) === index,
        ) as Record<string, unknown> | undefined;
        return (e?.['SkN'] as string) || undefined;
      };
      const suffix = elementSuffix(it.charType);
      ELEMENT_NAMES.forEach((elName, el) => {
        const v = Number(leaves.get(elName)) || 0;
        if (v) lines.push(`<p class="tt-line el-${el}">+${fmt(v)}% ${elName} ${suffix}</p>`);
      });
      for (const m of it.main ?? []) {
        const idx = Number(m['Index']);
        lines.push(`<p class="tt-line mod">${affixLine('main', m, idx >= 3000 ? skNameFor(idx) : undefined)}</p>`);
      }
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
          const equippedCount = equipment.filter((e) =>
            Number(e.leaves.find((l) => l.name === 'Set_Index')?.value) === setIndex).length;
          lines.push(`<div class="tt-rule"></div><p class="tt-line set">${esc(set['SetName'])} (${equippedCount} equipped)</p>`);
          for (const gid of (set['pieces'] as number[] | undefined) ?? []) {
            const i = cat.weaponById.get(gid);
            if (i === undefined) continue;
            const pieceName = String(cat.weapons.rows[i]![wc.name]);
            const worn = equipment.some((e) => e.globalId === gid);
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

  /** Paper-doll arranged like the in-game character screen. Callers bind
   *  clicks on `.eq-slot[data-h]`. */
  function dollHTML(equipment: ItemSummary[], selectedHandle?: string | null): string {
    const bySlot = new Map(equipment.map((it) => [it.slot, it]));
    return Array.from({ length: 10 }, (_, slot) => {
      const it = bySlot.get(slot);
      if (!it) return `<div class="eq-slot empty" style="grid-area:s${slot}"><span>${SLOT_NAMES[slot]}</span></div>`;
      const info = itemInfo(it);
      return `<button class="eq-slot ${selectedHandle === it.handle ? 'sel' : ''}" data-h="${it.handle}"
        style="grid-area:s${slot};--quality:var(--q${it.quality})" title="${esc(info.name)}">
        ${info.icon ? `<img src="${BASE + info.icon}" alt="" loading="lazy">` : ''}
        <span>${SLOT_NAMES[slot]}</span>
      </button>`;
    }).join('');
  }

  return { itemInfo, affixLine, saveTooltip, dollHTML };
}

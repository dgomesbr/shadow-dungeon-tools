import type { View } from '../router';
import { queryParams } from '../router';
import { loadCatalog } from '../data/catalog';
import { createCharacterRenderer, esc, loadAffixNames } from '../ui/character';
import { decodeShare, summaryFromBuild } from '../share/codec';
import type { ItemSummary } from '../worker/protocol';

// Read-only viewer for shared builds (#/build?d=<payload>). The payload lives
// in the URL hash fragment: it never reaches any server, and the whole
// character view is rebuilt client-side from it.
export async function buildView(): Promise<View> {
  const cat = await loadCatalog();
  const affixNames = await loadAffixNames();
  const R = createCharacterRenderer(cat, affixNames);

  return {
    mount(container) {
      const host = document.createElement('div');
      host.className = 'ed';
      container.appendChild(host);

      const payload = queryParams().get('d');
      if (!payload) {
        host.innerHTML = `<div class="ed-drop"><h2>No build in this link</h2>
          <p class="dim">A shared build link looks like <code>#/build?d=…</code>.
          Create one with the <a href="#/editor">Save Editor</a>'s Share button.</p></div>`;
        return () => {};
      }

      void (async () => {
        let summary;
        let drift = false;
        try {
          const build = await decodeShare(payload, cat);
          drift = build.dataDrift ?? false;
          summary = summaryFromBuild(build);
        } catch (e) {
          host.innerHTML = `<div class="ed-drop"><h2>Could not read this build link</h2>
            <p class="dim">${esc(e instanceof Error ? e.message : e)}</p></div>`;
          return;
        }

        const p = new Map(summary.player.map((l) => [l.name, l.value]));
        const tp = new Map(summary.talentPoints.map((l) => [l.name, l.value]));
        let selected: ItemSummary | null =
          summary.equipment.find((it) => it.slot === 0) ?? summary.equipment[0] ?? null;

        host.innerHTML = `
          <p class="ed-note">Shared build — read-only. Nothing was uploaded; this page is rebuilt
            entirely from the link. <a href="#/editor">Open the editor</a> to inspect your own save.</p>
          ${drift ? `<p class="ed-warn">⚠ This link was created with an older data version — skill names may be inaccurate.</p>` : ''}
          <div class="ed-hero">
            <h2>${esc(p.get('PlayerName') || 'Unnamed')}</h2>
            <span class="ed-hero-stat"><b>${esc(p.get('Level') ?? '?')}</b> level</span>
            <span class="ed-hero-stat"><b>${esc(p.get('DFLevel') ?? 0)}</b> divine favor</span>
            <span class="ed-hero-stat"><b>${esc(tp.get('P_Used') ?? 0)} / ${esc(tp.get('P_Base') ?? 0)}</b> points</span>
            <span class="ed-hero-stat"><b>${esc(tp.get('P_Used_DF') ?? 0)}</b> DF points</span>
          </div>
          <div class="ed-cols build-cols">
            <section class="ed-char">
              <h4>Equipment</h4>
              <div class="ed-doll" id="equip"></div>
              <h4>Talents (${summary.talents.length})</h4>
              <div id="talents" class="build-talents"></div>
            </section>
            <aside class="ed-item" id="item-panel"></aside>
          </div>`;

        const doll = host.querySelector<HTMLElement>('#equip')!;
        const panel = host.querySelector<HTMLElement>('#item-panel')!;

        const renderSel = (): void => {
          doll.innerHTML = R.dollHTML(summary.equipment, selected?.handle);
          doll.querySelectorAll<HTMLButtonElement>('.eq-slot[data-h]').forEach((b) =>
            b.addEventListener('click', () => {
              selected = summary.equipment.find((it) => it.handle === b.dataset['h']) ?? null;
              renderSel();
            }));
          panel.innerHTML = selected
            ? R.saveTooltip(selected, R.itemInfo(selected), summary.equipment)
            : `<p class="hint">Select an item.</p>`;
        };
        renderSel();

        host.querySelector<HTMLElement>('#talents')!.innerHTML = summary.talents
          .map((t) => `<div class="frow"><span>${esc(t.name)}</span><b>${t.level}</b></div>`)
          .join('');
      })();

      return () => {};
    },
  };
}

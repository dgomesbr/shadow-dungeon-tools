import type { View } from '../router';
import { loadJSON } from '../data/coltable';

interface Release { date: string; title: string; items: string[] }

function esc(s: unknown): string {
  return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
}

// Simple stroke icons for the three feature cards.
const ICONS = {
  items: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
    <path d="M14.5 17.5 3 6V3h3l11.5 11.5"/><path d="M13 19l6-6"/><path d="M16 16l4 4"/><path d="M19 21l2-2"/>
  </svg>`,
  editor: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
    <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/>
    <path d="M17 21v-8H7v8"/><path d="M7 3v5h8"/>
  </svg>`,
  mods: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
    <path d="M20.5 11H19V7a2 2 0 0 0-2-2h-4V3.5a2.5 2.5 0 0 0-5 0V5H4a2 2 0 0 0-2 2v3.8h1.5a2.7 2.7 0 0 1 0 5.4H2V20a2 2 0 0 0 2 2h3.8v-1.5a2.7 2.7 0 0 1 5.4 0V22H17a2 2 0 0 0 2-2v-4h1.5a2.5 2.5 0 0 0 0-5z"/>
  </svg>`,
};

function releaseBlock(r: Release, open: boolean): string {
  return `<details class="release" ${open ? 'open' : ''}>
    <summary><b>${esc(r.title)}</b><span class="dim">${esc(r.date)}</span></summary>
    <ul>${r.items.map((it) => `<li>${esc(it)}</li>`).join('')}</ul>
  </details>`;
}

export async function homeView(): Promise<View> {
  return {
    mount(container) {
      container.innerHTML = `
        <div class="home">
          <section class="hero">
            <h1>Shadow<span>Dungeon</span>Tools</h1>
            <p class="tagline">Browse every item in the game. Edit your save. Entirely in your browser —
            <b>your files never leave your machine</b>.</p>
          </section>
          <section class="whats-new latest" id="whats-new" hidden>
            <h2>What's new</h2>
            <div id="latest-release"></div>
          </section>
          <section class="home-cards">
            <a class="home-card" href="#/editor">
              <h2><span class="card-ic">${ICONS.editor}</span>Save Game Editor</h2>
              <p>Upload your <code>slot_*.sav</code>, edit character stats, gold,
              items, equipment, skill trees and unlocks, then download the modified
              saves. Byte-exact, verified against the game's own format.</p>
              <span class="cta">Open editor →</span>
            </a>
            <a class="home-card" href="#/items">
              <h2><span class="card-ic">${ICONS.items}</span>Item Library</h2>
              <p>Every weapon, armor piece, accessory, gem, consumable and set —
              searchable, filterable, with game-faithful tooltips and icons.</p>
              <span class="cta">Browse items →</span>
            </a>
            <a class="home-card" href="#/mods">
              <h2><span class="card-ic">${ICONS.mods}</span>Mods</h2>
              <p>Drop-in BepInEx plugins that add an in-game utility window:
              gold transfer, story-progress copy, auto-aim, boss replay and
              one-click Summon All. Download + install guide.</p>
              <span class="cta">Get the mods →</span>
            </a>
          </section>
          <section class="whats-new" id="release-history" hidden>
            <h2>Release history</h2>
            <div id="releases"></div>
          </section>
          <footer class="home-foot">
            Unofficial fan tooling for Shadow Dungeon (OO Cat). Modding patterns inspired by
            Max's Character Utilities plugin from the community Discord.
          </footer>
        </div>`;

      // Release notes load lazily and never block first paint.
      void loadJSON<Release[]>('changelog.json').then((releases) => {
        if (!releases.length) return;
        const latest = container.querySelector<HTMLElement>('#latest-release');
        const latestSection = container.querySelector<HTMLElement>('#whats-new');
        if (latest && latestSection) {
          const r = releases[0]!;
          const PREVIEW = 3;
          const head = r.items.slice(0, PREVIEW);
          const rest = r.items.slice(PREVIEW);
          latest.innerHTML = `<div class="release latest-card">
            <div class="release-title"><b>${esc(r.title)}</b><span class="dim">${esc(r.date)}</span></div>
            <ul>${head.map((it) => `<li>${esc(it)}</li>`).join('')}</ul>
            ${rest.length ? `
              <ul id="latest-more" hidden>${rest.map((it) => `<li>${esc(it)}</li>`).join('')}</ul>
              <button class="link-btn" id="latest-toggle">Click here to view ${rest.length} more…</button>` : ''}
          </div>`;
          latest.querySelector('#latest-toggle')?.addEventListener('click', function (this: HTMLElement) {
            latest.querySelector<HTMLElement>('#latest-more')!.hidden = false;
            this.remove();
          });
          latestSection.hidden = false;
        }
        const history = container.querySelector<HTMLElement>('#releases');
        const historySection = container.querySelector<HTMLElement>('#release-history');
        if (history && historySection) {
          history.innerHTML = releases.map((r) => releaseBlock(r, false)).join('');
          historySection.hidden = false;
        }
      }).catch(() => {});

      return () => {};
    },
  };
}

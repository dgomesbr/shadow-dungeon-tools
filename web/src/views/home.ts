import type { View } from '../router';
import { loadJSON } from '../data/coltable';

interface Release { date: string; title: string; items: string[] }

function esc(s: unknown): string {
  return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
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
          <section class="home-cards">
            <a class="home-card" href="#/items">
              <h2>Item Library</h2>
              <p>Every weapon, armor piece, accessory, gem, consumable and set —
              searchable, filterable, with game-faithful tooltips and icons.</p>
              <span class="cta">Browse items →</span>
            </a>
            <a class="home-card" href="#/editor">
              <h2>Save Editor</h2>
              <p>Upload your <code>slot_*.sav</code>, edit character stats, gold,
              items and equipment, then download the modified save. Byte-exact,
              verified against the game's own format.</p>
              <span class="cta">Open editor →</span>
            </a>
            <a class="home-card" href="#/mods">
              <h2>Mods — F6 Menu</h2>
              <p>Drop-in BepInEx plugins that add an in-game utility window:
              gold transfer, story-progress copy, auto-aim, boss replay and
              one-click Summon All. Download + install guide.</p>
              <span class="cta">Get the mods →</span>
            </a>
            <a class="home-card" href="https://github.com/dgomesbr/shadow-dungeon-tools" target="_blank" rel="noopener">
              <h2>GitHub</h2>
              <p>Open source: the save-format spec, data pipeline, BepInEx modding
              guide and plugins. Contributions welcome.</p>
              <span class="cta">View repository →</span>
            </a>
          </section>
          <section class="whats-new" id="whats-new" hidden>
            <h2>What's new</h2>
            <div id="releases"></div>
          </section>
          <footer class="home-foot">
            Unofficial fan tooling for Shadow Dungeon (OO Cat). Modding patterns inspired by
            Max's Character Utilities plugin from the community Discord.
          </footer>
        </div>`;

      // Release notes load lazily and never block first paint.
      void loadJSON<Release[]>('changelog.json').then((releases) => {
        const section = container.querySelector<HTMLElement>('#whats-new');
        const host = container.querySelector<HTMLElement>('#releases');
        if (!section || !host || !releases.length) return;
        host.innerHTML = releases.map((r, i) => `
          <details class="release" ${i === 0 ? 'open' : ''}>
            <summary><b>${esc(r.title)}</b><span class="dim">${esc(r.date)}</span></summary>
            <ul>${r.items.map((it) => `<li>${esc(it)}</li>`).join('')}</ul>
          </details>`).join('');
        section.hidden = false;
      }).catch(() => {});

      return () => {};
    },
  };
}

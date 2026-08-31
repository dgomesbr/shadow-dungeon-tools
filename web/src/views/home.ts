import type { View } from '../router';

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
            <a class="home-card" href="https://github.com/dgomesbr/shadow-dungeon-tools" target="_blank" rel="noopener">
              <h2>GitHub</h2>
              <p>Open source: the save-format spec, data pipeline, BepInEx modding
              guide and plugins. Contributions welcome.</p>
              <span class="cta">View repository →</span>
            </a>
          </section>
          <footer class="home-foot">
            Unofficial fan tooling for Shadow Dungeon (OO Cat). Modding patterns inspired by
            Max's Character Utilities plugin from the community Discord.
          </footer>
        </div>`;
      return () => {};
    },
  };
}

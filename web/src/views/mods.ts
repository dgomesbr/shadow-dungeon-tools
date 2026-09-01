import type { View } from '../router';

const RELEASE = 'https://github.com/dgomesbr/shadow-dungeon-tools/releases/download/mods-v1.0.0';
const ZIP = `${RELEASE}/ShadowDungeon-F6-Mods-1.0.0.zip`;
const ZIP_SHA256 = 'D9CD729BF5A1D8C6AEA4E3921FEC4EC89C5BBBD3E311E619AD50AD59465E0756';
const BEPINEX = 'https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5';
const REPO = 'https://github.com/dgomesbr/shadow-dungeon-tools';

export async function modsView(): Promise<View> {
  return {
    mount(container) {
      container.innerHTML = `
        <div class="mods">
          <section class="hero">
            <h1>The in-game <span>F6 menu</span></h1>
            <p class="tagline">Two BepInEx plugins that add a utility window inside the game —
            press <kbd>F6</kbd> and move gold between characters, copy story progress,
            auto-aim your Auto Cast, replay bosses and re-summon your whole army in one click.</p>
            <p>
              <a class="big-btn" href="${ZIP}">⬇ Download ShadowDungeon-F6-Mods-1.0.0.zip</a>
            </p>
            <p class="dim">20 KB · SHA-256 <code class="sha">${ZIP_SHA256}</code><br>
            <a href="${REPO}/releases/tag/mods-v1.0.0" target="_blank" rel="noopener">release page</a> ·
            individual DLLs and notes there. Only install DLLs you trust — back up your saves first.</p>
          </section>

          <div class="mods-cols">
            <section class="dcard">
              <h4>Install — 3 steps</h4>
              <ol class="steps">
                <li><b>Install BepInEx</b> (one time). Download
                  <a href="${BEPINEX}" target="_blank" rel="noopener">BepInEx_win_x64_5.4.23.5.zip</a>
                  and extract it into the <b>game root</b> — the folder with
                  <code>Shadow Dungeon.exe</code>, e.g.
                  <code>…\\steamapps\\common\\Shadow Dungeon\\</code>.
                  You should end up with <code>winhttp.dll</code>, <code>doorstop_config.ini</code>
                  and a <code>BepInEx\\</code> folder <i>next to</i> the exe.
                  Start the game once and quit — this creates <code>BepInEx\\plugins\\</code>.</li>
                <li><b>Install the mods.</b> Extract the zip above into the same game root
                  (it only adds <code>ShadowDungeonPlus.dll</code> and <code>SummonAll.dll</code>
                  into <code>BepInEx\\plugins\\</code>).</li>
                <li><b>Play.</b> Launch the game and press <kbd>F6</kbd>.</li>
              </ol>
              <h4>Verify &amp; troubleshoot</h4>
              <ul class="steps">
                <li>After one launch, <code>BepInEx\\LogOutput.log</code> should list
                  <i>Character Utilities</i> and <i>Summon All</i>.</li>
                <li>Nothing on F6? Make sure <code>winhttp.dll</code> sits next to the exe
                  (not inside a leftover <code>BepInEx_win_x64…\\</code> folder from a lazy extract).</li>
                <li>Disable all mods temporarily: <code>enabled = false</code> in
                  <code>doorstop_config.ini</code>. Full uninstall: delete
                  <code>winhttp.dll</code>, <code>doorstop_config.ini</code> and <code>BepInEx\\</code>.</li>
              </ul>
            </section>

            <section class="dcard">
              <h4>What the F6 menu can do today</h4>
              <p class="mod-title">Character Utilities <span class="dim">v1.1.0 — by Max</span></p>
              <ul class="steps">
                <li><b>Gold transfer</b> between your save slots, plus <b>export / import</b>
                  gold through a file (<code>BepInEx\\config\\CharacterUtilities\\gold_transfer.json</code>).</li>
                <li><b>Story-progress copy</b> from one slot to another — bring an alt straight
                  to your main's campaign unlocks.</li>
                <li><b>Auto Aim</b> — while the game's own <i>Auto Cast</i> is enabled, your casts
                  aim at the auto-lock target instead of your facing direction (toggle in config).</li>
                <li><b>Replay Boss</b> — after a boss dies, a second portal spawns that restarts
                  the fight for another run at the loot.</li>
                <li class="dim">Config: <code>BepInEx\\config\\max.characterutilities.cfg</code></li>
              </ul>
              <p class="mod-title">SummonAll <span class="dim">v1.0.0 —
                <a href="${REPO}/tree/main/mods/SummonAll" target="_blank" rel="noopener">source in this repo</a></span></p>
              <ul class="steps">
                <li><b>Summon All</b> button at the top of the F6 window — re-summons every
                  companion your talents allow, in one click (after death, zone change, …).</li>
                <li><b>Fair mode</b> (<code>RespectCooldownAndMana</code>) makes it pay normal
                  cooldowns and mana instead of summoning instantly.</li>
                <li><b>Hotkey</b> — optionally bind a key so you don't even open the menu.</li>
                <li>Works standalone: if Character Utilities isn't installed, it opens its own
                  small F6 window.</li>
                <li class="dim">Config: <code>BepInEx\\config\\dgome.summonall.cfg</code></li>
              </ul>
            </section>
          </div>

          <footer class="home-foot">
            The F6 window and its features are the work of <b>Max</b> from the Shadow Dungeon
            community Discord (#qol-mod) — redistributed here with attribution, and removed on request.
            Want to build your own plugin? The full modding guide (game internals, Harmony patterns,
            plugin walkthrough) lives in
            <a href="${REPO}/tree/main/docs/modding" target="_blank" rel="noopener">docs/modding</a>.
            Unaffiliated fan work — plugins run code inside the game process; install at your own risk.
          </footer>
        </div>`;
      return () => {};
    },
  };
}

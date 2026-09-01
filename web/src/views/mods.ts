import type { View } from '../router';

const REPO = 'https://github.com/dgomesbr/shadow-dungeon-tools';
const SUITE_RELEASE = `${REPO}/releases/download/mods-v1.1.0`;
const SUITE_ZIP = `${SUITE_RELEASE}/ShadowDungeon-QoL-Plugins-1.1.0.zip`;
const SUITE_SHA256 = '8DAA075E3725177A3F16EF56E63E906729FE3748195879B80569D28AAE5463CB';
const F6_RELEASE = `${REPO}/releases/tag/mods-v1.0.0`;
const F6_ZIP = `${REPO}/releases/download/mods-v1.0.0/ShadowDungeon-F6-Mods-1.0.0.zip`;
const BEPINEX = 'https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5';

export async function modsView(): Promise<View> {
  return {
    mount(container) {
      container.innerHTML = `
        <div class="mods">
          <section class="hero">
            <h1>QoL <span>plugin suite</span></h1>
            <p class="tagline">Seven open-source BepInEx plugins, one DLL each — install only
            what you want. An in-combat DPS meter, readable damage numbers
            (<i>1.2&nbsp;Trillion</i> instead of digit soup), affix roll ranges and ground-loot
            tooltips, an FPS-saving VFX reducer, Shift-click enhance-to-max, a Corrupted Realm
            floor selector and one-click re-summoning.</p>
            <p>
              <a class="big-btn" href="${SUITE_ZIP}">⬇ Download ShadowDungeon-QoL-Plugins-1.1.0.zip</a>
            </p>
            <p class="dim">63 KB · SHA-256 <code class="sha">${SUITE_SHA256}</code><br>
            <a href="${REPO}/releases/tag/mods-v1.1.0" target="_blank" rel="noopener">release page</a> ·
            <a href="${REPO}/tree/main/mods" target="_blank" rel="noopener">source &amp; docs</a> ·
            only install DLLs you trust — back up your saves first.</p>
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
                <li><b>Install the plugins.</b> Extract the zip above into the same game root
                  (it only adds DLLs into <code>BepInEx\\plugins\\</code>). Don't want one?
                  Delete its DLL.</li>
                <li><b>Play.</b> <kbd>F9</kbd> DPS meter · <kbd>F10</kbd> floor selector ·
                  <kbd>F11</kbd> VFX reducer · <kbd>Shift</kbd>+click at the forge ·
                  <kbd>F6</kbd> Summon All.</li>
              </ol>
              <h4>Verify &amp; troubleshoot</h4>
              <ul class="steps">
                <li>After one launch, <code>BepInEx\\LogOutput.log</code> lists every loaded plugin.</li>
                <li>Nothing happens? Make sure <code>winhttp.dll</code> sits next to the exe
                  (not inside a leftover <code>BepInEx_win_x64…\\</code> folder from a lazy extract).</li>
                <li>Each plugin's settings live in <code>BepInEx\\config\\custom.&lt;plugin&gt;.cfg</code>
                  (hotkeys are rebindable). Disable all mods temporarily:
                  <code>enabled = false</code> in <code>doorstop_config.ini</code>.</li>
                <li>Every plugin fails soft: if a game update breaks a hook it logs one warning
                  and turns itself off instead of breaking the game.</li>
              </ul>
            </section>

            <section class="dcard">
              <h4>What's in the suite <span class="dim">v1.1.0 —
                <a href="${REPO}/tree/main/mods" target="_blank" rel="noopener">source in this repo</a></span></h4>
              <ul class="steps">
                <li><b>Combat DPS Meter</b> (<kbd>F9</kbd>) — real dungeon DPS with per-source
                  rows (you, each summon, DoTs), share %, peak; the vanilla meter only works on
                  the training dummy.</li>
                <li><b>Readable Numbers</b> — damage, DPS and gold at the nearest named scale:
                  <i>510 Billion</i>, <i>1.2 Trillion</i>, <i>3.4 Quadrillion</i>…</li>
                <li><b>Advanced Tooltips</b> — affix lines show <i>rolled X (min~max)</i>, and
                  hovering ground loot shows its full tooltip without picking it up.</li>
                <li><b>VFX Reducer</b> (<kbd>F11</kbd>) — Off / Reduced / Minimal particle
                  budgets on your skills and summons for dense-floor FPS.</li>
                <li><b>Quick Enhance</b> — hold <kbd>Shift</kbd> and click enhance: runs to
                  +max / out of gold in one burst.</li>
                <li><b>Mijing Floor Selector</b> (<kbd>F10</kbd>) — jump to any unlocked
                  Corrupted Realm floor; optional confirm-gated cap raise.</li>
                <li><b>Summon All</b> — the original: re-summon your whole army in one click
                  from the F6 window, with an optional fair mode and hotkey.</li>
              </ul>
              <p class="mod-title">Character Utilities <span class="dim">v1.1.0 — by Max ·
                <a href="${F6_RELEASE}" target="_blank" rel="noopener">separate download</a></span></p>
              <ul class="steps">
                <li>The original F6 window: <b>gold transfer</b> between slots, <b>story-progress
                  copy</b>, <b>Auto Aim</b> for Auto Cast, <b>Replay Boss</b> portals.
                  Get it from the <a href="${F6_ZIP}">mods-v1.0.0 pack</a> — the suite embeds its
                  Summon All button into that window when both are installed.</li>
              </ul>
            </section>
          </div>

          <footer class="home-foot">
            The suite is unaffiliated fan work (MIT, full source in
            <a href="${REPO}/tree/main/mods" target="_blank" rel="noopener">mods/</a>).
            Character Utilities is the work of <b>Max</b> from the Shadow Dungeon community
            Discord (#qol-mod) — redistributed with attribution, removed on request.
            Want to build your own plugin? The full modding guide lives in
            <a href="${REPO}/tree/main/docs/modding" target="_blank" rel="noopener">docs/modding</a>.
            Plugins run code inside the game process — install at your own risk.
          </footer>
        </div>`;
      return () => {};
    },
  };
}

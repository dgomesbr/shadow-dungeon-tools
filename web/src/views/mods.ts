import type { View } from '../router';

const REPO = 'https://github.com/dgomesbr/shadow-dungeon-tools';
const SUITE_RELEASE = `${REPO}/releases/download/mods-v1.2.0`;
const SUITE_ZIP = `${SUITE_RELEASE}/ShadowDungeon-QoL-Plugins-1.2.0.zip`;
const SUITE_SHA256 = '55FD1FF683A5ED254B0E8A8593F869E64BD4D9A430320CA768A5DCE066D7858A';
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
            <p class="tagline">Nine open-source BepInEx plugins driven from one translucent
            <b>Mods</b> panel docked to the screen edge, with no hotkeys to remember.
            19 measured performance patches, an in-combat DPS meter, readable numbers
            (<i>1.2&nbsp;Trillion</i> instead of digit soup), affix roll ranges, ground-loot
            tooltips, a VFX reducer, Shift-click enhance-to-max, a Corrupted Realm floor selector
            and one-click summon or dismiss.</p>
            <p>
              <a class="big-btn" href="${SUITE_ZIP}">⬇ Download ShadowDungeon-QoL-Plugins-1.2.0.zip</a>
            </p>
            <p class="dim">135 KB · SHA-256 <code class="sha">${SUITE_SHA256}</code><br>
            <a href="${REPO}/releases/tag/mods-v1.2.0" target="_blank" rel="noopener">release page</a> ·
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
                <li><b>Play.</b> Everything lives in the <b>Mods</b> panel on the right edge of
                  the screen: click a row to toggle it, hover for a description. There are no
                  hotkeys to learn. (<kbd>F6</kbd> still opens Character Utilities, a separate
                  mod, and <kbd>Shift</kbd>+click is a click modifier at the forge.)</li>
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
              <h4>What's in the suite <span class="dim">v1.2.0 —
                <a href="${REPO}/tree/main/mods" target="_blank" rel="noopener">source in this repo</a></span></h4>
              <ul class="steps">
                <li><b>Mod Menu</b> &mdash; the panel itself: translucent, docked to the right
                  edge, one row per feature with an icon, live state and a hover description.</li>
                <li><b>Performance Patches</b> &mdash; 19 independently toggleable patches for the
                  hot paths behind the stutter (raycast caching, projectile targeting, AI tick
                  staggering, damage-number merging), plus a frame-time overlay and a 60-second
                  benchmark that writes a CSV.
                  <a href="${REPO}/blob/main/docs/blog/where-shadow-dungeon-spends-its-frame-time.md" target="_blank" rel="noopener">Read the write-up</a>.</li>
                <li><b>Combat DPS Meter</b> &mdash; real dungeon DPS with per-source rows (you,
                  each summon, DoTs), share %, peak; the vanilla meter only works on the training
                  dummy.</li>
                <li><b>Readable Numbers</b> &mdash; damage, DPS, gold and the HP/mana readouts at
                  the nearest named scale: <i>510 Billion</i>, <i>1.2 Trillion</i>,
                  <i>3.4 Quadrillion</i>&hellip;</li>
                <li><b>Advanced Tooltips</b> &mdash; affix lines show <i>rolled X (min~max)</i>,
                  and hovering ground loot shows its full tooltip without picking it up.</li>
                <li><b>VFX Reducer</b> &mdash; Off / Reduced / Minimal particle budgets on your
                  skills and summons for dense-floor FPS.</li>
                <li><b>Quick Enhance</b> &mdash; hold <kbd>Shift</kbd> and click enhance: runs to
                  +max / out of gold in one burst.</li>
                <li><b>Corrupted Realm Selector</b> &mdash; jump to any unlocked floor; optional
                  confirm-gated cap raise.</li>
                <li><b>Summon All</b> &mdash; summon or dismiss your whole army in one click, and
                  the summon counter row sits above your skill bar.</li>
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

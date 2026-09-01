import { register, start } from './router';

const app = document.getElementById('app')!;
app.innerHTML = `
  <header class="topbar">
    <a class="brand" href="#/">Shadow<span>Dungeon</span>Tools</a>
    <nav>
      <a href="#/items">Items</a>
      <a href="#/editor">Save Editor</a>
      <a href="#/mods">Mods</a>
      <a href="https://github.com/dgomesbr/shadow-dungeon-tools" target="_blank" rel="noopener">GitHub</a>
    </nav>
    <span class="build" title="Build ${__BUILD_ID__} — if a new feature is missing, hard-refresh (Ctrl+F5)">${__BUILD_ID__}</span>
  </header>
  <div id="view"></div>
`;

register('/', () => import('./views/home').then((m) => m.homeView()));
register('/items', () => import('./views/items').then((m) => m.itemsView()));
register('/editor', () => import('./views/editor').then((m) => m.editorView()));
register('/build', () => import('./views/build').then((m) => m.buildView()));
register('/mods', () => import('./views/mods').then((m) => m.modsView()));

start(document.getElementById('view')!);

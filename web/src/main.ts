import { register, start } from './router';

const app = document.getElementById('app')!;
app.innerHTML = `
  <header class="topbar">
    <a class="brand" href="#/">Shadow<span>Dungeon</span>Tools</a>
    <nav>
      <a href="#/editor">Save Game Editor</a>
      <a href="#/items">Item Library</a>
      <a href="#/mods">Mods</a>
    </nav>
    <span class="build" title="Build ${__BUILD_ID__} — if a new feature is missing, hard-refresh (Ctrl+F5)">${__BUILD_ID__}</span>
    <a class="gh-link" href="https://github.com/dgomesbr/shadow-dungeon-tools" target="_blank" rel="noopener" title="Source, docs and releases">
      <svg viewBox="0 0 16 16" width="15" height="15" fill="currentColor" aria-hidden="true"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z"/></svg>
      View on GitHub
    </a>
  </header>
  <div id="view"></div>
`;

register('/', () => import('./views/home').then((m) => m.homeView()));
register('/items', () => import('./views/items').then((m) => m.itemsView()));
register('/editor', () => import('./views/editor').then((m) => m.editorView()));
register('/build', () => import('./views/build').then((m) => m.buildView()));
register('/mods', () => import('./views/mods').then((m) => m.modsView()));

start(document.getElementById('view')!);

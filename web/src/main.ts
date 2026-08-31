import { register, start } from './router';

const app = document.getElementById('app')!;
app.innerHTML = `
  <header class="topbar">
    <a class="brand" href="#/">Shadow<span>Dungeon</span>Tools</a>
    <nav>
      <a href="#/items">Items</a>
      <a href="#/editor">Save Editor</a>
      <a href="https://github.com/dgomesbr/shadow-dungeon-tools" target="_blank" rel="noopener">GitHub</a>
    </nav>
  </header>
  <div id="view"></div>
`;

register('/', () => import('./views/home').then((m) => m.homeView()));
register('/items', () => import('./views/items').then((m) => m.itemsView()));
register('/editor', () => import('./views/editor').then((m) => m.editorView()));

start(document.getElementById('view')!);

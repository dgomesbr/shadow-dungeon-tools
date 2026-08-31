// Minimal hash router. Views are lazy factories so code-splitting keeps the
// initial bundle tiny.
export interface View {
  /** Mount into the container. Return a cleanup function. */
  mount(container: HTMLElement): () => void;
}

type ViewFactory = () => Promise<View>;

const routes = new Map<string, ViewFactory>();
let cleanup: (() => void) | null = null;
let container: HTMLElement;

export function register(path: string, factory: ViewFactory): void {
  routes.set(path, factory);
}

export function currentPath(): string {
  const h = location.hash.replace(/^#/, '');
  return h === '' ? '/' : h.split('?')[0]!;
}

export function queryParams(): URLSearchParams {
  const h = location.hash;
  const q = h.indexOf('?');
  return new URLSearchParams(q === -1 ? '' : h.slice(q + 1));
}

async function render(): Promise<void> {
  const factory = routes.get(currentPath()) ?? routes.get('/');
  if (!factory) return;
  cleanup?.();
  cleanup = null;
  const view = await factory();
  container.replaceChildren();
  cleanup = view.mount(container);
  for (const a of document.querySelectorAll<HTMLAnchorElement>('nav a[href^="#"]')) {
    a.classList.toggle('active', a.hash.replace(/^#/, '').split('?')[0] === currentPath());
  }
}

export function start(el: HTMLElement): void {
  container = el;
  addEventListener('hashchange', () => void render());
  void render();
}

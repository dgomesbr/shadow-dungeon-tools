import { defineConfig } from 'vite';

export default defineConfig({
  // GitHub Pages serves the site under the repo name.
  base: process.env.CI ? '/shadow-dungeon-tools/' : '/',
  define: {
    // Short commit id shown in the top bar — makes stale-cache issues obvious.
    __BUILD_ID__: JSON.stringify((process.env.GITHUB_SHA ?? 'dev').slice(0, 7)),
  },
  build: {
    target: 'es2022',
    sourcemap: false,
    modulePreload: { polyfill: false },
  },
  worker: {
    format: 'es',
  },
});

import { defineConfig } from 'vite';

export default defineConfig({
  // GitHub Pages serves the site under the repo name.
  base: process.env.CI ? '/shadow-dungeon-tools/' : '/',
  build: {
    target: 'es2022',
    sourcemap: false,
    modulePreload: { polyfill: false },
  },
  worker: {
    format: 'es',
  },
});

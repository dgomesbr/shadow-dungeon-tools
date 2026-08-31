import type { View } from '../router';

// Placeholder — replaced once the .sav parser lands.
export async function editorView(): Promise<View> {
  return {
    mount(container) {
      container.innerHTML = `<div class="boot"><p>Save editor — parser in progress. Your file never leaves the browser.</p></div>`;
      return () => {};
    },
  };
}

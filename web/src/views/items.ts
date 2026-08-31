import type { View } from '../router';

// Placeholder — replaced once the data pipeline lands its JSON schema.
export async function itemsView(): Promise<View> {
  return {
    mount(container) {
      container.innerHTML = `<div class="boot"><p>Item library — data pipeline in progress.</p></div>`;
      return () => {};
    },
  };
}

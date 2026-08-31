// Windowed (virtualized) list for uniform-height rows. Renders only visible
// rows + overscan, recycles row elements, and batches updates in rAF.
export interface VListOptions<T> {
  rowHeight: number;
  overscan?: number;
  /** Create an empty, reusable row element. */
  createRow(): HTMLElement;
  /** Fill a recycled row element with item data. Must not allocate per call. */
  renderRow(el: HTMLElement, item: T, index: number): void;
  onRowClick?(item: T, index: number, ev: MouseEvent): void;
}

export class VList<T> {
  readonly root: HTMLElement;
  private readonly spacer: HTMLElement;
  private readonly opts: Required<Pick<VListOptions<T>, 'rowHeight'>> & VListOptions<T>;
  private items: readonly T[] = [];
  private pool: HTMLElement[] = [];
  private first = -1; // first rendered index (-1 = nothing rendered)
  private rafPending = false;

  constructor(opts: VListOptions<T>) {
    this.opts = opts;
    this.root = document.createElement('div');
    this.root.className = 'vlist';
    this.spacer = document.createElement('div');
    this.spacer.className = 'vlist-spacer';
    this.root.appendChild(this.spacer);
    this.root.addEventListener('scroll', this.schedule, { passive: true });
    if (opts.onRowClick) {
      this.spacer.addEventListener('click', (ev) => {
        const row = (ev.target as HTMLElement).closest<HTMLElement>('[data-i]');
        if (!row) return;
        const i = Number(row.dataset['i']);
        const item = this.items[i];
        if (item !== undefined) this.opts.onRowClick!(item, i, ev);
      });
    }
  }

  setItems(items: readonly T[]): void {
    this.items = items;
    this.spacer.style.height = `${items.length * this.opts.rowHeight}px`;
    this.first = -1; // force full repaint
    this.paint(); // synchronous: rAF may never fire in hidden tabs
  }

  private schedule = (): void => {
    if (this.rafPending) return;
    this.rafPending = true;
    requestAnimationFrame(() => {
      this.rafPending = false;
      this.paint();
    });
  };

  private paint(): void {
    const { rowHeight } = this.opts;
    const overscan = this.opts.overscan ?? 6;
    const top = this.root.scrollTop;
    const height = this.root.clientHeight;
    const first = Math.max(0, Math.floor(top / rowHeight) - overscan);
    const last = Math.min(this.items.length, Math.ceil((top + height) / rowHeight) + overscan);
    const count = last - first;
    if (first === this.first && count === this.pool.length) return;
    this.first = first;

    while (this.pool.length < count) {
      const el = this.opts.createRow();
      el.classList.add('vlist-row');
      this.spacer.appendChild(el);
      this.pool.push(el);
    }
    for (let k = 0; k < this.pool.length; k++) {
      const el = this.pool[k]!;
      const i = first + k;
      if (k >= count || i >= this.items.length) {
        el.style.display = 'none';
        continue;
      }
      el.style.display = '';
      el.style.transform = `translateY(${i * rowHeight}px)`;
      el.dataset['i'] = String(i);
      this.opts.renderRow(el, this.items[i]!, i);
    }
  }
}

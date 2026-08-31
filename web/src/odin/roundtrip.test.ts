// Byte-identical round-trip against real saves (web/fixtures/, gitignored).
// These are the user's actual save files — the strongest possible corpus.
import { describe, expect, it } from 'vitest';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseOdin } from './reader';
import { writeOdin } from './writer';

const FIXTURES = join(__dirname, '..', '..', 'fixtures');
const FILES = ['global.sav', 'slot_1.sav', 'slot_1_auto.sav', 'slot_1_exit.sav'];

function firstDiff(a: Uint8Array, b: Uint8Array): number {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) if (a[i] !== b[i]) return i;
  return a.length === b.length ? -1 : n;
}

describe('odin binary round-trip', () => {
  for (const file of FILES) {
    const p = join(FIXTURES, file);
    it.skipIf(!existsSync(p))(`${file} re-encodes byte-identically`, () => {
      const original = new Uint8Array(readFileSync(p));
      const t0 = performance.now();
      const doc = parseOdin(original);
      const parseMs = performance.now() - t0;
      const t1 = performance.now();
      const encoded = writeOdin(doc);
      const writeMs = performance.now() - t1;
      const diff = firstDiff(original, encoded);
      // eslint-disable-next-line no-console
      console.log(`${file}: ${original.length} bytes, parse ${parseMs.toFixed(1)}ms, write ${writeMs.toFixed(1)}ms, roots=${doc.root.length}, types=${doc.typeNames.size}`);
      expect(encoded.length).toBe(original.length);
      expect(diff).toBe(-1);
    });
  }
});

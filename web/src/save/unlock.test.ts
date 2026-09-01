// Structural unlock edits must produce exactly the bytes the game itself
// would write: remove the last UnlockedLevelIds entry from the parsed tree,
// re-add it through applyUnlock, and require a byte-identical file.
import { describe, expect, it } from 'vitest';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseOdin } from '../odin/reader';
import { writeOdin } from '../odin/writer';
import { applyUnlock, buildSummary } from './summary';
import { child, isContainer, type AnyNode } from '../odin/tree';

const FIXTURE = join(__dirname, '..', '..', 'fixtures', 'slot_1.sav');

describe.skipIf(!existsSync(FIXTURE))('unlock structural edits', () => {
  it('re-adding a removed level id reproduces the original bytes', () => {
    const original = new Uint8Array(readFileSync(FIXTURE));
    const doc = parseOdin(original);
    const root = doc.root[0]!;
    if (!isContainer(root)) throw new Error('bad root');
    const holder = child(root, 'UnlockedLevelIds')!;
    if (!isContainer(holder)) throw new Error('bad holder');
    const arr = holder.children.find((c) => (c as AnyNode).kind === 'array') as
      Extract<AnyNode, { kind: 'array' }>;
    const last = arr.children[arr.children.length - 1] as AnyNode;
    if (last.kind !== 'string') throw new Error('expected string entry');
    const removedId = last.value;
    arr.children.pop();
    arr.length = BigInt(arr.children.length);

    const added = applyUnlock(doc, { chapters: [], levels: [removedId], bossLevels: [] });
    expect(added.levels).toBe(1);

    const bytes = writeOdin(doc);
    expect(bytes.length).toBe(original.length);
    let diff = -1;
    for (let i = 0; i < bytes.length; i++) if (bytes[i] !== original[i]) { diff = i; break; }
    expect(diff).toBe(-1);
  });

  it('adds chapters/levels/boss ids and raises mijing floors additively', () => {
    const doc = parseOdin(new Uint8Array(readFileSync(FIXTURE)));
    const before = buildSummary(doc, 'x');
    const added = applyUnlock(doc, {
      chapters: [...before.unlockedChapters, 1],       // all existing → no-op
      levels: ['99_99'],                               // synthetic id, structural check only
      bossLevels: [],
      mijing: { easy: 5, master: 999 },                // easy 5 ≤ current → no lower
    });
    expect(added.chapters).toBe(0);
    expect(added.levels).toBe(1);
    const after = buildSummary(parseOdin(writeOdin(doc)), 'x');
    expect(after.unlockedLevels).toContain('99_99');
    expect(after.mijing.master).toBe(999);
    expect(after.mijing.easy).toBe(before.mijing.easy); // never lowered
    expect(after.mijing.unlocked).toBe(true);
  });
});

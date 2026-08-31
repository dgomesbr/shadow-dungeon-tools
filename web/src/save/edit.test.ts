// Edit → encode → reparse: verifies handle resolution, value mutation, and
// that an edited save still parses with only the intended bytes changed.
import { describe, expect, it } from 'vitest';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseOdin } from '../odin/reader';
import { writeOdin } from '../odin/writer';
import { applySet, buildSummary, resolveHandle } from './summary';

const FIXTURE = join(__dirname, '..', '..', 'fixtures', 'slot_1.sav');

describe.skipIf(!existsSync(FIXTURE))('save editing', () => {
  it('summary exposes player fields, money, equipment and inventory', () => {
    const doc = parseOdin(new Uint8Array(readFileSync(FIXTURE)));
    const s = buildSummary(doc, 'slot_1.sav');
    expect(s.player.find((l) => l.name === 'Level')).toBeTruthy();
    expect(s.money).toBeTruthy();
    expect(s.equipment.length).toBe(10);
    expect(s.inventory.length).toBeGreaterThan(50);
    expect(s.pageCount).toBeGreaterThan(0);
    // protected fields never appear
    for (const l of s.player) expect(['SessionId', 'BackupKind']).not.toContain(l.name);
  });

  it('applySet mutates, re-encodes to same length, and reparses with new value', () => {
    const original = new Uint8Array(readFileSync(FIXTURE));
    const doc = parseOdin(original);
    const s = buildSummary(doc, 'slot_1.sav');
    const level = s.player.find((l) => l.name === 'Level')!;
    applySet(doc, level.handle, 42);

    const node = resolveHandle(doc, level.handle);
    expect(node.kind).toBe('prim');

    const bytes = writeOdin(doc);
    expect(bytes.length).toBe(original.length); // int stays 4 bytes

    const reparsed = buildSummary(parseOdin(bytes), 'slot_1.sav');
    expect(reparsed.player.find((l) => l.name === 'Level')!.value).toBe(42);

    // only the level payload bytes may differ
    let diffs = 0;
    for (let i = 0; i < bytes.length; i++) if (bytes[i] !== original[i]) diffs++;
    expect(diffs).toBeGreaterThan(0);
    expect(diffs).toBeLessThanOrEqual(4);
  });

  it('refuses to edit protected fields', () => {
    const doc = parseOdin(new Uint8Array(readFileSync(FIXTURE)));
    // SessionId is root child index 4 in current saves; find it dynamically
    const root = doc.root[0]!;
    if (root.kind !== 'ref') throw new Error('bad root');
    const i = root.children.findIndex((c) => c.name === 'SessionId');
    expect(i).toBeGreaterThanOrEqual(0);
    expect(() => applySet(doc, String(i), 'boom')).toThrow(/protected/);
  });
});

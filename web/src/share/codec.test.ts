// Share codec round-trip against the real save, plus the whole point of the
// feature: the link must fit in a Discord non-Nitro message (2000 chars).
import { describe, expect, it } from 'vitest';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseOdin } from '../odin/reader';
import { buildSummary } from '../save/summary';
import { Table } from '../data/coltable';
import type { Catalog } from '../data/catalog';
import { buildFromSummary, decodeShare, encodeShare, summaryFromBuild } from './codec';

const FIXTURE = join(__dirname, '..', '..', 'fixtures', 'slot_1.sav');
const SKILLS = join(__dirname, '..', '..', 'public', 'data', 'skills.json');

function fakeCatalog(): Catalog {
  const skills = new Table(JSON.parse(readFileSync(SKILLS, 'utf8')));
  return { skills, skillByIndexName: skills.keyBy('IndexName') } as unknown as Catalog;
}

describe.skipIf(!existsSync(FIXTURE))('build share codec', () => {
  it('round-trips the real build and fits in a Discord message', async () => {
    const cat = fakeCatalog();
    const summary = buildSummary(parseOdin(new Uint8Array(readFileSync(FIXTURE))), 'slot_1.sav');
    const build = buildFromSummary(summary);

    const payload = await encodeShare(build, cat);
    const url = `https://dgomesbr.github.io/shadow-dungeon-tools/#/build?d=${payload}`;
    // eslint-disable-next-line no-console
    console.log(`share link: ${url.length} chars (${build.equipment.length} items, ${build.talents.length} talents)`);
    expect(url.length).toBeLessThan(2000);

    const back = await decodeShare(payload, cat);
    expect(back.dataDrift).toBe(false);
    expect(back.name).toBe(build.name);
    expect(back.level).toBe(build.level);
    expect(back.pBase).toBe(build.pBase);
    expect(back.equipment.length).toBe(build.equipment.length);
    expect(back.talents.length).toBe(build.talents.length);

    // Talents survive by name+points (order may differ: known ones are sorted).
    const orig = new Map(build.talents.map((t) => [t.name, t.points]));
    for (const t of back.talents) expect(t.points).toBe(orig.get(t.name));

    // Equipment: identity exact, rolled values within quantization (1/100).
    const bySlot = new Map(build.equipment.map((e) => [e.slot, e]));
    for (const e of back.equipment) {
      const o = bySlot.get(e.slot)!;
      expect(e.globalId).toBe(o.globalId);
      expect(e.quality).toBe(o.quality);
      expect(e.setIndex).toBe(o.setIndex);
      expect(e.main.length).toBe(o.main.length);
      e.main.forEach(([idx, el, n], i) => {
        expect(idx).toBe(o.main[i]![0]);
        expect(el).toBe(o.main[i]![1]);
        expect(Math.abs(n - o.main[i]![2])).toBeLessThanOrEqual(0.005);
      });
      e.elements.forEach((v, i) => expect(Math.abs(v - o.elements[i]!)).toBeLessThanOrEqual(0.005));
      expect(e.wpsk.map((w) => w.name)).toEqual(o.wpsk.map((w) => w.name));
      expect(e.aocaoTypes).toEqual(o.aocaoTypes);
    }

    // The viewer adapter produces a renderable summary.
    const s2 = summaryFromBuild(back);
    expect(s2.equipment.length).toBe(build.equipment.length);
    expect(s2.player.find((l) => l.name === 'PlayerName')?.value).toBe(build.name);
  });
});

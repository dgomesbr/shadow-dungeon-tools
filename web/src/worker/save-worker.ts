/// <reference lib="webworker" />
import { parseOdin } from '../odin/reader';
import { writeOdin } from '../odin/writer';
import { applySet, buildSummary, deepLeaves, resolveHandle } from '../save/summary';
import type { OdinDocument } from '../odin/tree';
import type { WorkerRequest, WorkerResponse } from './protocol';

interface OpenFile {
  doc: OdinDocument;
  size: number;
}

const files = new Map<string, OpenFile>();

function firstDiff(a: Uint8Array, b: Uint8Array): number {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) if (a[i] !== b[i]) return i;
  return a.length === b.length ? -1 : n;
}

function handle(req: WorkerRequest): { res: WorkerResponse; transfer: Transferable[] } {
  switch (req.op) {
    case 'parse': {
      const bytes = new Uint8Array(req.buffer);
      const doc = parseOdin(bytes);
      const reEncoded = writeOdin(doc);
      const diff = firstDiff(bytes, reEncoded);
      files.set(req.fileName, { doc, size: bytes.length });
      return {
        res: {
          id: req.id, ok: true, op: 'parse',
          summary: buildSummary(doc, req.fileName),
          roundTrip: diff === -1,
          firstDiff: diff,
        },
        transfer: [],
      };
    }
    case 'set': {
      const f = files.get(req.fileName);
      if (!f) throw new Error(`${req.fileName} not loaded`);
      for (const s of req.sets) applySet(f.doc, s.handle, s.value);
      return {
        res: { id: req.id, ok: true, op: 'set', summary: buildSummary(f.doc, req.fileName) },
        transfer: [],
      };
    }
    case 'detail': {
      const f = files.get(req.fileName);
      if (!f) throw new Error(`${req.fileName} not loaded`);
      const node = resolveHandle(f.doc, req.handle);
      return {
        res: { id: req.id, ok: true, op: 'detail', leaves: deepLeaves(node, req.handle) },
        transfer: [],
      };
    }
    case 'encode': {
      const f = files.get(req.fileName);
      if (!f) throw new Error(`${req.fileName} not loaded`);
      const bytes = writeOdin(f.doc);
      const buffer = bytes.buffer as ArrayBuffer;
      return { res: { id: req.id, ok: true, op: 'encode', buffer }, transfer: [buffer] };
    }
  }
}

self.onmessage = (ev: MessageEvent<WorkerRequest>) => {
  try {
    const { res, transfer } = handle(ev.data);
    postMessage(res, { transfer });
  } catch (e) {
    postMessage({ id: ev.data.id, ok: false, error: e instanceof Error ? e.message : String(e) } satisfies WorkerResponse);
  }
};

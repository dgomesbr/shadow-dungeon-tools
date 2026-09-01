import type { Leaf, SaveSummary, SetOp, WorkerRequest, WorkerResponse } from './protocol';

// Promise-based RPC to the save worker. One worker instance, lazily created;
// ArrayBuffers are transferred, never structured-cloned.
let worker: Worker | null = null;
let nextId = 1;
const pending = new Map<number, { resolve: (v: WorkerResponse) => void; reject: (e: Error) => void }>();

function getWorker(): Worker {
  if (!worker) {
    worker = new Worker(new URL('./save-worker.ts', import.meta.url), { type: 'module' });
    worker.onmessage = (ev: MessageEvent<WorkerResponse>) => {
      const p = pending.get(ev.data.id);
      if (!p) return;
      pending.delete(ev.data.id);
      p.resolve(ev.data);
    };
    worker.onerror = (ev) => {
      for (const p of pending.values()) p.reject(new Error(ev.message));
      pending.clear();
    };
  }
  return worker;
}

async function call<T extends WorkerResponse & { ok: true }>(
  req: WorkerRequest, transfer: Transferable[],
): Promise<T> {
  const res = await new Promise<WorkerResponse>((resolve, reject) => {
    pending.set(req.id, { resolve, reject });
    getWorker().postMessage(req, transfer);
  });
  if (!res.ok) throw new Error(res.error);
  return res as T;
}

export interface ParseResult {
  summary: SaveSummary;
  roundTrip: boolean;
  firstDiff: number;
}

export async function parseSave(buffer: ArrayBuffer, fileName: string): Promise<ParseResult> {
  const r = await call<Extract<WorkerResponse, { op: 'parse' }>>(
    { id: nextId++, op: 'parse', buffer, fileName }, [buffer]);
  return { summary: r.summary, roundTrip: r.roundTrip, firstDiff: r.firstDiff };
}

export async function applySets(fileName: string, sets: SetOp[]): Promise<SaveSummary> {
  const r = await call<Extract<WorkerResponse, { op: 'set' }>>(
    { id: nextId++, op: 'set', fileName, sets }, []);
  return r.summary;
}

export async function itemDetail(fileName: string, handle: string): Promise<Leaf[]> {
  const r = await call<Extract<WorkerResponse, { op: 'detail' }>>(
    { id: nextId++, op: 'detail', fileName, handle }, []);
  return r.leaves;
}

export async function applyUnlock(
  fileName: string, unlock: import('./protocol').UnlockOp,
): Promise<{ summary: SaveSummary; added: { chapters: number; levels: number; bossLevels: number } }> {
  const r = await call<Extract<WorkerResponse, { op: 'unlock' }>>(
    { id: nextId++, op: 'unlock', fileName, unlock }, []);
  return { summary: r.summary, added: r.added };
}

export async function encodeSave(fileName: string): Promise<ArrayBuffer> {
  const r = await call<Extract<WorkerResponse, { op: 'encode' }>>(
    { id: nextId++, op: 'encode', fileName }, []);
  return r.buffer;
}

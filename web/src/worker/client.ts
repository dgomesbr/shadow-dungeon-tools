import type { WorkerRequest, WorkerResponse, SaveSummary } from './protocol';

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

function call(req: WorkerRequest, transfer: Transferable[]): Promise<WorkerResponse> {
  return new Promise((resolve, reject) => {
    pending.set(req.id, { resolve, reject });
    getWorker().postMessage(req, transfer);
  });
}

async function unwrap<T extends WorkerResponse>(p: Promise<WorkerResponse>): Promise<T> {
  const res = await p;
  if (!res.ok) throw new Error(res.error);
  return res as T;
}

export async function parseSave(buffer: ArrayBuffer, fileName: string): Promise<SaveSummary> {
  const res = await unwrap<Extract<WorkerResponse, { op: 'parse' }>>(
    call({ id: nextId++, op: 'parse', buffer, fileName }, [buffer]),
  );
  return res.summary;
}

export async function encodeSave(patch: unknown, fileName: string): Promise<ArrayBuffer> {
  const res = await unwrap<Extract<WorkerResponse, { op: 'encode' }>>(
    call({ id: nextId++, op: 'encode', patch, fileName }, []),
  );
  return res.buffer;
}

export async function verifyRoundtrip(buffer: ArrayBuffer): Promise<{ identical: boolean; firstDiff: number }> {
  return unwrap<Extract<WorkerResponse, { op: 'roundtrip' }>>(
    call({ id: nextId++, op: 'roundtrip', buffer }, [buffer]),
  );
}

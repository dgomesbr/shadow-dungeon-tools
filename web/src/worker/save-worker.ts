/// <reference lib="webworker" />
import type { WorkerRequest, WorkerResponse } from './protocol';

// Parse/encode land here once the wire-format spec is implemented in ../odin.
function fail(id: number, error: string): WorkerResponse {
  return { id, ok: false, error };
}

self.onmessage = (ev: MessageEvent<WorkerRequest>) => {
  const req = ev.data;
  try {
    switch (req.op) {
      case 'parse':
      case 'encode':
      case 'roundtrip':
        postMessage(fail(req.id, 'save parser not implemented yet'));
        break;
    }
  } catch (e) {
    postMessage(fail(req.id, e instanceof Error ? e.message : String(e)));
  }
};

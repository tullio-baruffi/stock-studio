// Local record of what this browser handed to the durable pipeline.
//
// The handoff creates no job and returns no identifier to poll: once the message is on the queue
// the work belongs to the Function, and the API keeps no trace of the batch. That is the whole
// point of the durable path, but it leaves the author without an answer to "what did I just send?".
//
// So the browser remembers, on its own. This is a receipt, not a source of truth: the real state of
// a picture lives in SharePoint and is read back per file through /api/pipeline/track. Losing this
// list costs nothing but the convenience of a list.

const KEY = "deliveredBatches";

/** Past this many batches the oldest are dropped: a receipt drawer, not an archive. */
const MAX_BATCHES = 40;

export type DeliveredFile = {
  name: string;
  /** Leaf name the pipeline works with downstream — the JPEG is what carries through. */
  trackName: string;
  threshold: number | null;
  ok: boolean;
  error?: string;
};

export type DeliveredBatch = {
  id: string;
  at: string;
  mode: "vector" | "raster";
  accepted: number;
  rejected: number;
  files: DeliveredFile[];
};

/** The name the deliverables take in SharePoint: same base as the upload, always .jpg. */
export function trackNameFor(fileName: string): string {
  const base = fileName.replace(/\.[^.]+$/, "");
  return `${base || "image"}.jpg`;
}

export function listDeliveries(): DeliveredBatch[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as DeliveredBatch[]) : [];
  } catch {
    // A corrupt receipt drawer must not take the view down with it.
    return [];
  }
}

export function recordDelivery(batch: DeliveredBatch): void {
  try {
    localStorage.setItem(KEY, JSON.stringify([batch, ...listDeliveries()].slice(0, MAX_BATCHES)));
  } catch {
    /* private mode, or a full quota: the delivery already happened, the receipt is optional */
  }
}

export function forgetDelivery(id: string): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(listDeliveries().filter((b) => b.id !== id)));
  } catch {
    /* ignored for the same reason */
  }
}

export function clearDeliveries(): void {
  try {
    localStorage.removeItem(KEY);
  } catch {
    /* ignored for the same reason */
  }
}

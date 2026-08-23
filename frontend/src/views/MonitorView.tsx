import { useCallback, useEffect, useState } from "react";
import { api, type JobSummary, type Queues, type TrackResult } from "../api";
import { clearDeliveries, forgetDelivery, listDeliveries, type DeliveredBatch, type DeliveredFile } from "../deliveries";

/**
 * Follows the batches handed to the durable pipeline.
 *
 * The old version of this screen listed the API's jobs and polled them every four seconds. In the
 * durable path no job is ever created: the API deposits the originals, posts a message and forgets
 * them, which is precisely what lets the batch survive the site being restarted or switched off.
 * A list of jobs would therefore stay empty for ever and quietly suggest nothing was happening.
 *
 * What genuinely exists instead is two things: the receipt this browser kept of what it sent, and
 * the state of each file in SharePoint, readable by name. So the screen joins them — a batch is a
 * list of names, and each name can be asked where it is. Tracking is on demand rather than on a
 * timer because every check is three CAML queries against SharePoint, and a batch of twenty on a
 * four-second poll would flatten the free-tier worker for no benefit: these stages take minutes.
 *
 * The queue depth at the top answers the one question a receipt cannot: whether the pipeline has
 * even started on it yet. The system-wide view stays in the Pipeline tab; it is not repeated here.
 */

const fmtWhen = (iso: string) => new Date(iso).toLocaleString();

function StageDots({ track }: { track?: TrackResult }) {
  if (!track) return <span className="muted small">stato non richiesto</span>;
  if (!track.ok) return <span className="errtext small">{track.error}</span>;
  const here = [...(track.stages ?? [])].reverse().find((s) => s.found);
  return (
    <span className="stage-dots">
      {track.stages?.map((s) => (
        <span key={s.stage} className={`sd ${s.found ? "on" : ""}`} title={s.label}>
          ●
        </span>
      ))}
      <span className="muted small">{here ? here.label : "non ancora arrivata"}</span>
    </span>
  );
}

export default function MonitorView() {
  const [batches, setBatches] = useState<DeliveredBatch[]>(() => listDeliveries());
  const [openId, setOpenId] = useState<string | null>(null);
  const [tracks, setTracks] = useState<Record<string, TrackResult>>({});
  const [tracking, setTracking] = useState<Set<string>>(new Set());
  const [queues, setQueues] = useState<Queues | null>(null);
  const [legacy, setLegacy] = useState<JobSummary[]>([]);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => setOpenId((id) => id ?? batches[0]?.id ?? null), [batches]);

  const refreshQueues = useCallback(async () => {
    try {
      setQueues(await api.queues());
    } catch {
      /* the backlog is a hint, not the subject of this screen: never fail the view over it */
    }
  }, []);

  useEffect(() => {
    refreshQueues();
    const t = setInterval(refreshQueues, 15_000);
    return () => clearInterval(t);
  }, [refreshQueues]);

  // The old in-API jobs are still readable until they are deleted. The section shows itself only
  // while some survive, so it retires on its own rather than lingering as a permanent empty box.
  useEffect(() => {
    api.listJobs(20).then(setLegacy).catch(() => setLegacy([]));
  }, []);

  const track = useCallback(async (file: DeliveredFile) => {
    setTracking((s) => new Set(s).add(file.trackName));
    try {
      const result = await api.track(file.trackName);
      setTracks((m) => ({ ...m, [file.trackName]: result }));
    } catch (e) {
      setTracks((m) => ({ ...m, [file.trackName]: { ok: false, error: (e as Error).message } }));
    } finally {
      setTracking((s) => {
        const n = new Set(s);
        n.delete(file.trackName);
        return n;
      });
    }
  }, []);

  /** Sequential on purpose: three SharePoint queries per file, and the free plan is one worker. */
  const trackBatch = async (batch: DeliveredBatch) => {
    for (const file of batch.files.filter((f) => f.ok)) await track(file);
  };

  const forget = (id: string) => {
    forgetDelivery(id);
    setBatches(listDeliveries());
  };

  const forgetAll = () => {
    if (!confirm("Svuotare l'elenco dei lotti consegnati? Le immagini restano nella pipeline, sparisce solo la ricevuta locale.")) return;
    clearDeliveries();
    setBatches([]);
  };

  const removeLegacy = async (jobId: string) => {
    if (!confirm("Eliminare questo job e i file generati? L'azione non è reversibile.")) return;
    try {
      await api.deleteJob(jobId);
      setLegacy(await api.listJobs(20));
    } catch (e) {
      setErr((e as Error).message);
    }
  };

  const waiting = queues?.queues.find((q) => q.name === "images-to-vectorize");
  const poison = queues?.queues.filter((q) => q.name.endsWith("-poison") && (q.count ?? 0) > 0) ?? [];

  return (
    <div className="monitor">
      {err && (
        <div className="notice err" role="alert">
          {err}
        </div>
      )}

      {queues?.configured && (
        <div className="notice" role="status">
          {waiting?.count ? (
            <>
              <strong>{waiting.count}</strong> {waiting.count === 1 ? "immagine è ancora in coda" : "immagini sono ancora in coda"} in attesa di
              essere tracciate.
            </>
          ) : (
            <>Nessuna immagine in attesa: la coda di ingresso è vuota, la pipeline ha preso in carico tutto.</>
          )}
          {poison.length > 0 && (
            <span className="errtext">
              {" "}· {poison.reduce((n, q) => n + (q.count ?? 0), 0)} messaggi in coda di scarto: la pipeline ci ha
              rinunciato. Vedi <strong>Pipeline</strong>.
            </span>
          )}
        </div>
      )}

      <div className="row between">
        <div className="section-title">Lotti consegnati ({batches.length})</div>
        {batches.length > 0 && (
          <button className="btn small" onClick={forgetAll}>
            Svuota elenco
          </button>
        )}
      </div>

      {batches.length === 0 && (
        <div className="empty">
          Nessun lotto consegnato da questo browser. Vai su <strong>Carica</strong> e premi{" "}
          <em>Consegna alla pipeline</em>.
          <div className="muted small" style={{ marginTop: 8 }}>
            L'elenco è una ricevuta tenuta in locale: le immagini vivono nella pipeline, non qui. Per cercarne una per
            nome, o per vedere lo stato complessivo, usa la scheda <strong>Pipeline</strong>.
          </div>
        </div>
      )}

      {batches.map((b) => {
        const open = openId === b.id;
        return (
          <div key={b.id} className={`batch ${open ? "open" : ""}`}>
            <button className="batch-head" onClick={() => setOpenId(open ? null : b.id)} aria-expanded={open}>
              <span className="batch-when">{fmtWhen(b.at)}</span>
              <span className={`chip ${b.mode === "vector" ? "s-dispatched" : "s-completed"}`}>
                {b.mode === "vector" ? "◆ vettoriale" : "▣ immagine"}
              </span>
              <span className="counts">
                <span className="ok">✓{b.accepted}</span>
                {b.rejected > 0 && <span className="err">✕{b.rejected}</span>}
                <span className="muted">
                  {b.files.length} {b.files.length === 1 ? "file" : "file"}
                </span>
              </span>
              <span className="batch-caret">{open ? "▾" : "▸"}</span>
            </button>

            {open && (
              <div className="batch-body">
                <div className="jobactions">
                  <button className="btn small" onClick={() => trackBatch(b)}>
                    Dove sono finite?
                  </button>
                  <button className="btn small danger" onClick={() => forget(b.id)}>
                    Togli dall'elenco
                  </button>
                </div>
                {b.files.map((file) => (
                  <div key={file.name} className="detail-item">
                    <div className="row between">
                      <div className="row" style={{ gap: 10 }}>
                        <strong>{file.name}</strong>
                        {!file.ok && <span className="errtext small">non consegnata: {file.error}</span>}
                        {file.ok && file.threshold != null && (
                          <span className="muted small">soglia {file.threshold}</span>
                        )}
                        {file.ok && file.threshold == null && b.mode === "vector" && (
                          <span className="muted small">soglia automatica</span>
                        )}
                      </div>
                      {file.ok && (
                        <button
                          className="btn small"
                          onClick={() => track(file)}
                          disabled={tracking.has(file.trackName)}
                        >
                          {tracking.has(file.trackName) ? "…" : "Dove si trova?"}
                        </button>
                      )}
                    </div>
                    {file.ok && <StageDots track={tracks[file.trackName]} />}
                  </div>
                ))}
              </div>
            )}
          </div>
        );
      })}

      {legacy.length > 0 && (
        <>
          <div className="section-title" style={{ marginTop: 24 }}>
            Job del percorso precedente ({legacy.length})
          </div>
          <div className="muted small" style={{ marginBottom: 8 }}>
            Elaborazioni rimaste dal tempo in cui il lavoro avveniva dentro l'API. Non ne nascono di nuove: la sezione
            sparisce quando li elimini.
          </div>
          {legacy.map((s) => (
            <div key={s.id} className="detail-item">
              <div className="row between">
                <div className="row" style={{ gap: 10 }}>
                  <code>{s.id.slice(0, 8)}</code>
                  <span className="muted small">{new Date(s.createdAt).toLocaleString()}</span>
                  <span className="muted small">{s.total} img</span>
                </div>
                <button className="btn small danger" onClick={() => removeLegacy(s.id)}>
                  Elimina
                </button>
              </div>
            </div>
          ))}
        </>
      )}
    </div>
  );
}

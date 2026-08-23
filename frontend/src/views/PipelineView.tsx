import { useCallback, useEffect, useRef, useState } from "react";
import { api, type Funnel, type Queues, type TrackResult, type PipelineItem } from "../api";
import StatusChip from "../components/StatusChip";

function StageDots({ track }: { track?: TrackResult }) {
  if (!track) return null;
  if (!track.ok) return <span className="err small">{track.error}</span>;
  const here = track.stages?.find((s) => s.found);
  return (
    <span className="stage-dots">
      {track.stages?.map((s) => (
        <span key={s.stage} className={`sd ${s.found ? "on" : ""}`} title={s.label}>●</span>
      ))}
      <span className="muted small">{here ? here.label : "non trovato"}</span>
    </span>
  );
}

export default function PipelineView() {
  const [funnel, setFunnel] = useState<Funnel | null>(null);
  const [queues, setQueues] = useState<Queues | null>(null);
  const [myItems, setMyItems] = useState<PipelineItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const loadingRef = useRef(false);

  const [tracks, setTracks] = useState<Record<string, TrackResult>>({});
  const [tracking, setTracking] = useState<Set<string>>(new Set());

  const [trackName, setTrackName] = useState("");
  const [manualTrack, setManualTrack] = useState<TrackResult | null>(null);
  const [manualBusy, setManualBusy] = useState(false);

  const load = useCallback(async () => {
    if (loadingRef.current) return;
    loadingRef.current = true;
    setLoading(true);
    const failures: string[] = [];
    await Promise.all([
      api.funnel().then(setFunnel).catch((e) => {
        const message = (e as Error).message;
        setFunnel({ ok: false, error: message });
        failures.push(`funnel: ${message}`);
      }),
      api.queues().then(setQueues).catch((e) => failures.push(`code: ${(e as Error).message}`)),
      api.pipelineItems().then(setMyItems).catch((e) => failures.push(`immagini: ${(e as Error).message}`)),
    ]);
    setLoadError(failures.length > 0 ? `Aggiornamento parziale — ${failures.join(" · ")}` : null);
    loadingRef.current = false;
    setLoading(false);
  }, []);

  useEffect(() => {
    load();
    const t = setInterval(load, 8000);
    return () => clearInterval(t);
  }, [load]);

  const trackItem = async (it: PipelineItem) => {
    setTracking((s) => new Set(s).add(it.itemId));
    try {
      const r = await api.track(it.trackName);
      setTracks((m) => ({ ...m, [it.itemId]: r }));
    } catch (e) {
      setTracks((m) => ({ ...m, [it.itemId]: { ok: false, error: (e as Error).message } }));
    } finally {
      setTracking((s) => { const n = new Set(s); n.delete(it.itemId); return n; });
    }
  };

  const runManualTrack = async () => {
    if (!trackName.trim()) return;
    setManualBusy(true);
    setManualTrack(null);
    try { setManualTrack(await api.track(trackName.trim())); }
    catch (e) { setManualTrack({ ok: false, error: (e as Error).message }); }
    finally { setManualBusy(false); }
  };

  const max = funnel?.stages?.reduce((m, s) => Math.max(m, s.count), 1) ?? 1;

  return (
    <div className="pipeline">
      {loadError && <div className="notice err" role="alert">{loadError}</div>}
      {/* MY IMAGES IN THE PIPELINE */}
      <div className="section-title">Le mie immagini nella pipeline ({myItems.length})</div>
      {!loading && myItems.length === 0 && (
        <div className="empty">
          Nessuna immagine inviata alla pipeline. Vai su <strong>Carica</strong>, elabora le immagini e premi
          <em> Invia alla pipeline</em>.
        </div>
      )}
      {myItems.length > 0 && (
        <div className="myitems">
          {myItems.map((it) => (
            <div key={it.itemId} className="myitem">
              <div className="myitem-main">
                <span className="fname" title={it.title || it.baseName}>{it.baseName}</span>
                <StatusChip status={it.status} />
                {it.metadataSource === "pipeline" && <span className="msrc pipe">metadati dal tuo sistema</span>}
              </div>
              <div className="myitem-track">
                <StageDots track={tracks[it.itemId]} />
                <button className="btn small" onClick={() => trackItem(it)} disabled={tracking.has(it.itemId)}>
                  {tracking.has(it.itemId) ? "…" : "Dove si trova?"}
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* FUNNEL */}
      <div className="row between">
        <div className="section-title">Funnel pipeline (SharePoint)</div>
        <button className="btn small" onClick={load} disabled={loading}>{loading ? "…" : "Aggiorna"}</button>
      </div>
      {funnel && !funnel.ok && (
        <div className="notice err">Funnel non disponibile: {funnel.error}
          <div className="muted small">Richiede pipeline abilitata e accesso a SharePoint.</div>
        </div>
      )}
      {funnel?.ok && funnel.stages && (
        <>
          <div className="stepper">
            {funnel.stages.map((s, i) => (
              <div key={s.key} className="stepper-node">
                <div className={`stepper-badge s${i}`}>{s.count.toLocaleString()}</div>
                <div className="stepper-label">{s.label}</div>
                {i < funnel.stages!.length - 1 && <div className="stepper-arrow">→</div>}
              </div>
            ))}
          </div>
          <div className="funnel">
            {funnel.stages.map((s, i) => (
              <div key={s.key} className="fstage">
                <div className="fstage-head">
                  <span className="fstep">{i + 1}</span>
                  <span className="flabel">{s.label}</span>
                  <span className="fcount">{s.count.toLocaleString()}</span>
                </div>
                <div className="fbar-track"><div className={`fbar s${i}`} style={{ width: `${(s.count / max) * 100}%` }} /></div>
              </div>
            ))}
          </div>
        </>
      )}

      {/* QUEUE DEPTHS */}
      <div className="section-title">Profondità code Azure</div>
      {queues && !queues.configured && (
        <div className="notice">Storage non configurato — imposta <code>Pipeline:StorageConnectionString</code> per vedere il backlog delle code.</div>
      )}
      {queues?.configured && (
        <div className="tiles">
          {queues.queues.map((q) => (
            <div key={q.name} className={`tile ${q.error ? "err" : q.count && q.count > 0 ? "" : "ok"}`}>
              <div className="tile-title">{q.name}</div>
              <div className="tile-value">{q.error ? "—" : q.count}</div>
              <div className="tile-sub">{q.error ?? "messaggi in coda"}</div>
            </div>
          ))}
        </div>
      )}

      {/* MANUAL TRACK */}
      <div className="section-title">Traccia un file per nome</div>
      <div className="row" style={{ gap: 8 }}>
        <input className="track-input" placeholder="es. silhouette_danza_001.jpg" value={trackName}
          onChange={(e) => setTrackName(e.target.value)} onKeyDown={(e) => e.key === "Enter" && runManualTrack()} />
        <button className="btn" onClick={runManualTrack} disabled={manualBusy}>{manualBusy ? "Cerco…" : "Cerca"}</button>
      </div>
      {manualTrack && !manualTrack.ok && <div className="notice err">{manualTrack.error}</div>}
      {manualTrack?.ok && manualTrack.stages && (
        <div className="track-result">
          {manualTrack.stages.map((s) => (
            <div key={s.stage} className={`track-stage ${s.found ? "here" : ""}`}>
              <span className="tdot">{s.found ? "●" : "○"}</span>
              <span className="flabel">{s.label}</span>
              <span className="muted small">{s.found ? `presente${s.modified ? " · " + s.modified : ""}` : "assente"}</span>
            </div>
          ))}
          {!manualTrack.stages.some((s) => s.found) && <div className="muted small">File non trovato in nessuno stadio.</div>}
        </div>
      )}
    </div>
  );
}

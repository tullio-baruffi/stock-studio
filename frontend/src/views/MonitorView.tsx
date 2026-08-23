import { useEffect, useRef, useState, useCallback } from "react";
import { api, downloadFile, type Item, type Job, type JobSummary, STATUS_LABELS } from "../api";
import StatusChip from "../components/StatusChip";

const fmtTime = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "—");
const fmtDur = (ms?: number | null) => (ms == null ? "—" : ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`);

function ProgressBar({ s }: { s: JobSummary }) {
  const seg = (n: number, cls: string) =>
    n > 0 ? <span className={`seg ${cls}`} style={{ flex: n }} title={`${cls}: ${n}`} /> : null;
  return (
    <div className="pbar">
      {seg(s.completed, "completed")}
      {seg(s.published, "published")}
      {seg(s.dispatched, "dispatched")}
      {seg(s.processing, "processing")}
      {seg(s.queued, "queued")}
      {seg(s.failed, "failed")}
    </div>
  );
}

function StepTracker({ it }: { it: Item }) {
  const steps = [
    { label: "Caricata", at: it.steps.queuedAt },
    { label: "Vettorializzata", at: it.steps.vectorizedAt },
    { label: "Metadati", at: it.steps.completedAt },
    { label: "Inviata", at: it.steps.dispatchedAt },
    { label: "Pubblicata", at: it.steps.publishedAt },
  ];
  const firstPending = steps.findIndex((s) => !s.at);
  const publishFailed = it.publishStatus === "publish_failed";
  return (
    <div className="tracker">
      {steps.map((s, i) => {
        const done = !!s.at;
        const isCurrent = !done && i === firstPending && it.status !== "failed";
        const failedHere = (it.status === "failed" && i === firstPending) || (publishFailed && s.label === "Pubblicata");
        const cls = done ? "done" : failedHere ? "failed" : isCurrent ? "current" : "todo";
        return (
          <div key={s.label} className={`step ${cls}`}>
            <span className="dot">{done ? "✓" : failedHere ? "✕" : i + 1}</span>
            <span className="slabel">{s.label}</span>
            {done && <span className="stime">{fmtTime(s.at)}</span>}
          </div>
        );
      })}
    </div>
  );
}

export default function MonitorView() {
  const [jobs, setJobs] = useState<JobSummary[]>([]);
  const [selected, setSelected] = useState<Job | null>(null);
  const [selId, setSelId] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [limit, setLimit] = useState(50);
  const [hasMore, setHasMore] = useState(false);
  const [detailLoading, setDetailLoading] = useState(false);
  const selectedIdRef = useRef<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const loaded = await api.listJobs(limit + 1);
      setHasMore(loaded.length > limit);
      setJobs(loaded.slice(0, limit));
      const activeId = selectedIdRef.current;
      if (activeId) {
        const detail = await api.getJob(activeId);
        if (selectedIdRef.current === activeId) setSelected(detail);
      }
      setErr(null);
    } catch (e) {
      setErr((e as Error).message);
    }
  }, [limit]);

  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 4000);
    return () => clearInterval(t);
  }, [refresh]);

  const open = async (id: string) => {
    selectedIdRef.current = id;
    setSelId(id);
    setSelected(null);
    setDetailLoading(true);
    setErr(null);
    try {
      const detail = await api.getJob(id);
      if (selectedIdRef.current === id) setSelected(detail);
    } catch (e) {
      if (selectedIdRef.current === id) setErr((e as Error).message);
    } finally {
      if (selectedIdRef.current === id) setDetailLoading(false);
    }
  };

  const retry = async (jobId: string, itemId: string) => {
    try {
      await api.retryItem(jobId, itemId);
      const detail = await api.getJob(jobId);
      if (selectedIdRef.current === jobId) setSelected(detail);
    } catch (e) {
      setErr((e as Error).message);
    }
  };

  const removeJob = async (jobId: string) => {
    if (!confirm("Eliminare questo job e i file generati? L'azione non è reversibile.")) return;
    try {
      await api.deleteJob(jobId);
      if (selectedIdRef.current === jobId) {
        selectedIdRef.current = null;
        setSelId(null);
        setSelected(null);
      }
      await refresh();
    } catch (e) {
      setErr((e as Error).message);
    }
  };

  const redispatch = async (jobId: string) => {
    try {
      const { job } = await api.dispatch(jobId);
      if (selectedIdRef.current === jobId) setSelected(job);
      await refresh();
    } catch (e) {
      setErr((e as Error).message);
    }
  };

  const download = async (url: string, name: string) => {
    try {
      await downloadFile(url, name);
    } catch (e) {
      setErr((e as Error).message);
    }
  };

  return (
    <div className="monitor">
      {err && <div className="notice err" role="alert">{err}</div>}
      <div className="status-legend">
        {Object.entries(STATUS_LABELS).map(([k, v]) => (
          <span key={k} className={`chip ${v.cls}`}><span className="chip-icon">{v.icon}</span> {v.label}</span>
        ))}
      </div>
      <div className="mon-grid">
        <div className="joblist">
          <div className="section-title">Job recenti ({jobs.length})</div>
          {jobs.length === 0 && <div className="empty">Nessun job ancora. Carica delle immagini.</div>}
          {jobs.map((s) => (
            <button key={s.id} className={`jobrow ${selId === s.id ? "sel" : ""}`} onClick={() => open(s.id)}>
              <div className="row between">
                <code>{s.id.slice(0, 8)}</code>
                <span className="muted">{new Date(s.createdAt).toLocaleString()}</span>
              </div>
              <ProgressBar s={s} />
              <div className="counts">
                <span>{s.total} img</span>
                {s.completed > 0 && <span className="ok">✓{s.completed}</span>}
                {s.published > 0 && <span className="ok">★{s.published}</span>}
                {s.dispatched > 0 && <span className="disp">↗{s.dispatched}</span>}
                {s.processing > 0 && <span className="proc">⟳{s.processing}</span>}
                {s.queued > 0 && <span className="q">…{s.queued}</span>}
                {s.failed > 0 && <span className="err">✕{s.failed}</span>}
              </div>
            </button>
          ))}
          {hasMore && (
            <button className="btn" onClick={() => setLimit((n) => n + 50)}>
              Carica altri 50 job
            </button>
          )}
        </div>

        <div className="jobdetail">
          {detailLoading && <div className="empty" role="status">Caricamento dettaglio job…</div>}
          {!selected && !detailLoading && <div className="empty">Seleziona un job per vedere i dettagli e gli step.</div>}
          {selected && (
            <>
              <div className="section-title">Dettaglio job <code>{selected.id.slice(0, 8)}</code></div>
              <div className="jobactions">
                <button className="btn small" onClick={() => download(`/api/jobs/${selected.id}/export/adobe`, `adobe_${selected.id.slice(0, 8)}.csv`)}>CSV Adobe</button>
                <button className="btn small" onClick={() => download(`/api/jobs/${selected.id}/export/freepik`, `freepik_${selected.id.slice(0, 8)}.csv`)}>CSV Freepik</button>
                <button className="btn small" onClick={() => download(`/api/jobs/${selected.id}/export/bundle`, `stock_${selected.id.slice(0, 8)}.zip`)}>Bundle .zip</button>
                <button className="btn small accent" onClick={() => redispatch(selected.id)}>Ri-invia alla pipeline</button>
                <button className="btn small danger" onClick={() => removeJob(selected.id)}>Elimina job</button>
              </div>
              {selected.items.map((it) => (
                <div key={it.id} className="detail-item">
                  <div className="row between">
                    <div className="row" style={{ gap: 10 }}>
                      <strong>{it.baseName}</strong>
                      <StatusChip status={it.status} />
                    </div>
                    <span className="muted">durata: {fmtDur(it.durationMs)}</span>
                  </div>
                  <StepTracker it={it} />
                  {it.status === "failed" && (
                    <div className="fail-row">
                      <span className="errtext">⚠ {it.error}</span>
                      <button className="btn small" onClick={() => retry(selected.id, it.id)}>Riprova</button>
                    </div>
                  )}
                  {it.title && (
                    <div className="muted small">
                      {it.title}
                      {it.metadataSource === "pipeline" && <span className="msrc pipe" style={{ marginLeft: 8 }}>da pipeline</span>}
                    </div>
                  )}
                </div>
              ))}
            </>
          )}
        </div>
      </div>
    </div>
  );
}

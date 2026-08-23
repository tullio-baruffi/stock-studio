import { useCallback, useEffect, useRef, useState } from "react";
import { api, downloadFile, isTerminal, type Item, type Job, type PipelineStatus } from "../api";
import StatusChip from "../components/StatusChip";
import ValidationPanel from "../components/ValidationPanel";
import AuthImage from "../components/AuthImage";

export default function UploadView({ pipeline }: { pipeline: PipelineStatus | null }) {
  const [job, setJob] = useState<Job | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const [dispatching, setDispatching] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [dispatchResult, setDispatchResult] = useState<string | null>(null);
  const [thresholds, setThresholds] = useState<Record<string, number>>({});
  const [revecting, setRevecting] = useState<Set<string>>(new Set());
  const [bulkKw, setBulkKw] = useState("");
  const [bulkApplying, setBulkApplying] = useState(false);
  const [mode, setMode] = useState<"vector" | "raster">("vector");
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [noteSaved, setNoteSaved] = useState<Set<string>>(new Set());
  const inputRef = useRef<HTMLInputElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!showConfirm) return;
    cancelRef.current?.focus();
    const closeOnEscape = (e: KeyboardEvent) => {
      if (e.key === "Escape") setShowConfirm(false);
    };
    document.addEventListener("keydown", closeOnEscape);
    return () => document.removeEventListener("keydown", closeOnEscape);
  }, [showConfirm]);

  const process = useCallback(async (files: File[]) => {
    if (busy) {
      setError("Attendi il completamento del job corrente prima di caricare altre immagini.");
      return;
    }
    const images = files.filter((f) => /image\/(jpe?g|png|webp|tiff?)/i.test(f.type) || /\.(jpe?g|png|webp|tiff?)$/i.test(f.name));
    if (images.length === 0) return setError("Trascina immagini JPEG/PNG/WEBP/TIFF.");
    setError(null);
    setBusy(true);
    try {
      const created = await api.createJob(images, mode);
      setJob(created);
      // Background processing: poll until terminal, with a bound so a stuck job can't loop forever.
      let current = created;
      for (let attempts = 0; attempts < 400 && current.items.some((i) => !isTerminal(i.status)); attempts++) {
        await new Promise((r) => setTimeout(r, 1500));
        current = await api.getJob(created.id);
        setJob(current);
      }
      if (current.items.some((i) => !isTerminal(i.status))) {
        setError("L'elaborazione dura da oltre 10 minuti e continua in background. Seguila nella scheda Monitoraggio.");
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }, [busy, mode]);

  const patchItem = (itemId: string, patch: Partial<Item>) =>
    setJob((j) => (j ? { ...j, items: j.items.map((it) => (it.id === itemId ? { ...it, ...patch } : it)) } : j));

  const save = async (item: Item) => {
    if (!job) return;
    try {
      patchItem(item.id, await api.updateItem(job.id, item));
    } catch (e) {
      setError((e as Error).message);
    }
  };

  /**
   * Sends the note together with the current metadata: the review process needs both, because
   * the lesson is the difference between generated and corrected values, and the note explains it.
   */
  const saveNote = async (item: Item) => {
    if (!job) return;
    const note = (notes[item.id] ?? "").trim();
    if (!note) return;
    try {
      patchItem(item.id, await api.updateItem(job.id, item, note));
      setNotes((m) => ({ ...m, [item.id]: "" }));
      setNoteSaved((s) => new Set(s).add(item.id));
      setTimeout(() => setNoteSaved((s) => {
        const next = new Set(s);
        next.delete(item.id);
        return next;
      }), 2500);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const revector = async (item: Item, threshold: number | null) => {
    if (!job) return;
    setRevecting((s) => new Set(s).add(item.id));
    try {
      patchItem(item.id, await api.revectorize(job.id, item.id, threshold));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setRevecting((s) => { const n = new Set(s); n.delete(item.id); return n; });
    }
  };

  const applyBulkKeywords = async (mode: "add" | "remove") => {
    if (!job || !bulkKw.trim()) return;
    setError(null);
    setBulkApplying(true);
    const kws = bulkKw.split(",").map((k) => k.trim().toLowerCase()).filter(Boolean);
    const failed: string[] = [];
    try {
      for (const it of job.items) {
        let next: string[];
        if (mode === "add") {
          const set = new Set(it.keywords.map((k) => k.toLowerCase()));
          next = [...it.keywords, ...kws.filter((k) => !set.has(k))];
        } else {
          const rm = new Set(kws);
          next = it.keywords.filter((k) => !rm.has(k.toLowerCase()));
        }
        try {
          patchItem(it.id, await api.updateItem(job.id, { ...it, keywords: next }));
        } catch {
          failed.push(it.baseName);
        }
      }
      if (failed.length > 0)
        setError(`Keyword non aggiornate per ${failed.length} immagini: ${failed.slice(0, 3).join(", ")}.`);
      else
        setBulkKw("");
    } finally {
      setBulkApplying(false);
    }
  };

  const dispatch = async () => {
    if (!job) return;
    setError(null);
    setShowConfirm(false);
    setDispatching(true);
    try {
      const { job: updated, results } = await api.dispatch(job.id);
      setJob(updated);
      const sent = results.filter((r) => r.ok).length;
      const failed = results.filter((r) => !r.ok);
      if (sent > 0) {
        setDispatchResult(
          `${sent} ${sent === 1 ? "immagine inviata" : "immagini inviate"} alla pipeline. ` +
            "I titoli e le keyword definitivi arriveranno automaticamente dalla pipeline; puoi seguirne lo stato nelle schede Monitoraggio e Pipeline."
        );
      }
      if (failed.length > 0) {
        setError(`${failed.length} non inviate: ${failed[0].error ?? "errore sconosciuto"}`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setDispatching(false);
    }
  };

  const download = async (url: string, name: string) => {
    try {
      await downloadFile(url, name);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const completed = job?.items.filter((i) => i.status === "completed" || i.status === "dispatched").length ?? 0;

  return (
    <>
      <div className="modebar">
        <span className="muted small">Cosa vuoi caricare?</span>
        <button className={`modebtn ${mode === "vector" ? "on" : ""}`} onClick={() => setMode("vector")}>
          <strong>◆ Vettoriale</strong>
          <span>traccia in SVG + EPS (+AI con Illustrator)</span>
        </button>
        <button className={`modebtn ${mode === "raster" ? "on" : ""}`} onClick={() => setMode("raster")}>
          <strong>▣ Immagine</strong>
          <span>foto e grafiche già pronte, nessun tracciato</span>
        </button>
      </div>

      <section
        className={`drop ${dragOver ? "over" : ""} ${busy ? "busy" : ""}`}
        role="button"
        tabIndex={0}
        aria-disabled={busy}
        aria-label={`Seleziona immagini. Modalità ${mode === "vector" ? "vettoriale" : "immagine"}.`}
        onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={(e) => { e.preventDefault(); setDragOver(false); process(Array.from(e.dataTransfer.files)); }}
        onClick={() => inputRef.current?.click()}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            inputRef.current?.click();
          }
        }}
      >
        <input ref={inputRef} type="file" accept="image/jpeg,image/png,image/webp,image/tiff" multiple hidden disabled={busy}
          onChange={(e) => process(Array.from(e.target.files ?? []))} />
        <div className="drop-inner">
          <div className="drop-icon">⬆</div>
          <div><strong>Trascina qui le immagini</strong> oppure clicca per selezionare</div>
          <div className="hint">
            {mode === "vector"
              ? "Modalità vettoriale · produce SVG, EPS e JPG"
              : "Modalità immagine · nessuna vettorializzazione, l'immagine resta com'è"}
          </div>
        </div>
      </section>

      {busy && <div className="notice" role="status">Elaborazione in corso…</div>}
      {error && <div className="notice err" role="alert">{error}</div>}

      {job && (
        <>
          <div className="toolbar">
            <div>Job <code>{job.id.slice(0, 8)}</code> · {completed}/{job.items.length} pronti</div>
            <div className="actions">
              <button className="btn" onClick={() => download(`/api/jobs/${job.id}/export/adobe`, `adobe_${job.id.slice(0, 8)}.csv`)}>CSV Adobe Stock</button>
              <button className="btn" onClick={() => download(`/api/jobs/${job.id}/export/freepik`, `freepik_${job.id.slice(0, 8)}.csv`)}>CSV Freepik</button>
              <button className="btn primary" onClick={() => download(`/api/jobs/${job.id}/export/bundle`, `stock_${job.id.slice(0, 8)}.zip`)}>Bundle .zip</button>
              {pipeline?.enabled && (
                <button className="btn accent" onClick={() => setShowConfirm(true)} disabled={dispatching}>
                  {dispatching ? "Invio…" : "Invia alla pipeline"}
                </button>
              )}
            </div>
          </div>

          {dispatchResult && (
            <div className="notice success">
              ✓ {dispatchResult}
              <button className="notice-close" onClick={() => setDispatchResult(null)}>×</button>
            </div>
          )}

          {job.items.length > 1 && (
            <div className="bulkbar">
              <span className="muted small">Keyword su tutte ({job.items.length}):</span>
              <input className="track-input" placeholder="es. inverno, festivo, 2027" value={bulkKw}
                onChange={(e) => setBulkKw(e.target.value)} />
              <button className="btn small" onClick={() => applyBulkKeywords("add")} disabled={bulkApplying}>
                {bulkApplying ? "…" : "+ Aggiungi"}
              </button>
              <button className="btn small" onClick={() => applyBulkKeywords("remove")} disabled={bulkApplying}>
                {bulkApplying ? "…" : "− Rimuovi"}
              </button>
            </div>
          )}

          <div className="grid">
            {job.items.map((it) => (
              <article key={it.id} className="card">
                <div className="preview">
                  {it.previewUrl ? <AuthImage src={it.previewUrl} alt={`Anteprima: ${it.title || it.baseName}`} /> : <div className="noimg">…</div>}
                </div>
                <div className="meta">
                  <div className="row between">
                    <span className="fname" title={it.originalFileName}>{it.baseName}</span>
                    <StatusChip status={it.status} />
                  </div>
                  <div className="meta-source">
                    {it.metadataSource === "pipeline"
                      ? <span className="msrc pipe">✓ metadati dalla pipeline</span>
                      : <span className="msrc prov">metadati provvisori · definitivi dalla pipeline dopo l'invio</span>}
                  </div>
                  <label>Titolo</label>
                  <input value={it.title} onChange={(e) => patchItem(it.id, { title: e.target.value })} onBlur={() => save(it)} />
                  <label>Keyword ({it.keywords.length})</label>
                  <textarea rows={3} value={it.keywords.join(", ")}
                    onChange={(e) => patchItem(it.id, { keywords: e.target.value.split(",").map((k) => k.trim()).filter(Boolean) })}
                    onBlur={() => save(it)} />
                  <label>Categoria</label>
                  <input value={it.category} onChange={(e) => patchItem(it.id, { category: e.target.value })} onBlur={() => save(it)} />
                  {it.validation && <ValidationPanel v={it.validation} />}
                  <div className="fb">
                    <label htmlFor={`fb-${it.id}`}>
                      Perché hai corretto i metadati? <span className="opt">facoltativo</span>
                    </label>
                    <textarea
                      id={`fb-${it.id}`}
                      rows={2}
                      placeholder="Es. il titolo era troppo generico, e su Adobe Stock 'clipart' non porta vendite."
                      value={notes[it.id] ?? ""}
                      onChange={(e) => setNotes((m) => ({ ...m, [it.id]: e.target.value }))}
                    />
                    <div className="row between">
                      <small>
                        {it.feedback
                          ? <>Ultima nota: <em>{it.feedback}</em></>
                          : it.editedFromAi
                            ? "Modifiche registrate. Una nota le rende molto più utili alla revisione."
                            : "Le note alimentano la revisione del prompt."}
                      </small>
                      <button
                        className="btn small"
                        onClick={() => saveNote(it)}
                        disabled={!(notes[it.id] ?? "").trim()}
                      >
                        {noteSaved.has(it.id) ? "salvata" : "Invia nota"}
                      </button>
                    </div>
                  </div>
                  {it.mode !== "raster" && (
                  <div className="revector">
                    <label>Soglia B/N: {thresholds[it.id] ?? "auto"}</label>
                    <div className="row" style={{ gap: 8 }}>
                      <input type="range" min={0} max={255} value={thresholds[it.id] ?? 128}
                        onChange={(e) => setThresholds((m) => ({ ...m, [it.id]: Number(e.target.value) }))} />
                      <button className="btn small" onClick={() => revector(it, thresholds[it.id] ?? null)} disabled={revecting.has(it.id)}>
                        {revecting.has(it.id) ? "…" : "Rigenera"}
                      </button>
                      <button className="btn small" onClick={() => revector(it, null)} disabled={revecting.has(it.id)} title="Torna a soglia automatica">auto</button>
                    </div>
                  </div>
                  )}
                  <div className="files">
                    {it.files.svg && <button onClick={() => download(it.files.svg!, `${it.baseName}.svg`)}>SVG</button>}
                    {it.files.ai && <button onClick={() => download(it.files.ai!, `${it.baseName}.ai`)}>AI</button>}
                    {it.files.eps && <button onClick={() => download(it.files.eps!, `${it.baseName}.eps`)}>EPS</button>}
                    {it.files.jpg && <button onClick={() => download(it.files.jpg!, `${it.baseName}.jpg`)}>JPG</button>}
                  </div>
                </div>
              </article>
            ))}
          </div>
        </>
      )}

      {showConfirm && (
        <div className="modal-backdrop" onClick={() => setShowConfirm(false)} role="presentation">
          <div className="modal" onClick={(e) => e.stopPropagation()} role="dialog" aria-modal="true" aria-labelledby="dispatch-title">
            <h3 id="dispatch-title">Inviare alla pipeline?</h3>
            <p>
              Le immagini pronte verranno depositate nel back-office e la pipeline le
              <strong> classificherà, taggerà e caricherà su Adobe Stock e Freepik</strong>.
            </p>
            <ul className="modal-list">
              <li>I <strong>titoli e le keyword definitivi</strong> arriveranno dalla pipeline e sostituiranno quelli provvisori.</li>
              <li>Puoi seguire l'avanzamento in <strong>Monitoraggio</strong> e <strong>Pipeline</strong>.</li>
            </ul>
            <div className="modal-actions">
              <button ref={cancelRef} className="btn" onClick={() => setShowConfirm(false)}>Annulla</button>
              <button className="btn accent" onClick={dispatch}>Sì, invia</button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

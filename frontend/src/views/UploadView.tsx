import { useCallback, useEffect, useRef, useState } from "react";
import { api, type HandoffResponse, type PipelineStatus } from "../api";
import { recordDelivery, trackNameFor, type DeliveredFile } from "../deliveries";
import TracePreview from "../components/TracePreview";

/**
 * Hands pictures to the durable pipeline.
 *
 * This screen used to run the whole process while the author watched: it created a job, traced each
 * picture inside the API, generated provisional metadata, let them be edited, and only then pushed
 * the result downstream. It worked, and it was fragile in a specific way — the batch lived in the
 * web application's memory, so a deploy, a plan change or the free tier going to sleep froze it
 * halfway, and the author was left watching a screen that would never finish.
 *
 * Now the API only deposits the originals and posts one queue message each. There is nothing to
 * watch, because there is nothing happening here: the Function traces, the pipeline classifies and
 * writes the metadata, and the results turn up in the Backoffice a few minutes later. So the screen
 * says what it can honestly say — what is about to be sent, and that it has been sent — and stops
 * pretending to be a progress display for work happening somewhere else.
 *
 * The one judgement kept from the old screen is the tracing threshold, because it is the only thing
 * the author can see that the machine cannot. It is now decided in the browser, on a canvas, and
 * travels as a number in the queue message.
 */

type Staged = {
  id: string;
  file: File;
  /** null leaves the cut to Otsu, inside the Function, on the full-size original. */
  threshold: number | null;
  /** Otsu computed locally: only the slider's resting position, never what gets sent. */
  auto: number | null;
};

const IMAGE_NAME = /\.(jpe?g|png|webp|tiff?)$/i;
const IMAGE_TYPE = /image\/(jpe?g|png|webp|tiff?)/i;

let seq = 0;
const nextId = () => `${Date.now().toString(36)}-${++seq}`;

/** Il nome della modalità in italiano, scritto in un posto solo perché compare in tre punti. */
function nomeModalita(mode: "vector" | "colore" | "raster"): string {
  return mode === "vector" ? "vettoriale in bianco e nero"
    : mode === "colore" ? "vettoriale a colori"
    : "immagine";
}

const fmtSize = (bytes: number) =>
  bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;

/**
 * Quanto unire le tinte che descrivono la stessa cosa.
 *
 * Tre posizioni con un nome, invece di un cursore su un numero astratto: il valore grezzo non
 * significa niente per chi guarda un disegno, e le posizioni intermedie non corrisponderebbero a
 * nessuna scelta reale.
 *
 * I valori vengono dalla misura su illustrazioni vere del portfolio, provando la scala completa da
 * 0 a 2200. Due cose che si sono viste e che spiegano perché le posizioni sono queste:
 * - sotto 700 non succede niente, quindi non ha senso una posizione più timida di «prudente»;
 * - **sopra la consigliata non cambia più nulla**, perché a limitare la fusione non è questa
 *   soglia ma il controllo sui pixel dell'originale e il tetto percettivo dei 22 livelli. Una
 *   posizione «decisa» sarebbe stata un pulsante che non fa niente, e non è stata messa.
 */
const UNIONE_CONSIGLIATA = 1300;

const UNIONI: { valore: number; nome: string; spiega: string }[] = [
  {
    valore: 0,
    nome: "Nessuna",
    spiega: "Nessuna unione: la tavolozza resta com'esce. Serve a vedere il difetto in chiaro quando qualcosa non torna.",
  },
  {
    valore: 900,
    nome: "Prudente",
    spiega: "Unisce solo i casi più evidenti. Da usare se un disegno perde una sfumatura che invece c'era.",
  },
  {
    valore: UNIONE_CONSIGLIATA,
    nome: "Consigliata",
    spiega: "Il valore misurato: toglie i manti a chiazze e i contorni tracciati due volte, senza toccare le ombreggiature vere. Alzarlo oltre non cambia il risultato: a limitare la fusione è il controllo sui pixel dell'originale, non questa soglia.",
  },
];

export default function UploadView({
  pipeline,
  onNavigate,
}: {
  pipeline: PipelineStatus | null;
  onNavigate?: (tab: "backoffice" | "monitor") => void;
}) {
  const [mode, setMode] = useState<"vector" | "colore" | "raster">("vector");
  /**
   * Quante tinte nel tracciato a colori.
   *
   * Sedici e' il numero misurato, non una preferenza: sotto, spariscono i dettagli piccoli e
   * colorati -- guance, miele, bollicine -- che sono poi quelli che l'occhio cerca per primi.
   *
   * E' un tetto, non una promessa. Le tinte che descrivono una frangia di contorno invece di una
   * zona vengono scartate, e su certi disegni sono quasi tutte: misurato, chiedendone 48 ne
   * escono 6. Su un'illustrazione con oggetti ombreggiati invece le tinte in piu' vanno a
   * spezzare l'ombreggiatura in bande sempre piu' sottili -- sono vere, ma il file cresce senza
   * che si veda granche'.
   */
  const [colori, setColori] = useState(24);
  /**
   * Quanto unire le tinte che descrivono la stessa cosa.
   *
   * È l'unico numero **empirico** della vettorializzazione a colori: gli altri sono limiti
   * percettivi o cambi di segno, questo è un taglio scelto guardando i dati di 52 illustrazioni.
   * Sta qui, e non solo nella configurazione, perché è quello che potrebbe aver bisogno di una
   * correzione su un disegno diverso da quelli su cui è stato misurato — e correggerlo dalla
   * pagina di caricamento costa un clic invece di un rilascio.
   */
  const [unione, setUnione] = useState(UNIONE_CONSIGLIATA);
  const [staged, setStaged] = useState<Staged[]>([]);
  const [delivering, setDelivering] = useState(false);
  const [result, setResult] = useState<HandoffResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
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

  const add = useCallback(
    (files: File[]) => {
      const images = files.filter((f) => IMAGE_TYPE.test(f.type) || IMAGE_NAME.test(f.name));
      if (images.length === 0) {
        setError("Trascina immagini JPEG/PNG/WEBP/TIFF.");
        return;
      }
      // The same file dropped twice would become two queue messages, two traced silhouettes and
      // two items to clean up in the Backoffice.
      const seen = new Set(staged.map((s) => `${s.file.name}:${s.file.size}`));
      const fresh = images.filter((f) => !seen.has(`${f.name}:${f.size}`));
      const skipped = images.length - fresh.length;

      setResult(null);
      setError(
        skipped > 0
          ? `${skipped === 1 ? "Un'immagine era già" : `${skipped} immagini erano già`} in elenco: non ${
              skipped === 1 ? "è stata aggiunta" : "sono state aggiunte"
            } di nuovo.`
          : null
      );
      if (fresh.length > 0)
        setStaged((prev) => [...prev, ...fresh.map((file) => ({ id: nextId(), file, threshold: null, auto: null }))]);
    },
    [staged]
  );

  const patch = (id: string, p: Partial<Staged>) =>
    setStaged((prev) => prev.map((s) => (s.id === id ? { ...s, ...p } : s)));

  const remove = (id: string) => setStaged((prev) => prev.filter((s) => s.id !== id));

  const deliver = async () => {
    if (staged.length === 0) return;
    setShowConfirm(false);
    setError(null);
    setDelivering(true);
    try {
      const res = await api.handoff(
        staged.map((s) => s.file),
        mode,
        mode === "vector" ? staged.map((s) => s.threshold) : undefined,
        mode === "colore" ? colori : undefined,
        mode === "colore" ? unione : undefined
      );

      const failedBy = new Map((res.errors ?? []).map((e) => [e.file, e.error]));
      recordDelivery({
        id: nextId(),
        at: new Date().toISOString(),
        mode,
        accepted: res.accepted,
        rejected: res.rejected,
        files: staged.map<DeliveredFile>((s) => ({
          name: s.file.name,
          trackName: trackNameFor(s.file.name),
          threshold: mode === "vector" ? s.threshold : null,
          ok: !failedBy.has(s.file.name),
          error: failedBy.get(s.file.name),
        })),
      });

      setResult(res);
      // Only the rejected ones stay on screen: they are the only ones still worth a second attempt,
      // and clearing them would hide a failure behind a success message.
      setStaged((prev) => prev.filter((s) => failedBy.has(s.file.name)));
      if (res.rejected > 0)
        setError(
          `${res.rejected} non consegnate e rimaste in elenco: ${res.errors?.[0]?.error ?? "errore sconosciuto"}`
        );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setDelivering(false);
    }
  };

  const storageOff = pipeline != null && !pipeline.canEnqueue;

  return (
    <>
      <div className="modebar">
        <span className="muted small">Cosa vuoi consegnare?</span>
        <button className={`modebtn ${mode === "vector" ? "on" : ""}`} onClick={() => setMode("vector")}>
          <strong>◆ Vettoriale B/N</strong>
          <span>silhouette: una soglia, un tracciato</span>
        </button>
        <button className={`modebtn ${mode === "colore" ? "on" : ""}`} onClick={() => setMode("colore")}>
          <strong>◈ Vettoriale a colori</strong>
          <span>un tracciato per tinta, SVG + EPS a colori</span>
        </button>
        <button className={`modebtn ${mode === "raster" ? "on" : ""}`} onClick={() => setMode("raster")}>
          <strong>▣ Immagine</strong>
          <span>foto e grafiche già pronte, nessun tracciato</span>
        </button>
      </div>

      {storageOff && (
        <div className="notice err" role="alert">
          Storage della pipeline non configurato: senza <code>Pipeline:StorageConnectionString</code> non c'è nessuna
          coda a cui consegnare il lavoro.
        </div>
      )}

      <section
        className={`drop ${dragOver ? "over" : ""} ${delivering ? "busy" : ""}`}
        role="button"
        tabIndex={0}
        aria-disabled={delivering}
        aria-label={`Seleziona immagini. Modalità ${nomeModalita(mode)}.`}
        onDragOver={(e) => {
          e.preventDefault();
          setDragOver(true);
        }}
        onDragLeave={() => setDragOver(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragOver(false);
          add(Array.from(e.dataTransfer.files));
        }}
        onClick={() => inputRef.current?.click()}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            inputRef.current?.click();
          }
        }}
      >
        <input
          ref={inputRef}
          type="file"
          accept="image/jpeg,image/png,image/webp,image/tiff"
          multiple
          hidden
          disabled={delivering}
          onChange={(e) => {
            add(Array.from(e.target.files ?? []));
            // Without this, choosing the same file again after removing it fires no change event.
            e.target.value = "";
          }}
        />
        <div className="drop-inner">
          <div className="drop-icon">⬆</div>
          <div>
            <strong>Trascina qui le immagini</strong> oppure clicca per selezionare
          </div>
          <div className="hint">
            {mode === "vector"
              ? "Modalità vettoriale in bianco e nero · silhouette in SVG, EPS e JPG"
              : mode === "colore"
              ? `Modalità vettoriale a colori · ${colori} tinte · unione ${UNIONI.find((u) => u.valore === unione)?.nome.toLowerCase() ?? "consigliata"} · SVG ed EPS a colori`
              : "Modalità immagine · nessuna vettorializzazione, l'immagine resta com'è"}
          </div>
        </div>
      </section>

      {error && (
        <div className="notice err" role="alert">
          {error}
        </div>
      )}

      {result && (
        <div className="notice success handoff-done" role="status">
          <button className="notice-close" onClick={() => setResult(null)} aria-label="Chiudi">
            ×
          </button>
          <strong>
            ✓ {result.accepted} {result.accepted === 1 ? "immagine consegnata" : "immagini consegnate"} alla pipeline
          </strong>
          <p>Da qui in poi procede da sola: puoi chiudere il browser o spegnere l'applicazione senza fermarla.</p>
          <ol className="handoff-steps">
            <li>{mode === "raster" ? "Preparazione del JPG" : "Tracciato in SVG ed EPS"}</li>
            <li>Classificazione e metadati (titolo, descrizione, keyword)</li>
            <li>Revisione nel Backoffice, poi invio al marketplace</li>
          </ol>
          <div className="row" style={{ gap: 8 }}>
            <button className="btn primary" onClick={() => onNavigate?.("backoffice")}>
              Vai al Backoffice
            </button>
            <button className="btn" onClick={() => onNavigate?.("monitor")}>
              Segui i lotti consegnati
            </button>
          </div>
          <div className="muted small">
            I risultati compaiono nel Backoffice a qualche minuto di distanza: la coda viene letta a intervalli.
          </div>
        </div>
      )}

      {staged.length > 0 && (
        <>
          <div className="toolbar">
            <div>
              {staged.length} {staged.length === 1 ? "immagine pronta" : "immagini pronte"} ·{" "}
              {nomeModalita(mode)}
            </div>
            <div className="actions">
              <button className="btn" onClick={() => setStaged([])} disabled={delivering}>
                Svuota
              </button>
              <button
                className="btn accent"
                onClick={() => setShowConfirm(true)}
                disabled={delivering || storageOff}
              >
                {delivering ? "Consegna…" : "Consegna alla pipeline"}
              </button>
            </div>
          </div>

          {mode === "vector" && (
            <div className="bulkbar">
              <span className="muted small">
                La soglia decide cosa diventa nero e cosa bianco: potrace traccia il nero. Lasciala automatica se
                l'anteprima già ti convince.
              </span>
              <button
                className="btn small"
                onClick={() => setStaged((prev) => prev.map((s) => ({ ...s, threshold: null })))}
                disabled={delivering}
              >
                Tutte automatiche
              </button>
            </div>
          )}

          {mode === "colore" && (
            <div className="bulkbar">
              <span className="muted small">
                Quante tinte: <strong>{colori}</strong>. Servono a non perdere i dettagli piccoli e
                colorati, come una guancia rosa o un riflesso azzurro. È un tetto, non una promessa:
                le tinte che descrivono una frangia di contorno invece di una zona vengono scartate,
                e su certi disegni sono quasi tutte — chiedendone quarantotto ne escono sei. Su
                un'illustrazione con oggetti ombreggiati, invece, le tinte in più vanno a spezzare
                l'ombreggiatura in bande sempre più sottili: sono vere, ma il file cresce senza che
                si veda granché. Qui non c'è anteprima perché sarebbe una ricostruzione
                approssimata — e decidere su un'anteprima falsa è peggio che non averla.
              </span>
              <input
                type="range"
                min={2}
                max={32}
                value={colori}
                disabled={delivering}
                onChange={(e) => setColori(Number(e.target.value))}
                aria-label="Numero di tinte del tracciato a colori"
              />
              <button className="btn small" onClick={() => setColori(24)} disabled={delivering || colori === 24}>
                24 (consigliate)
              </button>
            </div>
          )}

          {mode === "colore" && (
            <div className="bulkbar">
              <span className="muted small">
                <strong>Unione delle tinte gemelle.</strong> Quando due tinte quasi identiche si
                dividono la stessa campitura, il disegno esce a chiazze e il contorno viene tracciato
                due volte. Questa passata le rimette insieme, e un secondo controllo sui pixel
                dell'originale le lascia separate se lì c'è davvero un bordo.{" "}
                {UNIONI.find((u) => u.valore === unione)?.spiega}
              </span>
              <div className="scelte" role="group" aria-label="Quanto unire le tinte gemelle">
                {UNIONI.map((u) => (
                  <button
                    key={u.valore}
                    className="btn small"
                    onClick={() => setUnione(u.valore)}
                    disabled={delivering}
                    title={u.spiega}
                    aria-pressed={u.valore === unione}
                  >
                    {u.nome}
                  </button>
                ))}
              </div>
            </div>
          )}

          <div className="grid">
            {staged.map((it) => (
              <article key={it.id} className="card">
                <div className="preview">
                  {mode === "vector" ? (
                    <TracePreview
                      file={it.file}
                      threshold={it.threshold}
                      onReady={(auto) => patch(it.id, { auto })}
                    />
                  ) : (
                    <RasterThumb file={it.file} />
                  )}
                </div>
                <div className="meta">
                  <div className="row between">
                    <span className="fname" title={it.file.name}>
                      {it.file.name}
                    </span>
                    <span className="muted small">{fmtSize(it.file.size)}</span>
                  </div>

                  {mode === "vector" && (
                    <div className="revector">
                      <label>
                        Soglia:{" "}
                        {it.threshold == null
                          ? `automatica${it.auto != null ? ` (≈${it.auto})` : ""}`
                          : it.threshold}
                      </label>
                      <div className="row" style={{ gap: 8 }}>
                        <input
                          type="range"
                          min={0}
                          max={255}
                          value={it.threshold ?? it.auto ?? 128}
                          disabled={delivering}
                          onChange={(e) => patch(it.id, { threshold: Number(e.target.value) })}
                          aria-label={`Soglia di tracciamento per ${it.file.name}`}
                        />
                        <button
                          className="btn small"
                          onClick={() => patch(it.id, { threshold: null })}
                          disabled={delivering || it.threshold == null}
                          title="Lascia decidere la soglia alla pipeline"
                        >
                          auto
                        </button>
                      </div>
                    </div>
                  )}

                  <div className="row between" style={{ marginTop: 10 }}>
                    <span className="muted small">
                      {mode === "vector" ? "SVG + EPS + JPG" : "solo JPG"}
                    </span>
                    <button className="btn small" onClick={() => remove(it.id)} disabled={delivering}>
                      Togli
                    </button>
                  </div>
                </div>
              </article>
            ))}
          </div>
        </>
      )}

      {staged.length === 0 && !result && (
        <div className="empty">
          Nessuna immagine in attesa. Trascinane qui sopra: verranno consegnate alla pipeline, che le elabora per conto
          suo anche ad applicazione spenta.
        </div>
      )}

      {showConfirm && (
        <div className="modal-backdrop" onClick={() => setShowConfirm(false)} role="presentation">
          <div
            className="modal"
            onClick={(e) => e.stopPropagation()}
            role="dialog"
            aria-modal="true"
            aria-labelledby="handoff-title"
          >
            <h3 id="handoff-title">
              Consegnare {staged.length} {staged.length === 1 ? "immagine" : "immagini"} alla pipeline?
            </h3>
            <p>
              Gli originali vengono depositati e messi in coda. Da quel momento{" "}
              <strong>il lavoro non dipende più da questa applicazione</strong>: prosegue anche se chiudi il browser o
              se il sito si sospende.
            </p>
            <ul className="modal-list">
              <li>
                {mode === "vector"
                  ? "Vengono tracciate in SVG ed EPS, con la soglia che hai scelto."
                  : "Nessun tracciato: viene preparato solo il JPG."}
              </li>
              <li>
                <strong>Titoli, descrizioni e keyword</strong> li scrive la pipeline: non c'è nulla da compilare qui.
              </li>
              <li>
                Li rivedi e li correggi nel <strong>Backoffice</strong>, prima dell'invio al marketplace.
              </li>
            </ul>
            <div className="modal-actions">
              <button ref={cancelRef} className="btn" onClick={() => setShowConfirm(false)}>
                Annulla
              </button>
              <button className="btn accent" onClick={deliver}>
                Sì, consegna
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/** Plain thumbnail for image mode, where nothing is traced and there is no threshold to judge. */
function RasterThumb({ file }: { file: File }) {
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file]);

  return url ? <img src={url} alt={`Anteprima: ${file.name}`} /> : <div className="noimg">…</div>;
}

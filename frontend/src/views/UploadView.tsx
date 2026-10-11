import { useCallback, useEffect, useRef, useState } from "react";
import { api, type GiaPresente, type HandoffResponse, type ParametriTracciato, type PipelineStatus } from "../api";
import { recordDelivery, trackNameFor, type DeliveredFile } from "../deliveries";
import TracePreview from "../components/TracePreview";
import PannelloTracciato from "../components/PannelloTracciato";

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

/**
 * Cosa fare del file caricato.
 *
 * «auto» non e' l'assenza di una scelta ma la scelta migliore: guarda l'immagine e decide fra
 * colori e silhouette. Le altre due vettoriali sono **imposizioni**, e servono solo quando si
 * vuole qualcosa di diverso da quel che l'immagine e'.
 */
type Modalita = "auto" | "vector" | "colore" | "raster";

/** Il nome della modalità in italiano, scritto in un posto solo perché compare in tre punti. */
function nomeModalita(mode: Modalita): string {
  return mode === "auto" ? "automatica"
    : mode === "vector" ? "vettoriale in bianco e nero"
    : mode === "colore" ? "vettoriale a colori"
    : "immagine";
}

const fmtSize = (bytes: number) =>
  bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;

/** Dove la pipeline ha già un'immagine, in poche parole: per il riquadro e per la conferma. */
const etichettaPresente = (p: GiaPresente): string => {
  if (p.dove === "da-inviare") return "già pronta per l'invio";
  if (p.dove === "in-lavorazione") return "già in lavorazione";
  if (!p.quando) return "già pubblicata";
  const quando = new Date(p.quando);
  const data = quando.toLocaleDateString("it-IT", { day: "numeric", month: "2-digit", year: "numeric" });
  // «l'11», «l'8», «l'1»: davanti a un numero che si legge con una vocale l'articolo si apostrofa.
  const articolo = [1, 8, 11].includes(quando.getDate()) ? "l'" : "il ";
  return `già pubblicata ${articolo}${data}`;
};

export default function UploadView({
  pipeline,
  onNavigate,
}: {
  pipeline: PipelineStatus | null;
  onNavigate?: (tab: "backoffice" | "monitor") => void;
}) {
  const [mode, setMode] = useState<Modalita>("auto");
  /**
   * I numeri con cui tracciare a colori, per tutto il lotto.
   *
   * Ci sta solo quel che è stato **spostato**: un campo assente lascia decidere al servizio, e
   * segue la configurazione anche se un domani cambia. Vedi PannelloTracciato.
   */
  const [tracciato, setTracciato] = useState<ParametriTracciato>({});
  const [staged, setStaged] = useState<Staged[]>([]);
  const [delivering, setDelivering] = useState(false);
  /** Quante immagini sono già partite, durante una consegna a più invii. */
  const [consegnate, setConsegnate] = useState<{ fatte: number; totale: number } | null>(null);
  const [result, setResult] = useState<HandoffResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  /**
   * Le immagini in elenco che la pipeline ha già, per id: vedi apriConferma.
   *
   * Si controllano all'apertura della conferma e non al momento dell'aggiunta, perché è lì che si
   * decide; e si ricontrollano ogni volta, perché nel frattempo un'immagine può essere partita.
   */
  const [presenti, setPresenti] = useState<Map<string, GiaPresente>>(new Map());
  const [controllo, setControllo] = useState<"in-corso" | "fatto" | "non-riuscito" | null>(null);
  const [motivoControllo, setMotivoControllo] = useState<string | null>(null);
  /** Solo la risposta dell'ultima apertura conta: una più vecchia, arrivata tardi, non la sovrascrive. */
  const giroControllo = useRef(0);
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

  /**
   * Apre la conferma e intanto chiede quali immagini la pipeline ha già.
   *
   * Una consegna ripetuta non è un errore che la pipeline possa riconoscere: per lei ogni
   * caricamento è un'immagine nuova, e la rimanda ai marketplace. È così che BBoxer, Boxer e i due
   * Border collie sono saliti due volte su Adobe Stock e Freepik. Il controllo avvisa e lascia
   * scegliere; se non riesce, lo dice e la consegna resta possibile.
   */
  const apriConferma = async () => {
    const elenco = staged;
    const giro = ++giroControllo.current;
    setShowConfirm(true);
    setPresenti(new Map());
    setMotivoControllo(null);
    setControllo("in-corso");
    try {
      // Un tetto al controllo: la conferma non deve restare bloccata se SharePoint non risponde.
      const res = await Promise.race([
        api.giaPresenti(elenco.map((s) => s.file.name)),
        new Promise<never>((_, rifiuta) =>
          setTimeout(() => rifiuta(new Error("SharePoint non ha risposto in tempo")), 20000)
        ),
      ]);
      if (giro !== giroControllo.current) return;
      const trovate = new Map<string, GiaPresente>();
      for (const p of res.presenti ?? []) {
        const s = elenco[p.posizione];
        if (s) trovate.set(s.id, p);
      }
      setPresenti(trovate);
      setControllo(res.controllato ? "fatto" : "non-riuscito");
      if (!res.controllato) setMotivoControllo(res.motivo ?? null);
    } catch (e) {
      if (giro !== giroControllo.current) return;
      setControllo("non-riuscito");
      setMotivoControllo((e as Error).message);
    }
  };

  const deliver = async (soloNuove = false) => {
    const elenco = soloNuove ? staged.filter((s) => !presenti.has(s.id)) : staged;
    if (elenco.length === 0) return;
    setShowConfirm(false);
    setError(null);
    setDelivering(true);
    setConsegnate(null);
    try {
      const res = await api.handoff(
        elenco.map((s) => s.file),
        mode,
        mode === "vector" ? elenco.map((s) => s.threshold) : undefined,
        // I parametri viaggiano in tutte le modalità che tracciano, non solo a colori: un preset
        // porta con sé anche **come** tracciare, e mandarlo solo quando l'elenco è già su
        // «A colori» vorrebbe dire che scegliere «Silhouette» non arriva da nessuna parte.
        mode === "raster" ? undefined : tracciato,
        (fatte, totale) => setConsegnate({ fatte, totale })
      );

      // Per posizione, non per nome: due immagini omonime sono due consegne, e riconoscerle per
      // nome terrebbe in elenco anche quella arrivata -- il secondo tentativo la duplicherebbe.
      const rifiutataIn = new Map((res.rifiutate ?? []).map((r) => [r.posizione, r.error]));
      recordDelivery({
        id: nextId(),
        at: new Date().toISOString(),
        mode,
        accepted: res.accepted,
        rejected: res.rejected,
        files: elenco.map<DeliveredFile>((s, i) => ({
          name: s.file.name,
          trackName: trackNameFor(s.file.name),
          threshold: mode === "vector" ? s.threshold : null,
          ok: !rifiutataIn.has(i),
          error: rifiutataIn.get(i),
        })),
      });

      setResult(res);
      // Restano in elenco le rifiutate, le sole per cui valga un secondo tentativo, e quelle
      // lasciate fuori perché già presenti: toglierle le farebbe sembrare consegnate.
      const arrivate = new Set(elenco.filter((_, i) => !rifiutataIn.has(i)).map((s) => s.id));
      setStaged((prev) => prev.filter((s) => !arrivate.has(s.id)));
      if (res.rejected > 0)
        setError(
          `${res.rejected} non consegnate e rimaste in elenco: ${res.errors?.[0]?.error ?? "errore sconosciuto"}`
        );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setDelivering(false);
      setConsegnate(null);
    }
  };

  const storageOff = pipeline != null && !pipeline.canEnqueue;

  return (
    <>
      <div className="modebar">
        <span className="muted small">Cosa vuoi consegnare?</span>
        <button className={`modebtn ${mode === "auto" ? "on" : ""}`} onClick={() => setMode("auto")}>
          <strong>✦ Automatico</strong>
          <span>guarda l'immagine e sceglie: colori o silhouette</span>
        </button>
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

      {mode === "vector" && (
        <div className="notice" role="status">
          <strong>La silhouette butta via il colore.</strong> Un disegno a contorni blu esce nero
          pieno: è la lavorazione giusta solo per chi vuole davvero una sagoma a una tinta. Se
          l'immagine ha dei colori, <em>Automatico</em> se ne accorge da solo.
        </div>
      )}

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
            {mode === "auto"
              ? "Modalità automatica · si guarda ogni immagine e si sceglie: a colori se colori ne ha, silhouette se non ne ha"
              : mode === "vector"
              ? "Modalità vettoriale in bianco e nero · silhouette in SVG, EPS e JPG"
              : mode === "colore"
              ? `Modalità vettoriale a colori · ${Object.keys(tracciato).length === 0
                  ? "taratura di serie"
                  : `${Object.keys(tracciato).length} ${Object.keys(tracciato).length === 1 ? "parametro" : "parametri"} su misura`} · SVG ed EPS a colori`
              : "Modalità immagine · nessuna vettorializzazione, l'immagine resta com'è"}
          </div>
        </div>
      </section>

      {error && (
        <div className="notice err" role="alert">
          {error}
        </div>
      )}

      {result && result.accepted > 0 && (
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
                onClick={apriConferma}
                disabled={delivering || storageOff}
              >
                {delivering
                  ? consegnate && consegnate.totale > 0
                    ? `Consegna ${consegnate.fatte}/${consegnate.totale}…`
                    : "Consegna…"
                  : "Consegna alla pipeline"}
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

          {mode !== "raster" && (
            <div className="bulkbar tracciato-barra">
              <span className="muted small">
                <strong>Come tracciare.</strong> Il preset dice in una parola che genere di disegno
                stai caricando; i cursori sotto lo correggono. Valgono per tutto il lotto, perché
                chi carica venti disegni insieme li ha scelti insieme: per correggerne uno solo c'è
                la finestra «Ritraccia» nel dettaglio dell'immagine. Qui non c'è anteprima perché
                sarebbe una ricostruzione approssimata — e decidere su un'anteprima falsa è peggio
                che non averla.
              </span>
              <PannelloTracciato
                valore={tracciato}
                onChange={setTracciato}
                disabilitato={delivering}
                onModalita={(m) => setMode(m as Modalita)}
                compatto
              />
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
                  {presenti.has(it.id) && (
                    <div className="row" style={{ marginTop: 6 }}>
                      <span className="badge warn" title={`Trovato: ${presenti.get(it.id)!.file}`}>
                        {etichettaPresente(presenti.get(it.id)!)}
                      </span>
                    </div>
                  )}

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
            {controllo === "in-corso" && (
              <p className="muted small">Controllo se qualcuna è già passata dalla pipeline…</p>
            )}
            {controllo === "non-riuscito" && (
              <p className="muted small">
                Non sono riuscito a controllare se qualcuna è già stata pubblicata
                {motivoControllo ? ` (${motivoControllo})` : ""}: puoi consegnarle lo stesso.
              </p>
            )}
            {presenti.size > 0 && (
              <div className="in-avviso" role="alert">
                <strong>
                  {presenti.size === 1
                    ? "Un'immagine è già passata dalla pipeline."
                    : `${presenti.size} immagini sono già passate dalla pipeline.`}
                </strong>{" "}
                Consegnarle di nuovo le rimanda ai marketplace, dove diventano doppioni.
                <ul className="modal-list" style={{ margin: "6px 0 0" }}>
                  {staged
                    .filter((s) => presenti.has(s.id))
                    .slice(0, 8)
                    .map((s) => (
                      <li key={s.id}>
                        <strong>{s.file.name}</strong>: {etichettaPresente(presenti.get(s.id)!)}
                        {presenti.get(s.id)!.file.toLowerCase() !== s.file.name.toLowerCase()
                          ? ` (${presenti.get(s.id)!.file})`
                          : ""}
                      </li>
                    ))}
                </ul>
                {presenti.size > 8 && <div>e altre {presenti.size - 8}.</div>}
              </div>
            )}
            <div className="modal-actions">
              <button ref={cancelRef} className="btn" onClick={() => setShowConfirm(false)}>
                Annulla
              </button>
              {presenti.size > 0 ? (
                <>
                  <button className="btn" onClick={() => deliver(false)}>
                    Consegna tutte ({staged.length})
                  </button>
                  {staged.length > presenti.size && (
                    <button className="btn accent" onClick={() => deliver(true)}>
                      Consegna solo le nuove ({staged.length - presenti.size})
                    </button>
                  )}
                </>
              ) : (
                <button className="btn accent" onClick={() => deliver(false)} disabled={controllo === "in-corso"}>
                  Sì, consegna
                </button>
              )}
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


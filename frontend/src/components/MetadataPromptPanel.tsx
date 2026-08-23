import { useEffect, useState } from "react";
import { api, type FeedbackEntry, type Guidance } from "../api";

/**
 * The review loop, from the operator's side: shows the rules currently appended to the metadata
 * prompt, how much feedback is waiting, and lets the review run on demand.
 */
export default function MetadataPromptPanel() {
  const [guidance, setGuidance] = useState<Guidance | null>(null);
  const [entries, setEntries] = useState<FeedbackEntry[]>([]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showFeedback, setShowFeedback] = useState(false);

  const load = async () => {
    try {
      setGuidance(await api.metadataPrompt());
    } catch (e) {
      setError((e as Error).message);
    }
  };

  useEffect(() => { load(); }, []);

  const rebuild = async () => {
    setBusy(true);
    setNotice(null);
    setError(null);
    try {
      const r = await api.rebuildMetadataPrompt();
      setGuidance(r.guidance);
      setNotice(
        r.warning
          ? r.warning
          : `Revisione completata con motore ${r.engine === "agentic" ? "agentico" : "deterministico"}: ` +
            `${r.entriesUsed} feedback elaborati, prompt aggiornato alla versione ${r.guidance.version}.`
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const reset = async () => {
    setBusy(true);
    setNotice(null);
    setError(null);
    try {
      setGuidance(await api.resetMetadataPrompt());
      setNotice("Regole azzerate: si torna al prompt predefinito. I feedback restano registrati.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const toggleFeedback = async () => {
    if (showFeedback) return setShowFeedback(false);
    try {
      setEntries(await api.metadataFeedback(30));
      setShowFeedback(true);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  if (!guidance) return null;

  const pending = guidance.pendingFeedback;

  return (
    <section className="cfg-area">
      <h2>Revisione del prompt metadati</h2>
      <p className="muted small">
        Le correzioni che applichi ai metadati, e le note con cui le spieghi, vengono raccolte.
        La revisione le elabora e riscrive le regole aggiuntive appese al prompt di generazione,
        così le immagini successive non richiedono le stesse correzioni.
      </p>

      <div className="guid">
        <div className="guid-head">
          <span className={`guid-badge ${guidance.version > 0 ? "on" : ""}`}>
            {guidance.version > 0 ? `Prompt v${guidance.version}` : "Prompt predefinito"}
          </span>
          <span className="guid-badge">{guidance.totalFeedback} feedback raccolti</span>
          {pending > 0 && (
            <span className="guid-badge pending">{pending} da elaborare</span>
          )}
          {guidance.version > 0 && (
            <span className="guid-badge">
              motore: {guidance.engine === "agentic" ? "agentico" : "deterministico"}
            </span>
          )}
          {!guidance.agenticAvailable && (
            <span className="guid-badge">nessun motore AI: revisione deterministica</span>
          )}
        </div>

        {notice && <div className="notice" role="status">{notice}</div>}
        {error && <div className="notice err" role="alert">{error}</div>}

        {guidance.text ? (
          <pre className="guid-text">{guidance.text}</pre>
        ) : (
          <div className="guid-empty">
            {guidance.totalFeedback === 0
              ? "Nessun feedback ancora raccolto: correggi i metadati di un'immagine e spiega il perché."
              : "Nessuna regola attiva. Avvia la revisione per ricavarla dai feedback raccolti."}
          </div>
        )}

        {guidance.bannedKeywords?.length > 0 && (
          <div>
            <div className="muted small" style={{ marginBottom: 4 }}>
              Keyword rimosse automaticamente dopo la generazione ({guidance.bannedKeywords.length}).
              Questo elenco non è un suggerimento al modello: viene applicato dal codice, così i termini
              che hai scartato non raggiungono i marketplace nemmeno se il modello li ripropone.
            </div>
            <pre className="guid-text">{guidance.bannedKeywords.join(", ")}</pre>
          </div>
        )}

        <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
          <button className="btn" onClick={rebuild} disabled={busy || guidance.totalFeedback === 0}>
            {busy ? "Revisione in corso…" : "Avvia revisione"}
          </button>
          <button className="btn small" onClick={toggleFeedback} disabled={guidance.totalFeedback === 0}>
            {showFeedback ? "Nascondi feedback" : "Vedi i feedback"}
          </button>
          {guidance.version > 0 && (
            <button className="btn small" onClick={reset} disabled={busy} title="Torna al prompt predefinito">
              Azzera regole
            </button>
          )}
        </div>

        {guidance.updatedAt && (
          <div className="muted small">
            Ultima revisione: {new Date(guidance.updatedAt).toLocaleString()} · basata su {guidance.basedOnEntries} feedback
          </div>
        )}

        {showFeedback && (
          <div className="fblist">
            {entries.length === 0 && <div className="guid-empty">Nessun feedback registrato.</div>}
            {entries.map((e) => (
              <article key={e.id} className="fbrow">
                <div className="fbtop">
                  <span className="fbname">{e.baseName}</span>
                  <span className="fbwhen">{new Date(e.at).toLocaleString()}</span>
                </div>
                {e.note && <p className="fbnote">“{e.note}”</p>}
                <div className="fbdiff">
                  {e.titleChanged && e.generated && (
                    <span>Titolo: «{e.generated.title}» → «{e.corrected.title}»</span>
                  )}
                  {e.categoryChanged && e.generated && (
                    <span>Categoria: {e.generated.category} → {e.corrected.category}</span>
                  )}
                  {e.keywordsRemoved.length > 0 && (
                    <span className="krem">− {e.keywordsRemoved.join(", ")}</span>
                  )}
                  {e.keywordsAdded.length > 0 && (
                    <span className="kadd">+ {e.keywordsAdded.join(", ")}</span>
                  )}
                </div>
              </article>
            ))}
          </div>
        )}
      </div>
    </section>
  );
}

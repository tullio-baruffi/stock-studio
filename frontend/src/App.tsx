import { useCallback, useEffect, useState } from "react";
import { api, type PipelineStatus } from "./api";
import UploadView from "./views/UploadView";
import MonitorView from "./views/MonitorView";
import SystemView from "./views/SystemView";
import PipelineView from "./views/PipelineView";
import TrendsView from "./views/TrendsView";
import GuideView from "./views/GuideView";
import ConfigurationView from "./views/ConfigurationView";
import BackofficeView from "./views/BackofficeView";
import SignedInAs from "./components/SignedInAs";

type Tab = "upload" | "monitor" | "system" | "configuration" | "pipeline" | "trends" | "guide" | "backoffice";

const TABS: { key: Tab; label: string; icon: string }[] = [
  { key: "upload", label: "Carica", icon: "⬆" },
  { key: "backoffice", label: "Backoffice", icon: "🗂" },
  { key: "monitor", label: "Monitoraggio", icon: "📊" },
  { key: "pipeline", label: "Pipeline", icon: "🔀" },
  { key: "trends", label: "Opportunità", icon: "📈" },
  { key: "system", label: "Sistema", icon: "🩺" },
  { key: "configuration", label: "Configurazione", icon: "⚙" },
  { key: "guide", label: "Guida", icon: "📖" },
];

/** Past this many unprocessed corrections the reminder stops being a hint and becomes advice. */
const ADVISED_AT = 3;

export default function App() {
  const [tab, setTab] = useState<Tab>("upload");
  const [pipeline, setPipeline] = useState<PipelineStatus | null>(null);
  const [pending, setPending] = useState(0);
  const [reviewing, setReviewing] = useState(false);
  const [reviewMsg, setReviewMsg] = useState<string | null>(null);
  // Dismissal is per-count: correcting more images brings the reminder back on its own.
  const [dismissedAt, setDismissedAt] = useState<number | null>(null);

  const refreshPending = useCallback(async () => {
    try {
      const g = await api.metadataPrompt();
      setPending(g.pendingFeedback);
    } catch {
      /* the reminder is secondary: never surface its failures */
    }
  }, []);

  useEffect(() => {
    api.pipelineStatus().then(setPipeline).catch(() => setPipeline(null));
    refreshPending();
    const t = setInterval(refreshPending, 60_000);
    return () => clearInterval(t);
  }, [refreshPending]);

  const runReview = async () => {
    setReviewing(true);
    setReviewMsg(null);
    try {
      const r = await api.rebuildMetadataPrompt();
      setReviewMsg(
        r.warning ??
        `Revisione completata: ${r.entriesUsed} feedback elaborati, prompt aggiornato alla versione ${r.guidance.version}.`
      );
      setPending(r.guidance.pendingFeedback);
      setDismissedAt(null);
    } catch (e) {
      setReviewMsg(`Revisione non riuscita: ${(e as Error).message}`);
    } finally {
      setReviewing(false);
    }
  };

  const showReminder = pending > 0 && dismissedAt !== pending;

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand"><span className="logo">▲</span> Stock Vector Studio</div>
        <div className="sub">JPEG → vettoriale + titolo/keyword → CSV Adobe Stock &amp; Freepik</div>
        {pipeline && (
          <div className={`pipeline-chip ${pipeline.enabled ? "on" : "off"}`} role="status">
            {pipeline.enabled
              ? `Pipeline attiva → ${pipeline.siteUrl?.replace(/^https?:\/\//, "")} · trigger: ${pipeline.trigger}`
              : "Pipeline non configurata (modalità autonoma)"}
          </div>
        )}
        <SignedInAs />
      </header>

      {showReminder && (
        <div className={`review-reminder ${pending >= ADVISED_AT ? "advised" : ""}`} role="status">
          <span className="rr-icon">{pending >= ADVISED_AT ? "🔔" : "💡"}</span>
          <span className="rr-text">
            {pending === 1
              ? "Hai 1 correzione ai metadati non ancora elaborata."
              : `Hai ${pending} correzioni ai metadati non ancora elaborate.`}{" "}
            {pending >= ADVISED_AT
              ? "Avvia la revisione per aggiornare il prompt di generazione."
              : "Puoi avviare la revisione quando vuoi."}
          </span>
          <button className="btn small" onClick={runReview} disabled={reviewing}>
            {reviewing ? "Revisione…" : "Avvia revisione"}
          </button>
          <button
            className="btn small ghost"
            onClick={() => { setTab("configuration"); setDismissedAt(pending); }}
          >
            Dettagli
          </button>
          <button className="rr-close" onClick={() => setDismissedAt(pending)} aria-label="Nascondi il promemoria">
            ×
          </button>
        </div>
      )}

      {reviewMsg && (
        <div className="review-reminder done" role="status">
          <span className="rr-icon">✓</span>
          <span className="rr-text">{reviewMsg}</span>
          <button className="rr-close" onClick={() => setReviewMsg(null)} aria-label="Chiudi">×</button>
        </div>
      )}

      <nav className="tabs" role="tablist" aria-label="Sezioni principali">
        {TABS.map((t) => (
          <button
            key={t.key}
            className={`tab ${tab === t.key ? "active" : ""}`}
            onClick={() => setTab(t.key)}
            role="tab"
            aria-selected={tab === t.key}
            aria-controls="main-panel"
          >
            <span className="tab-icon">{t.icon}</span> {t.label}
            {t.key === "configuration" && pending > 0 && (
              <span className="tab-badge" title={`${pending} feedback da elaborare`}>{pending}</span>
            )}
          </button>
        ))}
      </nav>

      <main id="main-panel" role="tabpanel">
        {tab === "upload" && <UploadView pipeline={pipeline} onNavigate={setTab} />}
        {tab === "backoffice" && <BackofficeView />}
        {tab === "monitor" && <MonitorView />}
        {tab === "pipeline" && <PipelineView />}
        {tab === "trends" && <TrendsView />}
        {tab === "system" && <SystemView />}
        {tab === "configuration" && <ConfigurationView />}
        {tab === "guide" && <GuideView />}
      </main>
    </div>
  );
}

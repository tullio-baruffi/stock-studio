import { useEffect, useState } from "react";
import { api, type ConfigurationSummary } from "../api";
import MetadataPromptPanel from "../components/MetadataPromptPanel";

const STATE = {
  ok: { label: "Configurato", cls: "cfg-ok" },
  warning: { label: "Parziale / locale", cls: "cfg-warning" },
  off: { label: "Disattivato", cls: "cfg-off" },
} as const;

export default function ConfigurationView() {
  const [data, setData] = useState<ConfigurationSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await api.configuration());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  return (
    <div className="configuration">
      <div className="cfg-hero">
        <div>
          <h1>Configurazione</h1>
          <p>
            Riepilogo della configurazione effettivamente caricata dal backend e delle alternative
            supportate. I nomi in <code>monospace</code> sono le chiavi da impostare in User Secrets,
            variabili ambiente o Key Vault.
          </p>
        </div>
        <button className="btn" onClick={load} disabled={loading}>
          {loading ? "Aggiorno…" : "Aggiorna"}
        </button>
      </div>

      {error && <div className="notice err" role="alert">{error}</div>}
      {!data && loading && <div className="empty">Lettura della configurazione in corso…</div>}

      {data && (
        <>
          <div className="cfg-safety" role="note">
            🔒 <strong>Vista sicura</strong> · {data.safetyNote} · Ambiente:
            <span className="cfg-env"> {data.environment}</span>
          </div>

          <div className="section-title">Configurazione attualmente in uso</div>
          <div className="cfg-grid">
            {data.groups.map((group) => {
              const state = STATE[group.state] ?? STATE.off;
              return (
                <section key={group.key} className={`cfg-card ${state.cls}`}>
                  <div className="cfg-card-head">
                    <div>
                      <h2>{group.title}</h2>
                      <div className="cfg-summary">{group.summary}</div>
                    </div>
                    <span className={`cfg-state ${state.cls}`}>{state.label}</span>
                  </div>
                  <dl className="cfg-values">
                    {group.items.map((item) => (
                      <div key={`${group.key}-${item.configKey}-${item.label}`} className="cfg-value">
                        <dt>{item.label}</dt>
                        <dd>
                          <span>{item.value}</span>
                          <code title="Chiave di configurazione">{item.configKey}</code>
                        </dd>
                      </div>
                    ))}
                  </dl>
                </section>
              );
            })}
          </div>

          <div className="section-title">Apprendimento dai tuoi interventi</div>
          <div className="cfg-areas">
            <MetadataPromptPanel />
          </div>

          <div className="section-title">Configurazioni possibili</div>
          <div className="cfg-areas">
            {data.possibilities.map((area) => (
              <section key={area.title} className="cfg-area">
                <h2>{area.title}</h2>
                {area.note && <p className="cfg-area-note">{area.note}</p>}
                <div className="cfg-choices">
                  {area.choices.map((choice) => (
                    <article key={`${area.title}-${choice.value}`} className={`cfg-choice ${choice.active ? "active" : ""}`}>
                      <div className="cfg-choice-head">
                        <strong>{choice.label}</strong>
                        {choice.active && <span className="cfg-active">In uso</span>}
                      </div>
                      <p>{choice.description}</p>
                      <div className="cfg-requires">
                        <span>Richiede</span>
                        <code>{choice.requirements}</code>
                      </div>
                    </article>
                  ))}
                </div>
              </section>
            ))}
          </div>

          <div className="cfg-footer muted small">
            Letta il {new Date(data.generatedAt).toLocaleString()} · La pagina è informativa:
            le modifiche si applicano tramite configurazione e riavvio del backend.
          </div>
        </>
      )}
    </div>
  );
}

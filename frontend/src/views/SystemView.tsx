import { useEffect, useState } from "react";
import { api, type Health, type Overview, type PlanState } from "../api";

function Tile({ ok, title, value, sub }: { ok?: boolean; title: string; value: string; sub?: string }) {
  return (
    <div className={`tile ${ok === undefined ? "" : ok ? "ok" : "err"}`}>
      <div className="tile-title">{title}</div>
      <div className="tile-value">{value}</div>
      {sub && <div className="tile-sub">{sub}</div>}
    </div>
  );
}

function Kpi({ label, value }: { label: string; value: string }) {
  return (
    <div className="kpi">
      <div className="kpi-value">{value}</div>
      <div className="kpi-label">{label}</div>
    </div>
  );
}

export default function SystemView() {
  const [health, setHealth] = useState<Health | null>(null);
  const [ov, setOv] = useState<Overview | null>(null);
  const [probe, setProbe] = useState<string>("");
  const [probing, setProbing] = useState(false);
  const [plan, setPlan] = useState<PlanState | null>(null);
  const [scaling, setScaling] = useState(false);
  const [planNote, setPlanNote] = useState<string>("");

  useEffect(() => {
    const load = () => {
      api.health().then(setHealth).catch(() => setHealth(null));
      api.overview().then(setOv).catch(() => setOv(null));
    };
    load();
    const t = setInterval(load, 5000);
    return () => clearInterval(t);
  }, []);

  // The plan reads go through ARM, so they are slower and far less interesting than the health
  // tiles: once a minute is plenty and keeps the management API out of a 5-second loop.
  useEffect(() => {
    const load = () => api.systemPlan().then(setPlan).catch(() => setPlan(null));
    load();
    const t = setInterval(load, 60_000);
    return () => clearInterval(t);
  }, []);

  const runProbe = async () => {
    setProbing(true);
    setProbe("");
    try {
      const r = await api.pipelineProbe();
      setProbe(r.ok ? `OK — ${r.web}` : `Errore — ${r.error}`);
    } catch (e) {
      setProbe(`Errore — ${(e as Error).message}`);
    } finally {
      setProbing(false);
    }
  };

  /**
   * Azure moves the site between workers to change SKU, so this connection drops for a few
   * seconds. Saying so up front is the difference between a known pause and a phantom outage.
   */
  const changePlan = async (sku: string) => {
    if (!confirm(
      `Portare il piano dell'API a ${sku}?\n\n` +
      `Azure riavvia il sito per spostarlo: l'API resta irraggiungibile per qualche secondo.`
    )) return;

    setScaling(true);
    setPlanNote("");
    try {
      const r = await api.setSystemPlan(sku);
      setPlanNote(r.ok ? `Piano ora su ${r.sku}.` : `Errore — ${r.error}`);
      // The site is restarting: give it a moment before asking again.
      setTimeout(() => api.systemPlan().then(setPlan).catch(() => {}), 15_000);
    } catch (e) {
      setPlanNote(`Errore — ${(e as Error).message}`);
    } finally {
      setScaling(false);
    }
  };

  return (
    <div className="system">
      <div className="section-title">Salute del sistema</div>
      <div className="tiles">
        <Tile ok={health?.backend} title="Backend API" value={health?.backend ? "Online" : "Offline"} />
        <Tile
          ok={health?.jobStore.ok}
          title="Persistenza"
          value={health?.jobStore.ok ? "Azure Table OK" : "Non raggiungibile"}
          sub={health?.jobStore.backend}
        />
        <Tile
          ok={health?.vectorizer.potracePresent}
          title="Vettorializzatore"
          value={health ? health.vectorizer.engine : "—"}
          sub={health ? (health.vectorizer.potracePresent ? "potrace presente" : "potrace mancante") : ""}
        />
        <Tile
          ok={health?.pipeline.enabled}
          title="Pipeline Azure"
          value={health?.pipeline.enabled ? "Attiva" : "Disabilitata"}
          sub={health ? `trigger: ${health.pipeline.trigger} · file: ${health.pipeline.dispatchFile}` : ""}
        />
        <Tile
          ok={health ? !health.pipeline.enabled || health.pipeline.callbackProtected : undefined}
          title="Callback pipeline"
          value={health?.pipeline.callbackProtected ? "Protetta" : "Senza secret"}
          sub={health?.pipeline.enabled
            ? "X-Callback-Secret tra Function e dashboard"
            : "Richiesta solo quando la pipeline è attiva"}
        />
      </div>

      <div className="section-title">Verifica SharePoint (on-demand)</div>
      <div className="row" style={{ gap: 12 }}>
        <button className="btn" onClick={runProbe} disabled={probing}>
          {probing ? "Verifica…" : "Verifica connessione SharePoint"}
        </button>
        {probe && <span className={probe.startsWith("OK") ? "ok" : "err"}>{probe}</span>}
      </div>
      {health?.pipeline.siteUrl && <div className="muted small" style={{ marginTop: 6 }}>{health.pipeline.libraryFolder} @ {health.pipeline.siteUrl}</div>}

      <div className="section-title">Piano App Service</div>
      {plan?.configured ? (
        <>
          <div className="tiles">
            <Tile
              ok={plan.sku !== "F1" || (plan.cpuPercent ?? 0) < plan.thresholdPercent}
              title="Livello attuale"
              value={plan.sku ?? "—"}
              sub={plan.tier ? `${plan.tier}${plan.alwaysOn ? " · Always On" : ""}` : ""}
            />
            <Tile
              ok={plan.cpuQuotaSeconds === 0 ? undefined : (plan.cpuPercent ?? 0) < plan.thresholdPercent}
              title="Quota CPU di oggi"
              value={plan.cpuQuotaSeconds > 0 ? `${plan.cpuPercent}%` : `${plan.cpuUsedSeconds}s`}
              sub={plan.cpuQuotaSeconds > 0
                ? `${plan.cpuUsedSeconds}s su ${Math.round(plan.cpuQuotaSeconds / 60)} minuti` +
                  (plan.quotaAssumed ? " (quota F1 nota: Azure non la espone)" : "")
                : "quota non esposta da Azure in questo momento"}
            />
            <Tile
              ok={plan.autoScaleUp}
              title="Salita automatica"
              value={plan.autoScaleUp ? `Sopra il ${plan.thresholdPercent}%` : "Disattivata"}
              sub={plan.autoScaleUp ? `sale a ${plan.upSku} prima dei 403` : "solo comando manuale"}
            />
          </div>
          <div className="muted small" style={{ margin: "6px 0 10px" }}>
            Su F1 la quota è di 60 minuti CPU al giorno: esaurita, il sito risponde 403 fino a
            mezzanotte. La discesa serale a F1 resta in mano alla Logic App api-plan-autoscaledown-001.
          </div>
          <div className="row" style={{ gap: 12 }}>
            <button className="btn" onClick={() => changePlan(plan.upSku)} disabled={scaling || plan.sku === plan.upSku}>
              {scaling ? "Cambio in corso…" : `Sali a ${plan.upSku}`}
            </button>
            <button className="btn ghost" onClick={() => changePlan("F1")} disabled={scaling || plan.sku === "F1"}>
              Torna a F1
            </button>
            {planNote && <span className={planNote.startsWith("Errore") ? "err" : "ok"}>{planNote}</span>}
          </div>
        </>
      ) : (
        <div className="empty">
          {plan?.error ?? "Autoscale del piano non configurato su questa istanza."}
        </div>
      )}

      <div className="section-title">KPI</div>
      {ov ? (
        <>
          <div className="kpis">
            <Kpi label="Job totali" value={String(ov.totals.jobs)} />
            <Kpi label="Immagini totali" value={String(ov.totals.items)} />
            <Kpi label="Oggi" value={String(ov.today.items)} />
            <Kpi label="Ultimi 7 giorni" value={String(ov.week.items)} />
            <Kpi label="Tasso successo" value={ov.successRate == null ? "—" : `${Math.round(ov.successRate * 100)}%`} />
            <Kpi label="Durata media" value={ov.avgDurationMs == null ? "—" : `${(ov.avgDurationMs / 1000).toFixed(1)} s`} />
          </div>
          <div className="statline">
            <span className="ok">✓ {ov.totals.completed} completate</span>
            <span className="disp">↗ {ov.totals.dispatched} inviate</span>
            <span className="proc">⟳ {ov.totals.processing} in corso</span>
            <span className="q">… {ov.totals.queued} in coda</span>
            <span className="err">✕ {ov.totals.failed} fallite</span>
          </div>
        </>
      ) : (
        <div className="empty">Nessun dato ancora.</div>
      )}
    </div>
  );
}

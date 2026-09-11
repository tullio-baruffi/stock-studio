import { useEffect, useState } from "react";
import { api, type Health, type Overview, type PlanState, type PunteggioStato } from "../api";
import AccessoSharePoint from "../components/AccessoSharePoint";

/** Nomi delle librerie come si chiamano nel Backoffice, non come si chiamano in SharePoint. */
const NOMI: Record<string, string> = {
  ImagesToClassify: "Da revisionare",
  ImagesToSend: "Pronti per l'invio",
  ImagesSent: "Pubblicati",
};

function quando(iso?: string): string {
  if (!iso) return "—";
  const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 5) return "adesso";
  if (s < 60) return `${s} secondi fa`;
  if (s < 3600) return `${Math.round(s / 60)} minuti fa`;
  return `${Math.round(s / 3600)} ore fa`;
}

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
  const [punteggi, setPunteggi] = useState<PunteggioStato | null>(null);

  useEffect(() => {
    const load = () => {
      api.health().then(setHealth).catch(() => setHealth(null));
      api.overview().then(setOv).catch(() => setOv(null));
    };
    load();
    const t = setInterval(load, 5000);
    return () => clearInterval(t);
  }, []);

  // Il deposito dei punteggi cambia a ritmo di pagine, non di secondi: ogni cinque basta e avanza,
  // e non aggiunge traffico a SharePoint (lo stato lo tiene il servizio in memoria).
  useEffect(() => {
    const load = () => api.backofficePunteggioStato().then(setPunteggi).catch(() => setPunteggi(null));
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

      {/*
        Due accessi diversi, e confonderli manda a cercare il guasto dove non è: il server parla a
        SharePoint col proprio certificato e non ha bisogno di nessuno, il browser ci parla con la
        sessione di chi guarda. Il pulsante qui sopra prova il primo, quello qui sotto il secondo.
      */}
      <div className="section-title">Accesso del browser a SharePoint</div>
      <div className="row" style={{ gap: 12, alignItems: "center" }}>
        <AccessoSharePoint />
        <span className="muted small" style={{ maxWidth: 620, lineHeight: 1.6 }}>
          Le anteprime non passano da questa applicazione: le chiede il browser direttamente alla
          libreria, ed è il motivo per cui la galleria si riempie senza consumare CPU del server.
          In cambio, senza sessione SharePoint le miniature restano riquadri vuoti — e la verifica
          qui sopra continuerebbe a dire che va tutto bene, perché il server usa il proprio
          certificato e non è toccato dal problema.
        </span>
      </div>

      <div className="section-title">Punteggi depositati in libreria</div>
      {punteggi?.coda ? (
        <>
          <div className="tiles">
            <Tile
              ok={punteggi.coda.ultimoErrore === null}
              title="Cosa sta facendo"
              value={punteggi.coda.riempimento.lavoro === "in attesa" ? "In attesa" : "Riempimento"}
              sub={punteggi.coda.riempimento.lavoro === "in attesa"
                ? "tutto allineato: scrive solo quando un punteggio cambia"
                : `${NOMI[punteggi.coda.riempimento.lavoro.replace("riempio ", "")] ?? punteggi.coda.riempimento.lavoro} · ultimo movimento ${quando(punteggi.coda.riempimento.da)}`}
            />
            <Tile
              ok={punteggi.coda.scartati === 0}
              title="Segnalazioni in coda"
              value={String(punteggi.coda.inCoda)}
              sub={punteggi.coda.scartati > 0
                ? `${punteggi.coda.scartati} scartate: la coda era piena, le riprende il riempimento`
                : `${punteggi.coda.scritti} depositate da chi ha sfogliato`}
            />
            <Tile
              title="Depositati dal riempimento"
              value={punteggi.coda.riempimento.depositati.toLocaleString("it-IT")}
              sub={punteggi.coda.riempimento.depositati === 0 && punteggi.coda.riempimento.visitati > 0
                ? `${punteggi.coda.riempimento.visitati.toLocaleString("it-IT")} passati in rassegna: erano già allineati`
                : `${punteggi.coda.riempimento.visitati.toLocaleString("it-IT")} elementi passati in rassegna`}
            />
            <Tile
              ok={punteggi.lotti.rifiutiPerCarico === 0}
              title="Scritture a gruppi"
              value={`${punteggi.lotti.riusciti.toLocaleString("it-IT")} riuscite`}
              sub={punteggi.lotti.rifiutiPerCarico > 0
                ? `${punteggi.lotti.rifiutiPerCarico} rifiuti per troppe richieste (riprovati dopo una pausa)`
                : punteggi.lotti.falliti > 0
                  ? `${punteggi.lotti.falliti} ripiegate su scrittura singola`
                  : "nessun rifiuto da SharePoint"}
            />
          </div>

          <div className="pg-librerie">
            {punteggi.coda.riempimento.librerie.map((l) => {
              const quota = l.totale > 0 ? Math.min(100, Math.round((l.visitati / l.totale) * 100)) : 0;
              return (
                <div key={l.libreria} className={`pg-riga ${l.interrotta ? "rotta" : l.completa ? "fatta" : l.inCorso ? "corso" : ""}`}>
                  <div className="pg-nome">
                    {NOMI[l.libreria] ?? l.libreria}
                    {l.completa && !l.interrotta && <span className="pg-segno"> ✓</span>}
                    {l.interrotta && <span className="pg-segno"> fermo</span>}
                    {l.inCorso && !l.completa && !l.interrotta && <span className="pg-segno"> in corso</span>}
                  </div>
                  <div className="pg-barra">
                    <div className="pg-riempita" style={{ width: `${l.completa ? 100 : quota}%` }} />
                  </div>
                  <div className="pg-conto">
                    {l.interrotta
                      ? l.interrotta
                      : l.completa
                        // Zero depositati su una libreria completa non è un fallimento: vuol dire che
                        // era già tutta a posto. Scritto come numero sembrerebbe il contrario.
                        ? l.depositati === 0
                          ? "già allineata"
                          : `${l.depositati.toLocaleString("it-IT")} depositati`
                        : l.totale > 0
                          ? `${l.visitati.toLocaleString("it-IT")} di ${l.totale.toLocaleString("it-IT")}`
                          : "—"}
                  </div>
                </div>
              );
            })}
          </div>

          <div className="muted small" style={{ margin: "8px 0 0" }}>
            Il punteggio si ricalcola comunque a ogni lettura: la colonna serve a filtrare tutta la
            libreria, non a decidere cosa si vede. Scrivere è la parte lenta -- SharePoint impiega
            circa mezzo secondo per elemento -- quindi avviene qui dietro, otto connessioni per
            volta, senza far aspettare la galleria. Dopo un riavvio il conteggio riparte da zero e
            le librerie vengono ripassate: dove il valore è già giusto non si riscrive niente.
          </div>
          {punteggi.coda.ultimoErrore && (
            <div className="err small" style={{ marginTop: 6 }}>Ultimo intoppo: {punteggi.coda.ultimoErrore}</div>
          )}
          {punteggi.lotti.ultimoErrore && (
            <div className="muted small" style={{ marginTop: 4 }}>Ultimo rifiuto: {punteggi.lotti.ultimoErrore}</div>
          )}
        </>
      ) : (
        <div className="empty">Servizio dei punteggi non raggiungibile.</div>
      )}

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

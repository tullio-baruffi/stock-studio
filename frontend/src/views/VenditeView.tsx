import { useCallback, useEffect, useRef, useState } from "react";
import { api, type SalesDna, type SalesImport, type SalesSummary, type SalesWarehouse } from "../api";
import { Attesa, Rotella, Segnaposto } from "../components/Attesa";

/**
 * Le vendite.
 *
 * Fino a qui l'applicazione sapeva dire quante immagini erano state prodotte, revisionate e
 * spedite: tutto quello che sta prima del mercato. Del mercato non sapeva niente -- se un file
 * avesse mai venduto, quale tema rendesse, se le correzioni ai metadati servissero a qualcosa.
 * Ogni scelta era un'opinione.
 *
 * Adobe non offre nessuna interfaccia programmabile agli autori e lo dichiara apertamente nella
 * documentazione per sviluppatori: niente API per le vendite, per i più venduti, per il
 * caricamento. L'unica porta è il file che il portale lascia scaricare. Quindi si importa a mano,
 * e la schermata è fatta perché farlo sia rapido e ripetibile invece che un rito.
 *
 * ## Sul reimportare
 * Le finestre di date si sovrappongono sempre -- si riscarica "l'ultimo mese" sopra un archivio
 * che arriva a ieri. Reimportare è previsto: le vendite già viste vengono riconosciute e non
 * sommate di nuovo, e il riepilogo dice quante erano. Senza questa garanzia nessuno importerebbe
 * mai due volte, e i dati resterebbero vecchi.
 */

/** Dove sta l'esportazione, detto una volta per chi non ci arriva tutti i giorni. */
const DOVE = "contributor.stock.adobe.com → Dettagli → Le mie statistiche → tipo di dati «Attività» → Esporta come CSV";

export default function VenditeView() {
  const [riepilogo, setRiepilogo] = useState<SalesSummary | null>(null);
  const [esito, setEsito] = useState<SalesImport | null>(null);
  const [magazzino, setMagazzino] = useState<SalesWarehouse | null>(null);
  const [dna, setDna] = useState<SalesDna | null>(null);
  const [errore, setErrore] = useState<string | null>(null);
  const [occupato, setOccupato] = useState(false);
  const [sopra, setSopra] = useState(false);
  const scelta = useRef<HTMLInputElement>(null);

  const carica = useCallback(async () => {
    try {
      setRiepilogo(await api.salesSummary());
    } catch (e) {
      setErrore((e as Error).message);
    }
  }, []);

  useEffect(() => { carica(); }, [carica]);

  const importa = async (file: File) => {
    setOccupato(true);
    setErrore(null);
    setEsito(null);
    try {
      const testo = await file.text();
      const r = await api.importSales(testo);
      setEsito(r);
      if (r.ok) await carica();
      else setErrore(r.error ?? "Importazione non riuscita.");
    } catch (e) {
      setErrore((e as Error).message);
    } finally {
      setOccupato(false);
    }
  };

  const incrocia = async () => {
    setOccupato(true);
    try {
      setMagazzino(await api.salesWarehouse("ImagesSent", 200));
    } catch (e) {
      setErrore((e as Error).message);
    } finally {
      setOccupato(false);
    }
  };

  /** Cerca cosa accomuna i pochi file che fanno la maggior parte dei guadagni. */
  const analizza = async () => {
    setOccupato(true);
    try {
      setDna(await api.salesDna(3));
    } catch (e) {
      setErrore((e as Error).message);
    } finally {
      setOccupato(false);
    }
  };

  const soldi = (v?: number) =>
    v === undefined ? "—" : `${v.toLocaleString("it-IT", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} $`;

  const quando = (iso?: string) => (iso ? new Date(iso).toLocaleDateString("it-IT") : "—");

  const pieno = riepilogo?.ok && !riepilogo.vuoto;

  return (
    <div className="vend">
      <section className="cfg-hero">
        <h2>Vendite</h2>
        <p className="muted small">
          Adobe non espone le vendite a nessun programma: l'unica strada è l'esportazione del portale.
          Trascina qui il file e diventa la misura di tutto il resto — quali temi rendono, quali file
          non hanno mai venduto, se le correzioni ai metadati stanno servendo.
        </p>
        <p className="muted small vend-dove">{DOVE}</p>
      </section>

      <div
        className={`drop ${sopra ? "over" : ""} ${occupato ? "busy" : ""}`}
        onDragOver={(e) => { e.preventDefault(); setSopra(true); }}
        onDragLeave={() => setSopra(false)}
        onDrop={(e) => {
          e.preventDefault();
          setSopra(false);
          const f = e.dataTransfer.files[0];
          if (f) importa(f);
        }}
        onClick={() => scelta.current?.click()}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") scelta.current?.click(); }}
      >
        <div className="drop-inner">
          <span className="drop-icon">↓</span>
          <strong>{occupato ? <><Rotella /> Leggo e importo il file…</> : "Trascina qui l'esportazione (downloads.csv)"}</strong>
          <span className="hint">
            Reimportare lo stesso periodo non fa danni: le vendite già viste vengono riconosciute.
          </span>
        </div>
        <input
          ref={scelta}
          type="file"
          accept=".csv,text/csv"
          hidden
          onChange={(e) => { const f = e.target.files?.[0]; if (f) importa(f); e.target.value = ""; }}
        />
      </div>

      {errore && <div className="notice err" role="alert">{errore}</div>}

      {!riepilogo && !errore && (
        <>
          <Attesa testo="Leggo le vendite in archivio…" />
          <Segnaposto righe={3} />
        </>
      )}

      {esito?.ok && (
        <div className="notice" role="status">
          <strong>{esito.importate}</strong> vendite nuove
          {esito.giaPresenti ? <> · {esito.giaPresenti} già in archivio</> : null}
          {esito.scartate ? <> · {esito.scartate} righe non riconosciute</> : null}
          {" · "}periodo {quando(esito.dal)} – {quando(esito.al)}
          {" · "}archivio a <strong>{esito.inArchivio}</strong> vendite
        </div>
      )}

      {riepilogo?.vuoto && !esito && (
        <div className="empty">
          Nessuna vendita in archivio. Importa l'esportazione per cominciare a misurare.
        </div>
      )}

      {pieno && (
        <>
          <div className="kpis">
            <div className="kpi">
              <div className="kpi-value">{soldi(riepilogo!.ricavi)}</div>
              <div className="kpi-label">Ricavi</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{riepilogo!.vendite}</div>
              <div className="kpi-label">Vendite</div>
            </div>
            <div className="kpi">
              {/*
                Il rendimento per download distingue un portfolio che vende bene da uno che vende
                molto e male: a parità di download, la licenza che paga cambia il risultato.
              */}
              <div className="kpi-value">{soldi(riepilogo!.perDownload)}</div>
              <div className="kpi-label">Per download</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{quando(riepilogo!.dal)}</div>
              <div className="kpi-label">Dal</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{quando(riepilogo!.al)}</div>
              <div className="kpi-label">Al</div>
            </div>
          </div>

          {riepilogo!.fileDistinti !== undefined && (
            <div className="notice" role="status">
              Hanno venduto <strong>{riepilogo!.fileDistinti}</strong> file diversi, e{" "}
              <strong>{riepilogo!.fileMetaRicavi}</strong> di questi fanno metà dei ricavi.
              {" "}Il resto del magazzino, in questo periodo, non ha prodotto nulla.
            </div>
          )}

          <div className="vend-due">
            <section>
              <div className="section-title">Per tipo di risorsa</div>
              <Tabella gruppi={riepilogo!.perTipo} soldi={soldi} />
            </section>
            <section>
              <div className="section-title">Per licenza</div>
              <Tabella gruppi={riepilogo!.perLicenza} soldi={soldi} />
            </section>
          </div>

          {riepilogo!.perMese && riepilogo!.perMese.length > 1 && (
            <section>
              <div className="section-title">Andamento</div>
              <div className="vend-mesi">
                {riepilogo!.perMese.map((m) => {
                  const max = Math.max(...riepilogo!.perMese!.map((x) => x.ricavi));
                  return (
                    <div key={m.mese} className="vend-mese" title={`${m.vendite} vendite`}>
                      <div className="vend-barra" style={{ height: `${Math.max(4, (m.ricavi / max) * 100)}%` }} />
                      <span className="vend-cifra">{Math.round(m.ricavi)}</span>
                      <span className="vend-etichetta">{m.mese.slice(5)}/{m.mese.slice(2, 4)}</span>
                    </div>
                  );
                })}
              </div>
            </section>
          )}

          <section>
            <div className="section-title">Quali serie rendono</div>
            <p className="muted small">
              Le immagini nascono a gruppi, e il nome del file lo racconta. Un singolo file che vende
              può essere fortuna; una serie che vende è un'indicazione su cosa produrre ancora.
            </p>
            <Tabella gruppi={riepilogo!.perSerie} soldi={soldi} etichetta="Serie" />
          </section>

          <section>
            <div className="section-title">Cosa vende</div>
            <table className="guide-table">
              <thead>
                <tr><th>File</th><th>Titolo</th><th>Tipo</th><th>Vendite</th><th>Ricavi</th></tr>
              </thead>
              <tbody>
                {riepilogo!.migliori?.map((m) => (
                  <tr key={m.file}>
                    <td className="vend-file">{m.file}</td>
                    <td className="vend-tit">{m.titolo}</td>
                    <td>{m.tipo}</td>
                    <td>{m.vendite}</td>
                    <td>{soldi(m.ricavi)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>

          <section>
            <div className="section-title">Cosa accomuna i best seller</div>
            <p className="muted small">
              Confronta il vocabolario dei titoli dei pochi file che fanno metà dei ricavi con
              quello di tutti gli altri. Le parole in cima compaiono molto più spesso fra i primi:
              non è una spiegazione — un titolo non fa vendere da solo — ma è il segnale più
              concreto disponibile su cosa il mercato stia comprando davvero.
            </p>
            <button className="btn small" onClick={analizza} disabled={occupato}>
              {occupato ? <><Rotella /> Analizzo…</> : "Analizza i best seller"}
            </button>

            {dna?.ok && (
              <>
                <div className="notice" role="status">
                  <strong>{dna.vitali}</strong> file su {dna.totaleFile} fanno{" "}
                  {soldi(dna.quotaRicaviVitali)} su {soldi(dna.ricaviTotali)}. Vendono in media{" "}
                  <strong>{dna.venditeMediaVitali}</strong> volte contro {dna.venditeMediaResto} del
                  resto, e rendono {soldi(dna.perDownloadVitali)} per download contro{" "}
                  {soldi(dna.perDownloadResto)}.
                  {dna.tipiVitali?.length ? (
                    <> Composizione: {dna.tipiVitali.map((t) => `${t.file} ${t.tipo}`).join(" · ")}.</>
                  ) : null}
                </div>

                <div className="vend-parole">
                  {dna.parole?.map((p) => (
                    <span
                      key={p.parola}
                      className="vend-parola"
                      title={`${p.neiPochi} best seller · ${p.altrove} fra gli altri`}
                    >
                      {p.parola}
                      <em>×{p.rapporto}</em>
                    </span>
                  ))}
                </div>

                <table className="guide-table">
                  <thead>
                    <tr><th>File</th><th>Titolo</th><th>Tipo</th><th>Vendite</th><th>Ricavi</th><th>Custom</th></tr>
                  </thead>
                  <tbody>
                    {dna.elenco?.map((e) => (
                      <tr key={e.file}>
                        <td className="vend-file">{e.file}</td>
                        <td className="vend-tit">{e.titolo}</td>
                        <td>{e.tipo}</td>
                        <td>{e.vendite}</td>
                        <td>{soldi(e.ricavi)}</td>
                        <td>{e.quotaCustom}%</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}
            {dna && !dna.ok && <div className="notice err">{dna.error}</div>}
          </section>

          <section>
            <div className="section-title">Incrocio col magazzino</div>
            <p className="muted small">
              L'esportazione riporta il nome del file originale, quindi le vendite si possono
              appaiare ai file pubblicati. Attenzione a come si legge: il confronto prende un
              campione della libreria, non tutti i 10.000 file, e le vendite importate coprono solo
              il periodo scaricato. «Mai venduto» qui significa «non in questo periodo, in questo
              campione» — non è un verdetto definitivo sul file.
            </p>
            <button className="btn small" onClick={incrocia} disabled={occupato}>
              {occupato ? <><Rotella /> Incrocio…</> : "Confronta con i pubblicati"}
            </button>

            {magazzino?.ok && (
              <>
                <div className="notice" role="status">
                  Su {magazzino.esaminati} file esaminati: <strong>{magazzino.conVendite}</strong> hanno
                  venduto, <strong>{magazzino.senzaVendite}</strong> mai.
                </div>
                <table className="guide-table">
                  <thead>
                    <tr><th>File</th><th>Titolo</th><th>Vendite</th><th>Ricavi</th></tr>
                  </thead>
                  <tbody>
                    {magazzino.righe
                      ?.slice()
                      .sort((a, b) => b.ricavi - a.ricavi)
                      .slice(0, 40)
                      .map((r) => (
                        <tr key={r.file} className={r.vendite === 0 ? "vend-muto" : ""}>
                          <td className="vend-file">{r.file}</td>
                          <td className="vend-tit">{r.titolo}</td>
                          <td>{r.vendite || "—"}</td>
                          <td>{r.vendite ? soldi(r.ricavi) : "—"}</td>
                        </tr>
                      ))}
                  </tbody>
                </table>
              </>
            )}
            {magazzino && !magazzino.ok && (
              <div className="notice err">{magazzino.error}</div>
            )}
          </section>
        </>
      )}
    </div>
  );
}

function Tabella(
  { gruppi, soldi, etichetta = "Categoria" }:
  { gruppi?: { nome: string; vendite: number; ricavi: number; perDownload: number }[];
    soldi: (v?: number) => string; etichetta?: string }
) {
  if (!gruppi?.length) return <div className="empty">—</div>;
  return (
    <table className="guide-table">
      <thead>
        <tr><th>{etichetta}</th><th>Vendite</th><th>Ricavi</th><th>Per download</th></tr>
      </thead>
      <tbody>
        {gruppi.map((g) => (
          <tr key={g.nome}>
            <td>{g.nome}</td>
            <td>{g.vendite}</td>
            <td>{soldi(g.ricavi)}</td>
            <td>{soldi(g.perDownload)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

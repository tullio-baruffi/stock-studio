import { useCallback, useEffect, useState } from "react";
import { api, type Strategia } from "../api";
import { Attesa, Rotella } from "../components/Attesa";

/**
 * La pagina che dice cosa fare, non cosa è successo.
 *
 * La scheda Vendite misura il passato. Questa prende gli stessi numeri e li mette accanto alla
 * regola con cui Adobe decide chi mettere in vetrina, perché è lì che il confronto diventa una
 * decisione: se la classifica ordina per rapporto fra caricamenti e vendite, allora un magazzino
 * di file che non vendono non è neutro -- fa male.
 *
 * ## Perché la regola è citata per esteso
 * Sta scritta nel portale autori, sotto "Come funziona", e quasi nessuno la legge. Riportarla
 * parola per parola invece di riassumerla evita che una parafrasi diventi col tempo il ricordo di
 * qualcosa che Adobe non ha mai detto. Le conseguenze che seguono sono invece dichiaratamente
 * un'interpretazione.
 */

/** Quanti file l'autore stima di aver consegnato ad Adobe negli ultimi sei mesi. */
const CHIAVE_CARICATI = "caricati6m";

export default function StrategiaView() {
  const [q, setQ] = useState<Strategia | null>(null);
  const [errore, setErrore] = useState<string | null>(null);
  const [caricati, setCaricati] = useState<string>(() => {
    try { return localStorage.getItem(CHIAVE_CARICATI) ?? ""; } catch { return ""; }
  });

  const leggi = useCallback(async (n: number) => {
    try {
      setQ(await api.strategia(n));
    } catch (e) {
      setErrore((e as Error).message);
    }
  }, []);

  useEffect(() => { leggi(Number(caricati) || 0); }, [leggi]);

  const applica = () => {
    const n = Number(caricati) || 0;
    try { localStorage.setItem(CHIAVE_CARICATI, String(n)); } catch { /* preferenza non essenziale */ }
    leggi(n);
  };

  const soldi = (v?: number) =>
    v === undefined ? "—" : `${v.toLocaleString("it-IT", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} $`;
  const pct = (v?: number | null) => (v === null || v === undefined ? "—" : `${v > 0 ? "+" : ""}${v}%`);
  const data = (iso?: string) => (iso ? new Date(iso).toLocaleDateString("it-IT") : "—");

  const a = q?.archivio;
  const t = q?.tendenza;
  const r = q?.finestra?.rapporto;

  /** Quota del magazzino che ha prodotto qualcosa, sui file effettivamente pubblicati. */
  const pubblicati = 10055;
  const quotaViva = a ? Math.round((a.fileVenduti / pubblicati) * 1000) / 10 : null;

  return (
    <div className="strat">
      <section className="cfg-hero">
        <h2>Strategia</h2>
        <p className="muted small">
          Come Adobe sceglie chi mettere in vetrina, e come stanno i tuoi numeri rispetto a quella
          regola. Tutto quello che vedi qui sotto è calcolato sulle vendite importate: se importi
          altri periodi, cambia.
        </p>
      </section>

      {errore && <div className="notice err" role="alert">{errore}</div>}
      {!q && !errore && <Attesa testo="Calcolo il quadro sulle vendite in archivio…" />}
      {q?.vuoto && (
        <div className="empty">
          Nessuna vendita in archivio. Importa l'esportazione dalla scheda Vendite: senza quella
          questa pagina non ha niente da misurare.
        </div>
      )}

      {/* ---- La regola ---- */}
      <section className="strat-sec">
        <div className="section-title">La regola dei «Venditori di punta recenti»</div>
        <blockquote className="strat-cita">
          Per ogni tipo di risorsa è stilato un elenco dei <strong>200 autori</strong> che hanno
          generato il maggior numero di vendite nella settimana precedente, tenendo in
          considerazione <strong>solo le risorse caricate negli ultimi sei mesi</strong>. L'elenco è
          quindi ordinato in base al <strong>rapporto tra caricamenti e vendite</strong> e i primi
          10 autori di questo elenco sono messi in evidenza. Gli autori possono essere messi in
          evidenza al massimo <strong>una volta ogni cinque settimane</strong>.
          <cite>Adobe Stock, portale autori → Venditori di punta recenti → Come funziona</cite>
        </blockquote>

        <div className="strat-cons">
          <article>
            <h3>Non è volume, è efficienza</h3>
            <p>
              L'ordine è dato dal rapporto fra caricamenti e vendite. Non vince chi vende di più,
              ma chi vende di più <em>per ogni file caricato</em>.
            </p>
          </article>
          <article>
            <h3>Il magazzino vecchio non conta</h3>
            <p>
              Contano solo le risorse caricate negli <strong>ultimi sei mesi</strong>. Chi ha
              diecimila file fermi e cento recenti parte come chi ne ha cento in tutto.
            </p>
          </article>
          <article>
            <h3>Tre classifiche separate</h3>
            <p>
              Foto, Illustrazioni e <strong>Vettoriali</strong> hanno graduatorie distinte. La
              concorrenza non è la stessa nelle tre.
            </p>
          </article>
          <article>
            <h3>Non serve essere grandi</h3>
            <p>
              Nella top 10 vettoriali compaiono autori con «1.000+» download totali accanto ad altri
              con oltre un milione. Il rapporto conta più della stazza.
            </p>
          </article>
        </div>
      </section>

      {/* ---- Il rapporto ---- */}
      {!q?.vuoto && (
        <section className="strat-sec">
          <div className="section-title">Il tuo rapporto</div>
          <p className="muted small">
            Il numeratore lo conosco: sono le tue vendite. Il denominatore no — quanti file hai
            consegnato ad Adobe negli ultimi sei mesi lo sai solo tu, e lo leggi da{" "}
            <em>File caricati</em> nel portale. Scrivilo qui e il rapporto si calcola.
          </p>

          <div className="row" style={{ gap: 8, flexWrap: "wrap", margin: "10px 0" }}>
            <input
              className="track-input"
              type="number"
              min={0}
              placeholder="file consegnati negli ultimi 6 mesi"
              value={caricati}
              onChange={(e) => setCaricati(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") applica(); }}
              style={{ maxWidth: 280 }}
            />
            <button className="btn small" onClick={applica} disabled={!q}>{!q ? <><Rotella /> Leggo…</> : "Calcola"}</button>
          </div>

          <div className="kpis">
            <div className="kpi">
              <div className="kpi-value">{q?.settimana?.vendite ?? "—"}</div>
              <div className="kpi-label">Vendite ultimi 7 giorni</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{q?.mese?.vendite ?? "—"}</div>
              <div className="kpi-label">Ultimi 30 giorni</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{r ? r.venditeSeiMesi : "—"}</div>
              <div className="kpi-label">Vendite in 6 mesi</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{r ? r.perFileCaricato : "—"}</div>
              <div className="kpi-label">Vendite per file caricato</div>
            </div>
          </div>

          {q?.settimana?.perTipo?.length ? (
            <p className="muted small">
              Nell'ultima settimana:{" "}
              {q.settimana.perTipo.map((x) => `${x.vendite} ${x.tipo}`).join(" · ")}. La classifica
              è per tipo di risorsa, quindi è questa ripartizione a contare, non il totale.
            </p>
          ) : (
            <p className="muted small">Nessuna vendita negli ultimi sette giorni fra quelle importate.</p>
          )}

          {r && (
            <div className={`notice ${r.perFileCaricato < 0.5 ? "err" : ""}`} role="status">
              Con <strong>{r.caricati}</strong> file consegnati e{" "}
              <strong>{r.venditeSeiMesi}</strong> vendite nella finestra, il rapporto è di{" "}
              <strong>{r.perFileCaricato}</strong> vendite per file caricato.{" "}
              {r.perFileCaricato < 0.5
                ? "Sotto mezza vendita per file: ogni consegna che non vende peggiora la posizione in graduatoria."
                : "Ogni file consegnato sta producendo più di mezza vendita: è il verso giusto."}
            </div>
          )}
        </section>
      )}

      {/* ---- Salute del portfolio ---- */}
      {a && (
        <section className="strat-sec">
          <div className="section-title">Salute del portfolio</div>
          <div className="kpis">
            <div className="kpi">
              <div className="kpi-value">{a.fileVenduti}</div>
              <div className="kpi-label">File che hanno venduto</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{quotaViva}%</div>
              <div className="kpi-label">del magazzino pubblicato</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{a.fileMetaRicavi}</div>
              <div className="kpi-label">file fanno metà dei ricavi</div>
            </div>
            <div className="kpi">
              <div className="kpi-value">{soldi(a.ricavi)}</div>
              <div className="kpi-label">nel periodo importato</div>
            </div>
          </div>
          <p className="muted small">
            Periodo: {data(a.dal)} – {data(a.al)}, {a.vendite} vendite. Il conto del magazzino usa
            i {pubblicati.toLocaleString("it-IT")} file pubblicati: è una stima, non un dato letto
            da SharePoint.
          </p>
        </section>
      )}

      {/* ---- Tendenza ---- */}
      {t && (
        <section className="strat-sec">
          <div className="section-title">Dove sta andando</div>
          <table className="guide-table">
            <thead>
              <tr><th>Periodo</th><th>Vendite</th><th>Ricavi</th><th>Per download</th></tr>
            </thead>
            <tbody>
              <tr>
                <td>12 mesi precedenti</td>
                <td>{t.precedente.vendite}</td>
                <td>{soldi(t.precedente.ricavi)}</td>
                <td>{soldi(t.precedente.perDownload)}</td>
              </tr>
              <tr>
                <td>ultimi 12 mesi</td>
                <td>{t.recente.vendite}</td>
                <td>{soldi(t.recente.ricavi)}</td>
                <td>{soldi(t.recente.perDownload)}</td>
              </tr>
              <tr className="strat-var">
                <td>variazione</td>
                <td>{pct(t.variazioneVendite)}</td>
                <td>{pct(t.variazioneRicavi)}</td>
                <td>—</td>
              </tr>
            </tbody>
          </table>
        </section>
      )}

      {/* ---- Per tipo ---- */}
      {q?.perTipoAnno?.length ? (
        <section className="strat-sec">
          <div className="section-title">Quale formato rende, negli ultimi 12 mesi</div>
          <table className="guide-table">
            <thead>
              <tr><th>Tipo</th><th>Vendite</th><th>Ricavi</th><th>Per download</th></tr>
            </thead>
            <tbody>
              {q.perTipoAnno.map((x) => (
                <tr key={x.tipo}>
                  <td>{x.tipo}</td>
                  <td>{x.vendite}</td>
                  <td>{soldi(x.ricavi)}</td>
                  <td>{soldi(x.perDownload)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted small">
            La classifica dei Venditori di punta è separata per tipo: la categoria dove hai il
            rapporto migliore è quella su cui conviene concentrare le consegne.
          </p>
        </section>
      ) : null}

      {/* ---- Cosa fare ---- */}
      <section className="strat-sec">
        <div className="section-title">Cosa fare, in ordine</div>
        <ol className="strat-passi">
          <li>
            <strong>Consegnare poco e scelto, non tutto.</strong> Ogni file che non vende è un
            divisore in più nel rapporto. Lotti piccoli e regolari sono anche quello che Adobe
            raccomanda apertamente, perché tengono il contenuto visibile più a lungo invece di
            bruciare la finestra tutta insieme.
          </li>
          <li>
            <strong>Riordinare le prime dieci keyword.</strong> La documentazione Adobe
            dice che le prime dieci pesano più delle altre nella ricerca, e le modifiche
            vengono reindicizzate in poche ore. È l'unico intervento sui file già pubblicati con un
            effetto documentato.
          </li>
          <li>
            <strong>Restare sui soggetti che già vendono.</strong> La scheda Vendite → «Cosa
            accomuna i best seller» li ricava dai tuoi dati invece che da un'impressione.
          </li>
          <li>
            <strong>Scegliere una categoria e presidiarla.</strong> Tre graduatorie separate
            significano tre gare diverse: entrare in quella meno affollata è più facile che
            competere dove ci sono tutti.
          </li>
        </ol>
        <p className="muted small">
          Una cautela: Adobe non dichiara da nessuna parte che comparire fra i Venditori di punta
          porti un vantaggio nell'algoritmo di ricerca. Il valore certo è la vetrina; tutto il resto
          è ipotesi.
        </p>
      </section>
    </div>
  );
}

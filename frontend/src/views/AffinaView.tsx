import { useState } from "react";
import { api, type TuneKeywords, type TuneMute, type TuneOverlap } from "../api";
import { Attesa, Rotella } from "../components/Attesa";

/**
 * Gli interventi sul magazzino già pubblicato.
 *
 * Rifare diecimila file non è un'opzione, ma due cose si cambiano dall'esterno senza ricaricare
 * niente: l'ordine delle keyword, che Adobe pesa in modo diseguale, e la sovrapposizione fra i
 * file di una stessa serie, che li fa competere fra loro. La terza scheda guarda avanti invece
 * che indietro: quali soggetti conviene non consegnare affatto.
 *
 * ## Perché l'anteprima è obbligatoria
 * Il riordino tocca file già in vendita. Un'operazione in blocco su un magazzino di migliaia di
 * pezzi, fatta al buio, è il genere di cosa che si scopre di aver sbagliato il giorno dopo. Qui si
 * vede prima cosa cambierebbe, riga per riga, e solo dopo si applica.
 */

const LIBRERIE = [
  { id: "ImagesToSend", label: "Pronti per l'invio" },
  { id: "ImagesToClassify", label: "Da revisionare" },
  { id: "ImagesSent", label: "Pubblicati" },
];

type Scheda = "ordine" | "sovrapposizione" | "muti";

export default function AffinaView() {
  const [scheda, setScheda] = useState<Scheda>("ordine");
  const [libreria, setLibreria] = useState("ImagesToSend");
  const [occupato, setOccupato] = useState(false);
  const [errore, setErrore] = useState<string | null>(null);
  const [esito, setEsito] = useState<string | null>(null);

  const [kw, setKw] = useState<TuneKeywords | null>(null);
  const [ov, setOv] = useState<TuneOverlap | null>(null);
  const [mu, setMu] = useState<TuneMute | null>(null);

  const con = async <T,>(f: () => Promise<T>, poi: (v: T) => void) => {
    setOccupato(true);
    setErrore(null);
    try {
      const v = await f();
      poi(v);
    } catch (e) {
      setErrore((e as Error).message);
    } finally {
      setOccupato(false);
    }
  };

  const applica = async () => {
    const ids = kw?.righe?.filter((r) => r.cambia).map((r) => r.id) ?? [];
    if (ids.length === 0) return;
    if (!confirm(
      `Riordinare le keyword di ${ids.length} file?\n\n` +
      "Nessuna keyword viene aggiunta o tolta: cambia solo l'ordine, e l'operazione si può rifare " +
      "in senso inverso ripetendo il riordino su un ordine diverso."
    )) return;

    setOccupato(true);
    setEsito(null);
    try {
      const r = await api.tuneKeywordsApply(libreria, ids);
      if (!r.ok) setErrore(r.error ?? "Applicazione non riuscita.");
      else {
        setEsito(`${r.scritti} file riscritti, ${r.invariati} erano già in ordine.`);
        setKw(await api.tuneKeywordsPreview(libreria, 50));
      }
    } catch (e) {
      setErrore((e as Error).message);
    } finally {
      setOccupato(false);
    }
  };

  return (
    <div className="aff">
      <section className="cfg-hero">
        <h2>Affina</h2>
        <p className="muted small">
          Quello che si può ancora migliorare sui file già consegnati, senza ricaricarli. Ogni
          strumento mostra prima cosa cambierebbe.
        </p>
      </section>

      <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
        <div className="subtabs">
          <button className={`subtab ${scheda === "ordine" ? "on" : ""}`} onClick={() => setScheda("ordine")}>
            Ordine delle keyword
          </button>
          <button className={`subtab ${scheda === "sovrapposizione" ? "on" : ""}`} onClick={() => setScheda("sovrapposizione")}>
            Serie che si cannibalizzano
          </button>
          <button className={`subtab ${scheda === "muti" ? "on" : ""}`} onClick={() => setScheda("muti")}>
            Cosa non consegnare
          </button>
        </div>
        <select className="sat-input" value={libreria} onChange={(e) => setLibreria(e.target.value)}>
          {LIBRERIE.map((l) => <option key={l.id} value={l.id}>{l.label}</option>)}
        </select>
      </div>

      {errore && <div className="notice err" role="alert">{errore}</div>}
      {esito && <div className="notice" role="status">{esito}</div>}

      {scheda === "ordine" && (
        <section className="aff-sec">
          <p className="muted small">
            Adobe pesa le <strong>prime dieci keyword</strong> molto più delle altre: sono le
            ricerche in cui il file compete davvero. Una parola che compare su quasi tutta la
            libreria non ne distingue nessuno, e in prima posizione non compra visibilità. Lo
            strumento <strong>misura</strong> quanto ogni keyword è diffusa fra i file esaminati e
            manda in fondo le più comuni, lasciando tutto il resto esattamente dov'era. Nessuna
            keyword viene aggiunta o tolta.
          </p>
          <button className="btn small" disabled={occupato}
                  onClick={() => con(() => api.tuneKeywordsPreview(libreria, 50), setKw)}>
            {occupato ? <><Rotella /> Leggo la libreria…</> : "Mostra cosa cambierebbe"}
          </button>

          {occupato && !kw && <Attesa testo="Leggo la libreria e misuro quanto e' diffusa ogni keyword…" />}
          {kw?.ok && (
            <>
              <div className="notice" role="status">
                Su {kw.esaminati} file, <strong>{kw.daCambiare}</strong> avrebbero le prime dieci
                keyword diverse da adesso.
                {kw.daCambiare ? (
                  <> <button className="btn small" onClick={applica} disabled={occupato}>
            {occupato ? <><Rotella /> Applico…</> : `Applica a tutti e ${kw.daCambiare}`}
                  </button></>
                ) : null}
              </div>

              {kw.diffuse?.length ? (
                <div className="aff-diffuse">
                  <div className="aff-eti">
                    presenti in oltre il {kw.sogliaPercento}% dei file esaminati, quindi non distinguono
                  </div>
                  <div className="aff-kw">
                    {kw.diffuse.map((d) => (
                      <span key={d.parola} className="aff-tag" title={`${d.file} file su ${kw.esaminati}`}>
                        {d.parola} <em>{d.quota}%</em>
                      </span>
                    ))}
                  </div>
                </div>
              ) : (
                <p className="muted small">
                  Nessuna keyword supera la soglia in questo campione: scendono solo quelle
                  dell'elenco fisso, che non distinguono in nessuna libreria.
                </p>
              )}

              {kw.diffuse?.length ? (
                <p className="muted small">
                  Queste parole sono state passate anche al <strong>generatore di metadati</strong>:
                  le prossime immagini nasceranno senza metterle fra le prime dieci. Correggere qui
                  e continuare a produrle sarebbe una rincorsa senza fine.
                </p>
              ) : null}

              <div className="aff-lista">
                {kw.righe?.filter((r) => r.cambia).slice(0, 25).map((r) => (
                  <article key={r.id} className="aff-riga">
                    <div className="aff-file">{r.file}</div>
                    <div className="aff-tit">{r.titolo}</div>
                    <div className="aff-kw">
                      <span className="aff-eti">ora</span>
                      {r.prima.map((k, n) => <span key={n} className="aff-tag">{k}</span>)}
                    </div>
                    <div className="aff-kw">
                      <span className="aff-eti buono">dopo</span>
                      {r.dopo.map((k, n) => <span key={n} className="aff-tag buono">{k}</span>)}
                    </div>
                  </article>
                ))}
              </div>
            </>
          )}
          {kw && !kw.ok && <div className="notice err">{kw.error}</div>}
        </section>
      )}

      {scheda === "sovrapposizione" && (
        <section className="aff-sec">
          <p className="muted small">
            Le immagini nascono a gruppi e il generatore le descrive quasi allo stesso modo. Se i
            file di una serie condividono quasi tutte le keyword finiscono nelle stesse ricerche e
            si tolgono il posto a vicenda. La pratica consigliata è che una serie ne condivida{" "}
            <strong>al massimo la metà</strong>, differenziando il resto.
          </p>
          <button className="btn small" disabled={occupato}
                  onClick={() => con(() => api.tuneOverlap(libreria, 150), setOv)}>
            {occupato ? <><Rotella /> Calcolo…</> : "Misura la sovrapposizione"}
          </button>

          {occupato && !ov && <Attesa testo="Raggruppo i file per serie e confronto le keyword…" />}
          {ov?.ok && (
            <>
              <div className={`notice ${ov.serieOltreSoglia ? "err" : ""}`} role="status">
                {ov.serieTrovate} serie trovate, <strong>{ov.serieOltreSoglia}</strong> oltre il{" "}
                {ov.sogliaPercento}% di keyword condivise.
              </div>
              <table className="guide-table">
                <thead>
                  <tr><th>Serie</th><th>File</th><th>Keyword medie</th><th>Condivise</th><th>%</th><th>Esempi</th></tr>
                </thead>
                <tbody>
                  {ov.serie?.slice(0, 25).map((s) => (
                    <tr key={s.serie} className={s.oltreSoglia ? "aff-male" : ""}>
                      <td className="aff-file">{s.serie}</td>
                      <td>{s.file}</td>
                      <td>{s.keywordMedie}</td>
                      <td>{s.condivise}</td>
                      <td><strong>{s.percentuale}%</strong></td>
                      <td className="aff-tit">{s.esempi.slice(0, 8).join(", ")}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
          {ov && !ov.ok && <div className="notice err">{ov.error}</div>}
        </section>
      )}

      {scheda === "muti" && (
        <section className="aff-sec">
          <p className="muted small">
            La classifica dei venditori di punta ordina per rapporto fra caricamenti e vendite:
            ogni file consegnato che poi non vende <strong>peggiora</strong> la posizione. Qui i
            file in attesa sono confrontati con lo storico della loro serie. Quelli la cui famiglia
            non ha mai venduto nulla sono i primi da tenere indietro.
          </p>
          <button className="btn small" disabled={occupato}
                  onClick={() => con(() => api.tuneMute(libreria, 150), setMu)}>
            {occupato ? <><Rotella /> Confronto…</> : "Confronta con lo storico"}
          </button>

          {occupato && !mu && <Attesa testo="Confronto i file in attesa con lo storico delle vendite…" />}
          {mu?.ok && (
            <>
              <div className="notice" role="status">
                Su {mu.esaminati} file in attesa: <strong>{mu.conStorico}</strong> appartengono a
                serie che hanno già venduto, <strong>{mu.senzaStorico}</strong> a serie senza
                storico.
              </div>
              <table className="guide-table">
                <thead>
                  <tr><th>File</th><th>Serie</th><th>Vendite della serie</th><th>Ricavi</th></tr>
                </thead>
                <tbody>
                  {mu.righe?.slice(0, 30).map((r) => (
                    <tr key={r.id} className={r.venditeSerie === 0 ? "aff-male" : ""}>
                      <td className="aff-file">{r.file}</td>
                      <td>{r.serie}</td>
                      <td>{r.venditeSerie || "—"}</td>
                      <td>{r.venditeSerie ? `${r.ricaviSerie} $` : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <p className="muted small">
                Una serie senza storico non è necessariamente cattiva: può essere semplicemente
                nuova, come lo erano i vettoriali sei mesi fa. Il segnale vale per i soggetti già
                provati a lungo senza risultato.
              </p>
            </>
          )}
          {mu && !mu.ok && <div className="notice err">{mu.error}</div>}
        </section>
      )}
    </div>
  );
}

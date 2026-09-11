import { useCallback, useEffect, useState } from "react";
import { api, type BonificaQuadro, type BonificaRiga } from "../api";
import { Attesa, Rotella } from "../components/Attesa";
import AuthImage from "../components/AuthImage";
import { AvvisoSessioneSharePoint } from "../components/AccessoSharePoint";

/**
 * Che fare delle immagini già online.
 *
 * Il Backoffice serve a lavorare su ciò che deve ancora uscire. Questa pagina serve al problema
 * opposto, che era rimasto senza risposta: diecimila immagini sono già pubblicate, quasi novemila
 * non hanno mai venduto, e finora l'unica cosa che si poteva fare era guardarle.
 *
 * Non è una classifica né un cruscotto: è una coda di lavoro. Ogni riga dice cosa conviene fare e
 * perché, e il perché cita la fonte -- dove c'è una regola Adobe la si nomina, dove c'è solo una
 * nostra scelta lo si ammette.
 */

const COLORI: Record<string, string> = {
  RilavoraMetadati: "bo-azione",
  SoggettoDebole: "bo-fermo",
  LasciaStare: "bo-bene",
  Aspetta: "bo-presto",
};

const ETICHETTE: Record<string, string> = {
  RilavoraMetadati: "Rilavora i metadati",
  SoggettoDebole: "Non sono i metadati",
  LasciaStare: "Lascia stare",
  Aspetta: "Troppo presto",
};

export default function BonificaView() {
  const [quadro, setQuadro] = useState<BonificaQuadro | null>(null);
  const [righe, setRighe] = useState<BonificaRiga[]>([]);
  const [token, setToken] = useState<string | null>(null);
  const [altre, setAltre] = useState(true);
  const [caricamento, setCaricamento] = useState(true);
  const [errore, setErrore] = useState<string | null>(null);
  const [soglia, setSoglia] = useState(79);
  const [rigenerando, setRigenerando] = useState<number | null>(null);
  const [esito, setEsito] = useState<string | null>(null);

  useEffect(() => {
    api.bonificaQuadro()
      .then((q) => { if (q.ok) setQuadro(q); else setErrore(q.error ?? "Quadro non disponibile."); })
      .catch((e) => setErrore((e as Error).message));
  }, []);

  const leggi = useCallback(async (pageToken: string | null, azzera: boolean) => {
    setCaricamento(true);
    try {
      const r = await api.bonificaCoda(soglia, pageToken);
      if (!r.ok) { setErrore(r.error ?? "Lettura non riuscita."); setAltre(false); return; }
      setErrore(null);
      setRighe((cur) => {
        if (azzera) return r.righe;
        const visti = new Set(cur.map((x) => x.id));
        return [...cur, ...r.righe.filter((x) => !visti.has(x.id))];
      });
      setToken(r.nextPageToken ?? null);
      setAltre(Boolean(r.nextPageToken));
    } catch (e) {
      setErrore((e as Error).message);
      setAltre(false);
    } finally {
      setCaricamento(false);
    }
  }, [soglia]);

  useEffect(() => { setToken(null); setAltre(true); leggi(null, true); }, [leggi]);

  /**
   * Rigenerare qui non basta a sistemare nulla su Adobe, e dirlo è metà del lavoro: i metadati
   * nuovi restano in libreria, e vanno riportati sul portale a mano. Per i contributor Adobe non
   * espone un'API -- lo dichiara nella propria documentazione per gli sviluppatori -- quindi non
   * esiste un modo di automatizzare quel passaggio.
   */
  const rigenera = async (id: number) => {
    setRigenerando(id);
    setEsito(null);
    try {
      const r = await api.backofficeRegenerate("ImagesSent", id);
      if (r.ok) {
        setEsito(`${r.fileName}: metadati rigenerati in libreria. ` +
                 "Ora vanno riportati sul portale Adobe a mano: per i contributor non esiste un'API.");
        setRighe((cur) => cur.filter((x) => x.id !== id));
      } else {
        setEsito(r.error ?? "Rigenerazione non riuscita.");
      }
    } catch (e) {
      setEsito((e as Error).message);
    } finally {
      setRigenerando(null);
    }
  };

  /**
   * Prepara tutto ciò che va incollato su Adobe, nell'ordine in cui lo chiede il portale.
   *
   * Serve perché il passaggio finale resta a mano: Adobe non espone un'API per i contributor -- lo
   * dichiara nella propria documentazione per gli sviluppatori -- e la modifica di un'immagine già
   * pubblicata si fa un campo per volta, passando il mouse sul campo finché non compare la matita.
   * Non potendo togliere quel lavoro, almeno gli si toglie la parte di trascrivere.
   */
  const copia = async (r: BonificaRiga) => {
    const testo = `${r.title}\n\n${r.keywords.join(", ")}`;
    try {
      await navigator.clipboard.writeText(testo);
      setEsito(`Copiati titolo e ${r.keywords.length} keyword di ${r.fileName}: su Adobe apri l'immagine, ` +
               "passa il mouse sul campo finché compare la matita, incolla e salva.");
    } catch {
      setEsito("Copia non riuscita: il browser l'ha impedita.");
    }
  };

  return (
    <div className="bonifica">
      <AvvisoSessioneSharePoint />
      <div className="section-title">Il portfolio già pubblicato</div>

      {!quadro && !errore && <Attesa testo="Incrocio vendite e metadati…" />}

      {quadro && (
        <>
          <div className="tiles">
            <div className="tile">
              <div className="tile-title">Immagini online</div>
              <div className="tile-value">{quadro.totale.toLocaleString("it-IT")}</div>
              <div className="tile-sub">nella libreria dei pubblicati</div>
            </div>
            <div className="tile ok">
              <div className="tile-title">Hanno venduto</div>
              <div className="tile-value">{quadro.cheVendono.toLocaleString("it-IT")}</div>
              <div className="tile-sub">
                {quadro.percentualeCheVende}% · {quadro.ricaviTotali.toFixed(2)} $ in tutto
              </div>
            </div>
            <div className="tile">
              <div className="tile-title">Non hanno mai venduto</div>
              <div className="tile-value">{quadro.cheNonVendono.toLocaleString("it-IT")}</div>
              <div className="tile-sub">non è un difetto: è la norma dello stock</div>
            </div>
            <div className="tile err">
              <div className="tile-title">Con metadati deboli</div>
              <div className="tile-value">{quadro.conMetadatiDeboli.toLocaleString("it-IT")}</div>
              <div className="tile-sub">sotto {quadro.sogliaMetadatiDeboli}: qui c'è margine documentato</div>
            </div>
          </div>

          <div className="bo-fasce">
            {quadro.fasce.map((f) => (
              <div key={f.fascia} className={`bo-fascia ${f.deboli ? "debole" : ""}`}>
                <span className="bo-fascia-n">{f.quanti < 0 ? "—" : f.quanti.toLocaleString("it-IT")}</span>
                <span className="bo-fascia-l">{f.fascia}</span>
              </div>
            ))}
          </div>

          {/*
            Il punto che cambia tutto, e che meritava di essere scritto: cancellare non serve.
            La ricerca sulla documentazione Adobe non ha trovato nessuna prova che eliminare le
            immagini ferme giovi alle altre. Senza quella prova, cancellare è una perdita certa in
            cambio di un beneficio mai dimostrato.
          */}
          <div className="cn-avviso" style={{ marginTop: 12 }}>
            <strong>Sulla cancellazione.</strong> Adobe consente di eliminare le immagini pubblicate, ma
            non documenta da nessuna parte che farlo giovi alle altre: non esiste un punteggio di
            qualità del portfolio, né una penalizzazione dichiarata per chi ha molti file fermi.
            Un'immagine che non vende non costa niente e potrebbe vendere domani. Per questo qui non
            viene mai consigliata.
          </div>
        </>
      )}

      <div className="section-title" style={{ marginTop: 18 }}>Su cosa conviene lavorare</div>

      <div className="row" style={{ gap: 12, alignItems: "center", marginBottom: 10 }}>
        <span className="muted small">Mostra quelle con punteggio fino a</span>
        {[59, 69, 74, 79].map((s) => (
          <button key={s} className={`btn small ${soglia === s ? "primary" : "ghost"}`}
                  onClick={() => setSoglia(s)} disabled={caricamento}>
            {s}
          </button>
        ))}
        {caricamento && <Rotella />}
      </div>

      {errore && <div className="empty err">{errore}</div>}
      {esito && <div className="cn-avviso">{esito}</div>}

      {righe.length === 0 && !caricamento && !errore && (
        <div className="empty">Nessuna immagine in questa fascia.</div>
      )}

      <div className="bo-coda">
        {righe.map((r) => (
          <article key={r.id} className={`bo-riga ${COLORI[r.consiglio] ?? ""}`}>
            <div className="bo-mini"><AuthImage src={r.previewUrl} alt={r.fileName} /></div>
            <div className="bo-corpo">
              <div className="bo-testa">
                <span className="bo-consiglio">{ETICHETTE[r.consiglio] ?? r.consiglio}</span>
                <span className="bo-punteggio">{r.punteggio}</span>
                {r.vendite > 0 && (
                  <span className="bo-vendite">{r.vendite} vendite · {r.ricavi.toFixed(2)} $</span>
                )}
                {r.giorniOnline !== null && (
                  <span className="muted small">online da {r.giorniOnline} giorni</span>
                )}
              </div>
              <div className="bo-titolo">{r.title || <em>senza titolo</em>}</div>
              <div className="bo-perche">{r.perche}</div>
              {r.problemi.length > 0 && (
                <ul className="bo-problemi">
                  {r.problemi.map((p, k) => <li key={k}>{p.message}</li>)}
                </ul>
              )}
              <div className="row" style={{ gap: 8, marginTop: 8 }}>
                {r.consiglio === "RilavoraMetadati" && (
                  <button className="btn small primary" onClick={() => rigenera(r.id)} disabled={rigenerando !== null}>
                    {rigenerando === r.id ? <><Rotella /> Rigenero…</> : "Rigenera i metadati"}
                  </button>
                )}
                <button className="btn small ghost" onClick={() => copia(r)}>
                  Copia titolo e keyword
                </button>
                <a className="btn small ghost" href="https://contributor.stock.adobe.com/it/portfolio"
                   target="_blank" rel="noreferrer">Apri il portfolio Adobe</a>
                <a className="btn small ghost" href={r.fileUrl} target="_blank" rel="noreferrer">Apri il file</a>
              </div>
            </div>
          </article>
        ))}
      </div>

      {altre && righe.length > 0 && (
        <button className="btn" onClick={() => leggi(token, false)} disabled={caricamento}>
          {caricamento ? <><Rotella /> Carico…</> : "Carica altre"}
        </button>
      )}
    </div>
  );
}

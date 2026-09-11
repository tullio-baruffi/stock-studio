import { useCallback, useEffect, useState } from "react";
import { api, type Insights } from "../api";
import { Attesa, Rotella } from "../components/Attesa";

/**
 * Il cruscotto delle domande, non dei numeri.
 *
 * Ogni riquadro qui sotto esiste perché risponde a una domanda che qualcuno si è fatto davvero.
 * "Quante vendite in tutto" non è una di quelle: è un numero che sale e basta, e non ha mai
 * suggerito a nessuno cosa fare il giorno dopo. Sotto ogni cifra c'è quindi la domanda a cui
 * risponde, perché un numero senza la sua domanda è decorazione.
 */

function segnoDi(v: number | null | undefined): string {
  if (v === null || v === undefined) return "";
  return v > 0 ? "su" : v < 0 ? "giu" : "";
}

function Riquadro({ domanda, valore, sotto, stato }:
                  { domanda: string; valore: string; sotto?: React.ReactNode; stato?: "su" | "giu" | "" }) {
  return (
    <div className={`in-card ${stato ?? ""}`}>
      <div className="in-domanda">{domanda}</div>
      <div className="in-valore">{valore}</div>
      {sotto && <div className="in-sotto">{sotto}</div>}
    </div>
  );
}

export default function InsightsView() {
  const [d, setD] = useState<Insights | null>(null);
  const [errore, setErrore] = useState<string | null>(null);
  const [caricamento, setCaricamento] = useState(true);

  const leggi = useCallback((aggiorna = false) => {
    setCaricamento(true);
    api.insights("ImagesSent", 600, aggiorna)
      .then((r) => { if (r.ok) { setD(r); setErrore(null); } else setErrore(r.error ?? "Non disponibile."); })
      .catch((e) => setErrore((e as Error).message))
      .finally(() => setCaricamento(false));
  }, []);

  useEffect(() => { leggi(); }, [leggi]);

  if (caricamento && !d) return <Attesa testo="Incrocio vendite e magazzino…" />;
  if (errore) return <div className="empty err">{errore}</div>;
  if (!d) return null;

  const a30 = d.andamento.ultimi30;
  const a90 = d.andamento.ultimi90;
  const a365 = d.andamento.ultimi365;
  const md = d.metadatiFannoVendere;
  const massimoMese = Math.max(...d.stagionalita.mesi.map((m) => m.ricavi), 1);

  return (
    <div className="insights">
      <div className="row" style={{ justifyContent: "space-between", alignItems: "baseline" }}>
        <div className="section-title">Sto andando bene?</div>
        <button className="btn small ghost" onClick={() => leggi(true)} disabled={caricamento}>
          {caricamento ? <><Rotella /> Aggiorno…</> : "Aggiorna"}
        </button>
      </div>

      <div className="in-griglia">
        <Riquadro
          domanda="Ultimi 30 giorni"
          valore={`${a30.ricavi.toFixed(2)} $`}
          stato={segnoDi(a30.variazione) as "su" | "giu" | ""}
          sotto={a30.variazione !== null
            ? <>{a30.vendite} vendite · <strong>{a30.variazione > 0 ? "+" : ""}{a30.variazione}%</strong> sui 30 precedenti</>
            : <>{a30.vendite} vendite</>}
        />
        <Riquadro
          domanda="Ultimi 90 giorni"
          valore={`${a90.ricavi.toFixed(2)} $`}
          stato={segnoDi(a90.variazione) as "su" | "giu" | ""}
          sotto={a90.variazione !== null
            ? <>{a90.vendite} vendite · <strong>{a90.variazione > 0 ? "+" : ""}{a90.variazione}%</strong> sui 90 precedenti</>
            : <>{a90.vendite} vendite</>}
        />
        <Riquadro
          domanda="Ultimo anno"
          valore={`${a365.ricavi.toFixed(2)} $`}
          stato={segnoDi(a365.variazione) as "su" | "giu" | ""}
          sotto={a365.variazione !== null
            ? <>{a365.vendite} vendite · <strong>{a365.variazione > 0 ? "+" : ""}{a365.variazione}%</strong> sull'anno prima</>
            : <>{a365.vendite} vendite</>}
        />
      </div>

      <div className="section-title" style={{ marginTop: 20 }}>Il lavoro rende?</div>

      {/*
        Il portfolio Adobe è più grande della libreria: contiene anche i caricamenti precedenti a
        questa applicazione. Senza dirlo, ogni rapporto qui sotto sembrerebbe riferito a tutto.
      */}
      {!d.copertura.esatta && (
        <div className="in-avviso">
          <strong>Numeri ancora dedotti, non contati.</strong> L'aggancio fra le vendite e i file
          in libreria è una stima basata sulla serie di appartenenza, finché la libreria non viene
          scorsa per intero. Il giro parte da solo e dura una quarantina di secondi.{" "}
          <button className="btn small ghost" style={{ marginLeft: 4 }}
                  onClick={() => { api.insightsRiaggancia().then(() => setTimeout(() => leggi(true), 60000)); }}>
            Fallo adesso
          </button>
        </div>
      )}

      {d.copertura.misurata && d.copertura.venditeFuori > 0 && (
        <div className="in-avviso">
          I conti qui sotto riguardano <strong>solo i file che stanno in libreria</strong>:{" "}
          {d.copertura.venditeGestite.toLocaleString("it-IT")} vendite per{" "}
          {d.copertura.ricaviGestiti.toFixed(2)} $. Le altre{" "}
          {d.copertura.venditeFuori.toLocaleString("it-IT")} vendite ({d.copertura.ricaviFuori.toFixed(2)} $,
          il {(100 - d.copertura.percentualeRicaviGestiti).toFixed(0)}% dei ricavi) vengono da{" "}
          {d.copertura.fileFuori.toLocaleString("it-IT")} file caricati su Adobe prima di quest'app
          e mai passati da qui: non c'è modo di rilavorarli da questa applicazione, e mescolarli
          agli altri gonfierebbe ogni media.
        </div>
      )}

      <div className="in-griglia">
        {/*
          È il numero che di solito non si guarda, e l'unico che dica se produrre conviene: si
          divide per tutto ciò che si è caricato, non per la parte fortunata che ha venduto.
        */}
        <Riquadro
          domanda="Quanto ha reso ogni file prodotto"
          valore={`${d.efficienza.ricavoPerFileProdotto.toFixed(2)} $`}
          sotto={<>in {d.efficienza.fileInPortfolio.toLocaleString("it-IT")} file, su tutta la storia</>}
        />
        <Riquadro
          domanda="E ogni file che ha venduto"
          valore={`${d.efficienza.ricavoPerFileCheVende.toFixed(2)} $`}
          sotto={<>{d.efficienza.fileCheVendono.toLocaleString("it-IT")} file · il {d.efficienza.percentualeCheVende}% del portfolio</>}
        />
        <Riquadro
          domanda="Quanto dipendi da pochi pezzi"
          valore={`${d.concentrazione.fileCheFannoMetaRicavi} file`}
          sotto={<>fanno metà dei ricavi: il <strong>{d.concentrazione.percentualeDelPortfolio}%</strong> del portfolio</>}
        />
      </div>

      {/*
        La domanda che giustifica tutto il resto dell'applicazione. Se i file che vendono non
        avessero metadati migliori dei fermi, rifinire titoli e keyword sarebbe un rito.
      */}
      <div className="section-title" style={{ marginTop: 20 }}>I metadati fanno vendere?</div>

      {md.esaminati > 0 ? (
        <div className="in-confronto">
          <div className="in-barra-riga">
            <span className="in-etichetta">Chi ha venduto</span>
            <div className="in-barra"><div className="in-riempita ok" style={{ width: `${md.punteggioMedianoVenduti}%` }} /></div>
            <span className="in-num">{md.punteggioMedianoVenduti}</span>
          </div>
          <div className="in-barra-riga">
            <span className="in-etichetta">Chi non ha venduto</span>
            <div className="in-barra"><div className="in-riempita" style={{ width: `${md.punteggioMedianoFermi}%` }} /></div>
            <span className="in-num">{md.punteggioMedianoFermi}</span>
          </div>
          <div className="in-tabella" style={{ marginTop: 12 }}>
            <div className="in-riga" style={{ gridTemplateColumns: "1fr 120px 120px" }}>
              <span className="in-tipo" />
              <span className="muted small">ha venduto</span>
              <span className="muted small">fermo</span>
            </div>
            <div className="in-riga" style={{ gridTemplateColumns: "1fr 120px 120px" }}>
              <span className="in-tipo">Punteggio mediano</span>
              <span>{md.punteggioMedianoVenduti}</span>
              <span>{md.punteggioMedianoFermi}</span>
            </div>
            <div className="in-riga" style={{ gridTemplateColumns: "1fr 120px 120px" }}>
              <span className="in-tipo">Punteggio medio</span>
              <span>{md.mediaVenduti}</span>
              <span>{md.mediaFermi}</span>
            </div>
            <div className="in-riga" style={{ gridTemplateColumns: "1fr 120px 120px" }}>
              <span className="in-tipo">Quanti stanno sopra 90</span>
              <span>{md.sopra90Venduti}%</span>
              <span>{md.sopra90Fermi}%</span>
            </div>
          </div>
          <p className="muted small" style={{ marginTop: 10, maxWidth: 760, lineHeight: 1.6 }}>
            Punteggio mediano su {md.esaminati.toLocaleString("it-IT")} immagini{" "}
            {d.copertura.esatta ? "in libreria" : "campionate"}{" "}
            ({md.venduti} hanno venduto, {md.fermi} no)
            {md.collisioni > 0 && <> · {md.collisioni} nomi ridotti alla stessa chiave</>}.{" "}
            {!md.attendibile ? (
              <><strong>Il campione è troppo magro per rispondere.</strong> Con {md.venduti} file
              venduti la mediana non regge: servirebbero almeno {md.soglia} per lato. Le misure qui
              sopra vanno guardate come un'anticipazione, non come un verdetto.</>
            ) : md.differenza === 0 && md.mediaVenduti > md.mediaFermi ? (
              <><strong>La mediana è identica ({md.punteggioMedianoVenduti} da entrambe le parti),
              ma la media e la coda no.</strong> Chi vende ha in media{" "}
              {(md.mediaVenduti - md.mediaFermi).toFixed(1)} punti in più, e sta sopra 90 nel{" "}
              {md.sopra90Venduti}% dei casi contro il {md.sopra90Fermi}%. È una differenza reale ma
              piccola, e su {md.venduti} file venduti può ancora essere il caso: dice che curare i
              metadati non fa male, non che basti a far vendere. Il grosso lo decide il soggetto.</>
            ) : Math.abs(md.differenza) < 2 && Math.abs(md.mediaVenduti - md.mediaFermi) < 1 ? (
              <><strong>Nessuna differenza, né nella mediana né nella media.</strong> Su questi dati
              i metadati non sono ciò che separa un'immagine che vende da una ferma — il che non
              significa che siano inutili (senza, non si viene trovati affatto), ma che oltre una
              soglia decente il ritorno di limarli ancora è indistinguibile da zero.</>
            ) : md.differenza > 0 || md.mediaVenduti > md.mediaFermi ? (
              <><strong>Chi vende ha metadati migliori</strong> ({md.differenza > 0 ? `${md.differenza} punti di mediana, ` : ""}
              {(md.mediaVenduti - md.mediaFermi).toFixed(1)} di media). È una correlazione, non una
              prova: chi ha metadati migliori potrebbe avere anche immagini migliori. Ma va nella
              direzione attesa.</>
            ) : (
              <><strong>Chi vende ha metadati peggiori</strong> ({Math.abs(md.differenza)} punti di
              mediana in meno). Contro ogni attesa: vale la pena guardarci dentro prima di spendere
              altro tempo sui metadati.</>
            )}
          </p>
        </div>
      ) : (
        <div className="empty">Campionamento non riuscito.</div>
      )}

      <div className="section-title" style={{ marginTop: 20 }}>Quanta pazienza serve</div>

      {d.pazienza.misurate < 30 && (
        <div className="in-avviso">
          Misurato su {d.pazienza.misurate} file soltanto: quelli che hanno venduto{" "}
          <em>e</em> di cui si conosce la data di ingresso in libreria. Troppo pochi per farne una
          regola — vale come indizio.
        </div>
      )}

      <div className="in-griglia">
        <Riquadro
          domanda="Giorni prima della prima vendita"
          valore={`${d.pazienza.giorniMedianiAllaPrimaVendita}`}
          sotto={<>mediana su {d.pazienza.misurate} file misurati</>}
        />
        <Riquadro
          domanda="Vende entro il primo mese"
          valore={`${d.pazienza.entroUnMese}%`}
          sotto={<>di quelli che poi hanno venduto</>}
        />
        <Riquadro
          domanda="Ci mette più di sei mesi"
          valore={`${d.pazienza.oltreSeiMesi}%`}
          sotto={<>ecco perché non si giudica troppo presto</>}
        />
      </div>

      <div className="section-title" style={{ marginTop: 20 }}>Quando compra il mercato</div>

      <div className="in-mesi">
        {d.stagionalita.mesi.map((m) => (
          <div key={m.mese} className="in-mese" title={`${m.vendite} vendite · ${m.ricavi.toFixed(2)} $`}>
            <div className="in-colonna">
              <div className="in-colonna-piena"
                   style={{ height: `${Math.max(4, (m.ricavi / massimoMese) * 100)}%` }} />
            </div>
            <span className="in-mese-l">{m.nome.slice(0, 3)}</span>
          </div>
        ))}
      </div>
      <p className="muted small">
        Migliori: <strong>{d.stagionalita.migliori.join(", ")}</strong> · più deboli:{" "}
        {d.stagionalita.peggiori.join(", ")}. Calcolato su tutte le vendite dei 26 mesi in archivio,
        libreria compresa e non: la stagionalità è del mercato, non del singolo file. Sono i mesi in
        cui il mercato <em>compra</em>, quindi per esserci bisogna pubblicare prima — la moderazione
        e l'indicizzazione vogliono il loro tempo.
      </p>

      <div className="section-title" style={{ marginTop: 20 }}>Cosa si vende</div>

      <p className="muted small" style={{ maxWidth: 800 }}>
        Su tutte le {d.efficienza.venditeTotali + d.copertura.venditeFuori} vendite in archivio,
        libreria compresa e non: il prezzo per download è una caratteristica del mercato Adobe, non
        di questo portfolio, e restringerlo ai soli file gestiti lo renderebbe solo più rumoroso.
      </p>

      <div className="in-tabella">
        {d.mercato.perTipo.map((t) => (
          <div key={t.tipo} className="in-riga">
            <span className="in-tipo">{t.tipo}</span>
            <span className="muted small">{t.vendite.toLocaleString("it-IT")} vendite</span>
            <span>{t.ricavi.toFixed(2)} $</span>
            <strong>{t.perDownload.toFixed(2)} $ / download</strong>
          </div>
        ))}
      </div>

      {d.spenti.length > 0 && (
        <>
          <div className="section-title" style={{ marginTop: 20 }}>Vendevano, e hanno smesso</div>
          <p className="muted small" style={{ maxWidth: 780 }}>
            Hanno venduto almeno tre volte, poi più niente da oltre sei mesi. Non è detto sia un
            problema — una moda passa — ma se un soggetto rendeva e si è fermato, vale la pena
            chiedersi se il mercato è cambiato o se è sceso nei risultati di ricerca.
          </p>
          <div className="in-tabella">
            {d.spenti.map((s) => (
              <div key={s.file} className="in-riga">
                <span className="in-tipo">{s.file}</span>
                <span className="muted small">{s.vendite} vendite</span>
                <span>{s.ricavi.toFixed(2)} $</span>
                <strong>fermo da {s.fermoDa} giorni</strong>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

import { useEffect, useState } from "react";
import { api, type BonificaQuadro, type SalesSummary } from "../api";
import { Attesa } from "../components/Attesa";

/**
 * Quello che si è imparato, in un posto solo.
 *
 * Ogni cosa qui dentro è stata verificata: le regole leggendo la documentazione Adobe, i numeri
 * misurandoli sull'archivio vendite e sulla libreria. Ma verificato non vuol dire vero allo stesso
 * modo, e la differenza conta: una regola scritta da Adobe vincola, un numero misurato sul proprio
 * portfolio descrive, e una soglia scelta da noi è solo una scelta.
 *
 * Per questo ogni voce porta la sua provenienza. Senza, in sei mesi non si distinguerebbe più cosa
 * è legge da cosa era un'ipotesi di un martedì pomeriggio.
 */

type Fonte = "adobe" | "misurato" | "scelta";

const ETICHETTA_FONTE: Record<Fonte, string> = {
  adobe: "documentato da Adobe",
  misurato: "misurato sui tuoi dati",
  scelta: "scelta nostra",
};

function Voce({ fonte, titolo, children, url }:
              { fonte: Fonte; titolo: string; children: React.ReactNode; url?: string }) {
  return (
    <div className={`cs-voce cs-${fonte}`}>
      <div className="cs-voce-testa">
        <span className="cs-titolo">{titolo}</span>
        <span className="cs-fonte">{ETICHETTA_FONTE[fonte]}</span>
      </div>
      <div className="cs-corpo">{children}</div>
      {url && (
        <a className="cs-url" href={url} target="_blank" rel="noreferrer">
          {url.replace(/^https:\/\/(helpx\.adobe\.com|developer\.adobe\.com)/, "…")}
        </a>
      )}
    </div>
  );
}

export default function ConsigliView() {
  const [vendite, setVendite] = useState<SalesSummary | null>(null);
  const [quadro, setQuadro] = useState<BonificaQuadro | null>(null);
  const [caricamento, setCaricamento] = useState(true);

  useEffect(() => {
    Promise.allSettled([api.salesSummary(), api.bonificaQuadro()])
      .then(([v, q]) => {
        if (v.status === "fulfilled" && v.value.ok) setVendite(v.value);
        if (q.status === "fulfilled" && q.value.ok) setQuadro(q.value);
      })
      .finally(() => setCaricamento(false));
  }, []);

  const perTipo = vendite?.perTipo ?? [];
  const foto = perTipo.find((t) => t.nome === "photos");
  const illu = perTipo.find((t) => t.nome === "illustrations");

  return (
    <div className="consigli">
      <div className="section-title">Quello che si è imparato</div>
      <p className="muted small" style={{ maxWidth: 780, lineHeight: 1.6 }}>
        Ogni voce dice da dove viene. Una <strong>regola Adobe</strong> vincola e si cita la fonte;
        un <strong>numero misurato</strong> descrive questo portfolio e non vale per altri; una
        <strong> scelta nostra</strong> è discutibile per definizione. Confonderle è il modo più
        rapido di prendere una decisione sbagliata con la coscienza tranquilla.
      </p>

      {caricamento && <Attesa testo="Raccolgo i numeri…" />}

      <div className="section-title" style={{ marginTop: 20 }}>Le keyword</div>

      <Voce fonte="adobe" titolo="Le prime 10 pesano più di tutte le altre"
            url="https://helpx.adobe.com/stock/contributor/content-policies-guidelines/metadata/change-order-keywords.html">
        «The first 10 keywords carry the most weight in search results». Lo dice in due punti
        distinti della documentazione. Tutto ciò che viene dopo la decima conta molto meno: se una
        keyword importante sta in ventesima posizione, è come non esserci.
        <br />
        <em>Avevo creduto fossero sette</em>, sulla base di una dichiarazione riferita di un
        rappresentante Adobe. Fra una dichiarazione e la documentazione, vale la documentazione.
      </Voce>

      <Voce fonte="adobe" titolo="Le parole del titolo devono stare fra le prime 10"
            url="https://helpx.adobe.com/it/stock/contributor/help/generative-ai-content.html">
        «Includere le singole parole e i concetti del titolo nelle 10 parole chiave principali per
        una spinta aggiuntiva nella rilevanza di ricerca». È la regola più operativa che esista:
        se il titolo dice «golden retriever puppy» e fra le prime dieci keyword non c'è
        «puppy», si sta sprecando il titolo.
      </Voce>

      <Voce fonte="scelta" titolo="Dieci concetti diversi, non dieci sinonimi">
        Adobe non lo chiede. Ma dieci caselle occupate da un solo concetto scritto in cinque modi
        sono cinque caselle buttate: chi cerca «dog» non digita anche «doggy», digita «pet» o
        «puppy». Il generatore ora garantisce dieci idee distinte nella testa.
      </Voce>

      <div className="section-title" style={{ marginTop: 20 }}>I divieti che costano l'account</div>

      <Voce fonte="adobe" titolo="Mai nomi di persone, artisti, marchi o eventi reali"
            url="https://helpx.adobe.com/it/stock/contributor/help/generative-ai-content.html">
        Titoli e keyword non possono contenere nomi di artisti sotto copyright, nomi di persone,
        riferimenti a eventi reali, nomi di agenzie governative o proprietà intellettuale di terzi.
        La sanzione è dichiarata: «rimozione del contenuto o chiusura dell'account del
        collaboratore».
        <br />
        <strong>Verificato su 500 tue immagini pubblicate: nessuna violazione.</strong> Le 93 parole
        maiuscole trovate erano tutte razze di cani — Collie, Corgi, Samoyed, Cavalier King Charles —
        e aggettivi geografici.
      </Voce>

      <Voce fonte="adobe" titolo="Non scrivere «IA generativa» nei metadati"
            url="https://helpx.adobe.com/it/stock/contributor/help/generative-ai-content.html">
        È controintuitivo: a dichiarare che un'immagine è generata non è il testo, ma la casella
        «Creato utilizzando strumenti IA generativa», che va spuntata sempre. Metterlo anche nel
        titolo o nelle keyword è esplicitamente sconsigliato.
        <br />
        <strong>Verificato: zero occorrenze nel tuo portfolio.</strong>
      </Voce>

      <Voce fonte="adobe" titolo="Niente parametri del prompt nel titolo"
            url="https://helpx.adobe.com/it/stock/contributor/help/generative-ai-content.html">
        «Non mantenere parametri tecnici ripetitivi come caratteristiche specifiche della
        piattaforma, pesi o impostazioni del tuo prompt nel titolo».
        <br />
        <strong>Verificato:</strong> l'unico sospetto era «render», che però compare solo come
        keyword «3d render» — una descrizione legittima — e mai nei titoli.
      </Voce>

      <Voce fonte="adobe" titolo="Non caricare varianti dello stesso prompt"
            url="https://helpx.adobe.com/it/stock/contributor/help/generative-ai-content.html">
        «Non inviare più versioni dello stesso messaggio o iterazioni simili di un messaggio»: è
        la policy anti-spam.
        <br />
        <strong>Da tenere d'occhio:</strong> nel campione ci sono gruppi da 4 a 7 immagini con lo
        stesso incipit di titolo. Sono già passate dalla moderazione, quindi non è un problema
        aperto — ma è un limite da conoscere prima della prossima infornata.
      </Voce>

      <div className="section-title" style={{ marginTop: 20 }}>Cosa si può fare dopo la pubblicazione</div>

      <Voce fonte="adobe" titolo="I metadati si possono modificare, anche dopo l'approvazione"
            url="https://helpx.adobe.com/stock/contributor/manage-your-portfolio/edit-content.html">
        «Content can be edited before submission or after approval and publication, but not during
        moderation». È la regola che rende possibile bonificare un portfolio già online, e per un
        anno era stata data per impossibile.
        <br />
        La procedura è manuale: apri l'immagine, passa il mouse sul campo finché non compare la
        matita, modifica, salva. «Changes are reflected immediately in My Portfolio, while updated
        metadata may take time to appear in customer search results».
      </Voce>

      <Voce fonte="adobe" titolo="Non esiste un'API per gli autori"
            url="https://developer.adobe.com/stock/docs/getting-started/">
        «Approvals will NOT be given to Contributor use cases because there is no API for Stock
        Contributors. There is no API to get sales data, see top sellers, see creation dates or
        upload new Stock content.»
        <br />
        Per questo l'applicazione non può aggiornare Adobe da sé: il passaggio finale resta a mano,
        e ogni promessa contraria sarebbe falsa.
      </Voce>

      <Voce fonte="misurato" titolo="Il CSV non serve per i file già pubblicati">
        «Carica CSV» esiste nel portale, ma solo nella sezione <em>File caricati</em> — quella dei
        file in attesa di invio. Sui pubblicati non c'è. La documentazione era ambigua; il portale
        no.
      </Voce>

      <Voce fonte="misurato" titolo="Il portfolio non si può esportare">
        In Dettagli → Le mie statistiche si esportano solo dati di vendita: Più venduti, Attività,
        Download, Reddito, Ritenute, Altri pagamenti. <strong>Non esiste un elenco esportabile del
        portfolio con i nomi file.</strong>
        <br />
        Conseguenza pratica: la corrispondenza fra nome file e identificativo Adobe si ricava solo
        dal CSV delle vendite. Ce l'hai per i file che hanno venduto, non per gli altri — che sono
        proprio quelli da rilavorare.
      </Voce>

      <div className="section-title" style={{ marginTop: 20 }}>I numeri di questo portfolio</div>

      {quadro && (
        <Voce fonte="misurato" titolo="Vende una immagine su otto">
          {quadro.cheVendono.toLocaleString("it-IT")} file su {quadro.totale.toLocaleString("it-IT")}
          {" "}hanno venduto almeno una volta: il {quadro.percentualeCheVende}%.
          Gli altri {quadro.cheNonVendono.toLocaleString("it-IT")} non hanno mai venduto.
          <br />
          <strong>Non è un difetto, è la norma dello stock.</strong> Il valore di un portfolio sta
          nella coda: poche immagini portano quasi tutto, e non si sa in anticipo quali.
        </Voce>
      )}

      {vendite && (
        <Voce fonte="misurato" titolo="Le illustrazioni rendono più delle foto">
          {foto && <>Foto: {foto.vendite.toLocaleString("it-IT")} vendite a {foto.perDownload.toFixed(2)} $ per download.<br /></>}
          {illu && <>Illustrazioni: {illu.vendite.toLocaleString("it-IT")} vendite a <strong>{illu.perDownload.toFixed(2)} $</strong> per download.<br /></>}
          {foto && illu && (
            <>Differenza: <strong>+{Math.round(((illu.perDownload - foto.perDownload) / foto.perDownload) * 100)}%</strong> a favore delle illustrazioni.</>
          )}
          <br />
          <em>Attenzione a cosa se ne conclude:</em> non è detto che convenga riclassificare. Non è
          documentato che il tipo di risorsa si possa cambiare dopo la pubblicazione, e comunque
          servirebbe guardare le immagini una per una — «whimsical» è un'atmosfera, non una tecnica.
        </Voce>
      )}

      {quadro && (
        <Voce fonte="misurato" titolo="Dove sta il margine">
          {quadro.conMetadatiDeboli.toLocaleString("it-IT")} immagini hanno metadati sotto{" "}
          {quadro.sogliaMetadatiDeboli}. Su quelle, e solo su quelle, c'è un intervento con una base
          documentata: Adobe dice che rifinire i metadati migliora la visibilità.
          <div className="cs-fasce">
            {quadro.fasce.map((f) => (
              <span key={f.fascia} className={f.deboli ? "cs-fascia debole" : "cs-fascia"}>
                {f.quanti < 0 ? "—" : f.quanti.toLocaleString("it-IT")} <em>{f.fascia}</em>
              </span>
            ))}
          </div>
        </Voce>
      )}

      <div className="section-title" style={{ marginTop: 20 }}>Come comportarsi</div>

      <Voce fonte="scelta" titolo="Non toccare ciò che vende">
        Adobe non dichiara cosa succede al posizionamento di un'immagine dopo una modifica dei
        metadati. Su una che funziona, quindi, il guadagno non è misurabile e il rischio nemmeno:
        l'unica cosa certa è che si sta cambiando qualcosa che stava andando bene.
      </Voce>

      <Voce fonte="scelta" titolo="Sei mesi prima di dire che non vende">
        Un'immagine pubblicata da poche settimane che non ha venduto non dice niente: non ha ancora
        attraversato una stagione. La soglia è arbitraria — Adobe sulla durata non si esprime — ma
        serve una soglia, altrimenti si rilavora per impazienza.
      </Voce>

      <Voce fonte="scelta" titolo="Cancellare non serve">
        Adobe consente di eliminare le immagini pubblicate, ma <strong>non documenta da nessuna
        parte che farlo giovi alle altre</strong>: non esiste un punteggio di qualità del portfolio,
        né una penalizzazione dichiarata per chi ha molti file fermi. Un'immagine che non vende non
        costa niente e potrebbe vendere domani. Cancellarla è una perdita certa in cambio di un
        beneficio mai dimostrato.
      </Voce>

      <Voce fonte="misurato" titolo="Cosa compra il tuo mercato">
        Fra i tuoi file che vendono di più, il vocabolario ricorrente è dominato dagli animali
        (fra il 41% e il 58% a seconda del taglio), poi da feste e compleanni. È il segnale più
        concreto ricavabile senza rifare a mano la storia di diecimila file — ma resta una
        correlazione, non una spiegazione: il titolo non fa vendere da solo.
      </Voce>

      <div className="section-title" style={{ marginTop: 20 }}>Cose da non dimenticare</div>

      <Voce fonte="misurato" titolo="2.680 file caricati e mai inviati">
        Stanno nella sezione <em>File caricati</em> del portale: caricati, mai mandati in revisione.
        Finché restano lì non possono vendere. È l'unico posto dove il CSV di Adobe funziona
        davvero, se mai li si volesse completare in blocco.
      </Voce>
    </div>
  );
}

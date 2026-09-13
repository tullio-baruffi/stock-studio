import { useEffect, useState } from "react";
import { api, type ConfigTracciato } from "../api";

/**
 * La guida ai numeri del tracciato a colori.
 *
 * ## Perche' una pagina e non dei suggerimenti piu' lunghi
 * Perche' questi numeri non si capiscono leggendoli: si capiscono **vedendoli**. La riga accanto a
 * un cursore puo' dire che alzare i granelli toglie i frammenti, ma quanto sia "troppo" lo dice
 * solo un'immagine in cui le pieghe della pinna si sono spezzate. Ogni striscia qui sotto e' lo
 * stesso ritaglio della stessa illustrazione, tracciato davvero con quei valori: non sono disegni
 * esplicativi, sono il risultato.
 *
 * ## Come e' ordinata
 * Prima il rimedio, poi la spiegazione. Chi arriva qui di solito ha davanti un disegno venuto male
 * e vuole sapere quale cursore toccare, non come funziona la quantizzazione: la tabella dei
 * sintomi risponde a quello in una riga, e il resto sta sotto per quando serve.
 */

type Voce = {
  chiave: string;
  titolo: string;
  /** Una riga: che cosa fa, in parole di tutti i giorni. */
  cosa: string;
  /** L'immagine d'esempio, dentro public/tracciato. Assente quando l'effetto non si vede. */
  figura?: string;
  didascalia?: string;
  alzare: string;
  abbassare: string;
  /** Il dettaglio per chi lo vuole: perche' esiste, e cosa si e' misurato. */
  dietro: string;
};

const VOCI: Voce[] = [
  {
    chiave: "colori",
    titolo: "Numero di tinte",
    cosa: "Quante campiture diverse avrà il disegno finito.",
    figura: "colori.png",
    didascalia:
      "Con 5 tinte la guancia rosa e il corno beige non ci sono più: non c'era posto in tavolozza. " +
      "Con 48 il disegno è identico a quello con 24, perché le tinte in più non descrivono " +
      "nessuna zona nuova.",
    alzare: "il disegno perde un dettaglio colorato piccolo — una guancia, un riflesso, un'ombra.",
    abbassare: "vuoi un risultato più grafico, a poche campiture piatte.",
    dietro:
      "È un tetto, non una promessa. Le tinte che descrivono la frangia sfumata di un contorno, " +
      "invece di una zona vera, vengono scartate: su un disegno a sei colori puoi chiederne " +
      "quarantotto e ne escono sei. Alzarlo oltre il necessario non inventa colori, ma su " +
      "un'illustrazione molto ombreggiata spezza le ombre in bande sempre più sottili e fa " +
      "crescere il file.",
  },
  {
    chiave: "rumore",
    titolo: "Riduzione rumore",
    cosa: "Pulisce l'immagine prima di scegliere le tinte.",
    figura: "rumore.png",
    didascalia:
      "Senza pulizia i bordi sono frastagliati e restano frammenti sparsi. Con 6 il disegno è più " +
      "netto ma i dettagli minuti cominciano a sparire.",
    alzare: "i contorni escono seghettati o pieni di frammenti, e l'originale è un JPEG compresso.",
    abbassare: "il disegno ha dettagli minuti — puntini, righe sottili — che stanno sparendo.",
    dietro:
      "Dentro una campitura che il disegnatore ha riempito di un colore solo, i pixel di un JPEG " +
      "ondeggiano di qualche livello. La riduzione a poche tinte non media: sceglie. Quando due " +
      "tinte si contendono una zona, quell'ondeggiamento decide da che parte cade ogni pixel, e il " +
      "confine nasce frastagliato prima ancora che venga disegnato. Qui si passa una mediana, non " +
      "una sfocatura: restituisce sempre uno dei colori che ha davanti e mai una loro mescolanza, " +
      "quindi toglie il rumore senza spostare i bordi.",
  },
  {
    chiave: "granelli",
    titolo: "Granelli da togliere",
    cosa: "Butta via le macchie di colore più piccole di così.",
    figura: "granelli.png",
    didascalia:
      "Senza pulizia le pieghe della pinna si sbriciolano in decine di frammenti. Con 700 i " +
      "frammenti spariscono, ma se ne vanno anche le pieghe.",
    alzare: "il disegno è pieno di schegge e coriandoli che nell'originale non ci sono.",
    abbassare: "stanno sparendo dettagli veri: righe sottili, puntini, pieghe.",
    dietro:
      "È il numero che pesa di più sul peso del file, perché non toglie curve da un contorno: " +
      "toglie contorni interi. Zero non ne toglie nessuno, ed è la posizione giusta per capire " +
      "cosa sta succedendo quando un disegno esce strano.",
  },
  {
    chiave: "tolleranza",
    titolo: "Fedeltà del tracciato",
    cosa: "Di quanto la curva può discostarsi dal bordo misurato.",
    figura: "tolleranza.png",
    didascalia:
      "A 0,4 la curva ricalca anche le sbavature della quantizzazione, e la bolla esce " +
      "bitorzoluta. A 9 le curve sono poche e la forma si deforma.",
    alzare: "il file è pesante e in Illustrator i tracciati hanno troppi punti da maneggiare.",
    abbassare: "le forme si stanno deformando o gli angoli si arrotondano troppo.",
    dietro:
      "Attenzione al nome: più basso non vuol dire più fedele alla forma vera. I punti da cui si " +
      "parte stanno sugli spigoli interi dei pixel e portano già mezzo pixel di errore, quindi " +
      "sotto una certa soglia non ci si avvicina al disegno — si ricalca il rumore. È il numero " +
      "che decide quanti punti avrà il file: fra 0,4 e 2,7, sulla stessa illustrazione, si passa " +
      "da 12.000 punti a 4.800.",
  },
  {
    chiave: "lisciatura",
    titolo: "Lisciatura della mappa",
    cosa: "Quanto arrotondare il bordo fra una campitura e l'altra.",
    figura: "lisciatura.png",
    didascalia:
      "Alzandola i bordi si addolciscono, ma le righe sottili della pinna si assottigliano fino a " +
      "spezzarsi.",
    alzare: "i bordi fra le campiture sono ondulati o a gradini.",
    abbassare: "il disegno ha spigoli vivi da conservare, o linee sottili che si stanno spezzando.",
    dietro:
      "È questa, e non l'angolo di spigolo, la manopola che decide se un angolo retto resta " +
      "retto: agisce sulla mappa delle tinte, cioè prima che si disegni qualunque curva.",
  },
  {
    chiave: "spigoli",
    titolo: "Spigoli vivi: la stessa manopola",
    cosa: "Su loghi, scritte e figure geometriche la lisciatura è la cosa da guardare per prima.",
    figura: "spigoli.png",
    didascalia:
      "Una figura di prova a spigoli retti. Con la lisciatura a 5 i denti diventano pastiglie. " +
      "Per un logo o un lettering conviene 0.",
    alzare: "",
    abbassare: "",
    dietro: "",
  },
  {
    chiave: "angolo",
    titolo: "Angolo di spigolo",
    cosa: "Da quanti gradi in su una svolta del contorno è uno spigolo invece di una curva.",
    alzare: "il disegno ha angoli che dovrebbero essere morbidi e invece sono spezzati.",
    abbassare: "ci sono spigoli veri che escono arrotondati — ma prova prima la lisciatura.",
    dietro:
      "Si vede meno di quanto sembri. Sull'illustrazione delle balene, che è fatta di sole curve, " +
      "passando da 25° a 150° i punti scendono da 6.200 a 4.700 e il disegno resta lo stesso: " +
      "cambia quanto pesa il file, non quello che si guarda. Conta su disegni con spigoli veri, e " +
      "anche lì solo dopo aver sistemato la lisciatura.",
  },
  {
    chiave: "morbidezza",
    titolo: "Morbidezza dei contorni",
    cosa: "Di quanti pixel il contorno può allontanarsi dai pixel misurati mentre viene lisciato.",
    alzare: "quasi mai: è un limite di sicurezza, non una leva.",
    abbassare: "vuoi essere certo che nessuno spigolo venga smussato.",
    dietro:
      "Oggi non morde: la lisciatura sposta i punti molto meno di così, e misurando si vede che " +
      "portarla da 1 a 3,5 pixel non cambia un solo punto del risultato. Resta perché è la " +
      "garanzia che una taratura più decisa non possa mai cancellare uno spigolo del disegno.",
  },
  {
    chiave: "giri",
    titolo: "Giri di lisciatura",
    cosa: "Quante passate di lisciatura fare.",
    alzare: "quasi mai: oltre la convergenza non cambia più niente.",
    abbassare: "vuoi un contorno che segua più da vicino i pixel, a costo di qualche ondulazione.",
    dietro:
      "La lisciatura converge: dopo un certo numero di passate i punti non si spostano più, e i " +
      "giri in più costano tempo senza cambiare il disegno.",
  },
  {
    chiave: "unione",
    titolo: "Unione tinte gemelle",
    cosa: "Rimette insieme due tinte quasi identiche che si sono divise la stessa campitura.",
    alzare: "una superficie che dovrebbe essere di un colore solo esce a chiazze.",
    abbassare: "una sfumatura vera è stata appiattita in una tinta sola.",
    dietro:
      "Su un'illustrazione pulita spesso non cambia un pixel: sulle balene, fra 0 e 1300, il file " +
      "esce identico. Serve quando la riduzione a tinte spacca una campitura fra due colori che " +
      "l'occhio non distingue — succede sui manti ombreggiati — e allora si vede il disegno a " +
      "chiazze e il contorno tracciato due volte.",
  },
];

/** Sintomo → manopola. È la tabella che serve davvero quando un disegno è venuto male. */
const SINTOMI: { sintomo: string; rimedio: string }[] = [
  { sintomo: "I contorni sono seghettati, ondulati, bitorzoluti", rimedio: "Alza Riduzione rumore, poi Lisciatura." },
  { sintomo: "Il disegno è pieno di schegge e frammenti", rimedio: "Alza Granelli da togliere." },
  { sintomo: "Manca un colore che nell'originale c'è", rimedio: "Alza Numero di tinte." },
  { sintomo: "Una superficie unita esce a chiazze", rimedio: "Alza Unione tinte gemelle." },
  { sintomo: "Righe sottili spezzate o sparite", rimedio: "Abbassa Granelli e Lisciatura." },
  { sintomo: "Spigoli vivi arrotondati (loghi, scritte)", rimedio: "Abbassa Lisciatura, anche a 0." },
  { sintomo: "Il file è troppo pesante", rimedio: "Alza Fedeltà del tracciato e Granelli." },
  { sintomo: "Le forme si sono deformate", rimedio: "Abbassa Fedeltà del tracciato." },
];

export default function TracciatoView() {
  const [config, setConfig] = useState<ConfigTracciato | null>(null);

  useEffect(() => {
    api.configTracciato().then(setConfig).catch(() => setConfig(null));
  }, []);

  const predefinito = (chiave: string) => {
    if (!config) return null;
    const v = (config.valori as Record<string, number>)[chiave];
    return v === undefined ? null : v.toLocaleString("it-IT");
  };

  return (
    <div className="guide tracciato-guida">
      <div className="guide-hero">
        <h1>I numeri del tracciato a colori</h1>
        <p>
          Quando un'immagine viene vettorializzata a colori, il sistema la riduce a poche campiture
          piatte e ne ridisegna i bordi come curve. Questi nove numeri decidono <strong>quante
          campiture</strong>, <strong>quanto pulire prima</strong> e <strong>con quanta precisione
          ridisegnare</strong>. Si scelgono in due punti: nel pannello della pagina{" "}
          <em>Carica</em>, dove valgono per tutto il lotto, e nella finestra{" "}
          <em>Ritraccia vettoriali</em> del dettaglio immagine, dove valgono per quella sola.
        </p>
        <p className="muted">
          Ogni striscia qui sotto è lo stesso ritaglio della stessa illustrazione, tracciato davvero
          con quei valori. Non sono disegni esplicativi: sono il risultato.
        </p>
      </div>

      <section className="guide-sec">
        <h2>Se il disegno è venuto male</h2>
        <table className="guide-table">
          <thead>
            <tr><th>Che cosa vedi</th><th>Che cosa toccare</th></tr>
          </thead>
          <tbody>
            {SINTOMI.map((s) => (
              <tr key={s.sintomo}>
                <td>{s.sintomo}</td>
                <td>{s.rimedio}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="muted">
          Una manopola alla volta, e si guarda il risultato: agiscono una sull'altra, e spostarne
          tre insieme rende impossibile capire quale ha fatto cosa.
        </p>
      </section>

      <section className="guide-sec">
        <h2>Le misure si adattano da sole alla grandezza dell'immagine</h2>
        <p>
          I numeri espressi in pixel — riduzione rumore, granelli, fedeltà, morbidezza — si
          riferiscono a un'immagine con il lato lungo di{" "}
          <strong>{config?.riferimento.toLocaleString("it-IT") ?? "3.000"} pixel</strong>, e vengono
          riportati alla grandezza vera di ogni immagine prima di essere usati: le lunghezze in
          proporzione, i granelli col quadrato, perché sono un'area.
        </p>
        <p className="muted">
          Serve perché un raggio di due pixel non vuol dire la stessa cosa su un francobollo e su un
          manifesto. Prima che funzionasse così, la stessa illustrazione consegnata a tremila pixel
          e a seimila dava due disegni diversi: pulito il primo, pieno di granelli il secondo.
        </p>
      </section>

      {VOCI.map((v) => (
        <section className="guide-sec tracciato-voce" key={v.chiave}>
          <h2>
            {v.titolo}
            {predefinito(v.chiave) && (
              <span className="tracciato-predef">predefinito: {predefinito(v.chiave)}</span>
            )}
          </h2>
          <p className="tracciato-cosa">{v.cosa}</p>

          {v.figura && (
            <figure className="tracciato-figura">
              <img src={`tracciato/${v.figura}`} alt={`Esempi con diversi valori di ${v.titolo}`} loading="lazy" />
              {v.didascalia && <figcaption>{v.didascalia}</figcaption>}
            </figure>
          )}

          {(v.alzare || v.abbassare) && (
            <div className="tracciato-quando">
              {v.alzare && <p><strong>Alzalo se</strong> {v.alzare}</p>}
              {v.abbassare && <p><strong>Abbassalo se</strong> {v.abbassare}</p>}
            </div>
          )}

          {v.dietro && <p className="muted">{v.dietro}</p>}
        </section>
      ))}

      <section className="guide-sec">
        <h2>Che cosa non fanno</h2>
        <p>
          Non migliorano l'originale. Se una linea nell'immagine di partenza è sfocata o mangiata
          dalla compressione, nessuno di questi numeri la ricostruisce: al massimo decidono se
          tenerla com'è o buttarla via. La qualità del vettoriale si gioca per prima cosa sulla
          qualità del file che si carica.
        </p>
        <p className="muted">
          E non toccano le immagini già in libreria. Una taratura nuova vale per i caricamenti
          successivi; per rifare un disegno già consegnato c'è <em>Ritraccia vettoriali</em> nel
          dettaglio, che riscrive SVG ed EPS lasciando stare JPG e metadati.
        </p>
      </section>
    </div>
  );
}

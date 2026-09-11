import { useEffect, useState, useSyncExternalStore } from "react";
import { sessioneSharePoint } from "../sessioneSharePoint";

/**
 * Un'immagine di SharePoint.
 *
 * Prima passava dall'API: la richiesta partiva con fetch, il server scaricava il file con il
 * certificato dell'applicazione, lo ridimensionava e ne rimandava i byte. Costava banda in entrata
 * e tempo di CPU su un piano che ne ha sessanta minuti al giorno, tanto che serviva una coda a
 * quattro immagini per volta per non far cadere il server -- e con quella coda una galleria da
 * quarantotto miniature si riempiva a scaglioni.
 *
 * Ora il browser chiede il file direttamente a SharePoint, con la sessione di chi guarda. Il tag
 * <img> fa tutto da solo: niente coda, niente oggetti temporanei da liberare, e la cache del
 * browser tiene le miniature fra una pagina e l'altra invece di riscaricarle. Le richieste
 * parallele le governa il browser, che sa farlo meglio di qualsiasi coda scritta a mano.
 *
 * Il prezzo di quella scelta è che senza sessione SharePoint ogni miniatura è un riquadro vuoto,
 * e prima nulla lo spiegava. Ora ogni fallimento viene segnalato: quando ne arrivano abbastanza
 * compare l'avviso con il pulsante di accesso, e appena la sessione torna le immagini riprovano
 * invece di restare vuote fino al ricaricamento della pagina.
 */
export default function AuthImage({ src, alt }: { src: string; alt: string }) {
  const [rotta, setRotta] = useState(false);

  // La generazione cambia solo quando la sessione torna: e' il segnale per riprovare. Va letta
  // con useSyncExternalStore e non con uno stato aggiornato a mano, perche' resettare "rotta"
  // dentro l'aggiornatore di un altro stato sarebbe un effetto dentro una funzione che React
  // pretende pura -- e che ha facolta' di eseguire due volte.
  const generazione = useSyncExternalStore(sessioneSharePoint.abbonati, sessioneSharePoint.generazione);
  useEffect(() => { setRotta(false); }, [generazione]);

  if (rotta) return <div className="noimg">anteprima non disponibile</div>;

  return (
    <img
      key={generazione}
      src={src}
      alt={alt}
      // Le immagini fuori schermo non si scaricano finche' non servono: in una galleria che scorre
      // all'infinito e' la differenza fra decine di richieste e centinaia.
      loading="lazy"
      decoding="async"
      onLoad={() => sessioneSharePoint.segnalaRiuscita()}
      onError={() => { setRotta(true); sessioneSharePoint.segnalaRottura(); }}
    />
  );
}
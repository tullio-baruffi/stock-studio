import { useEffect, useState } from "react";
import { api, type CampoTracciato, type ConfigTracciato, type ParametriTracciato } from "../api";

/**
 * I numeri con cui si traccia a colori, da regolare guardando il disegno.
 *
 * ## Perché sta in un componente solo
 * Perché si sceglie negli stessi termini in due momenti diversi: prima di caricare un lotto, e
 * davanti a un'immagine già consegnata che è venuta male. Sono la stessa domanda posta due volte,
 * e tenerne due copie vorrebbe dire che prima o poi una delle due resta indietro — con l'utente che
 * sposta un cursore che dall'altra parte non esiste più.
 *
 * ## Perché i campi arrivano dal servizio
 * Minimi, massimi e predefiniti sono una proprietà dell'installazione, non di questa pagina: chi
 * cambia la taratura in `appsettings` deve vedere cambiare quello che gli viene proposto. Scritti
 * qui, resterebbero a raccontare numeri che nessuno usa più.
 *
 * ## Cosa esce
 * Solo i campi **spostati** rispetto al predefinito. Un campo non mandato lascia decidere al
 * servizio, ed è diverso da un campo mandato uguale al predefinito: il primo segue la
 * configurazione anche se cambia, il secondo la fissa a oggi.
 */
export default function PannelloTracciato({
  valore,
  onChange,
  disabilitato,
  compatto,
}: {
  valore: ParametriTracciato;
  onChange: (v: ParametriTracciato) => void;
  disabilitato?: boolean;
  /** Mostra solo i quattro numeri che contano, con «Tutti i parametri» per aprire il resto. */
  compatto?: boolean;
}) {
  const [config, setConfig] = useState<ConfigTracciato | null>(null);
  const [errore, setErrore] = useState<string | null>(null);
  const [tutti, setTutti] = useState(false);

  useEffect(() => {
    let vivo = true;
    api.configTracciato()
      .then((c) => { if (vivo) setConfig(c); })
      .catch((e: Error) => { if (vivo) setErrore(e.message); });
    return () => { vivo = false; };
  }, []);

  if (errore) return <p className="muted small">Parametri del tracciato non disponibili: {errore}</p>;
  if (!config) return <p className="muted small">Leggo i parametri del tracciato…</p>;

  const principali: (keyof ParametriTracciato)[] = ["colori", "rumore", "granelli", "tolleranza"];
  const campi = compatto && !tutti
    ? config.campi.filter((c) => principali.includes(c.nome))
    : config.campi;

  const attuale = (c: CampoTracciato) => valore[c.nome] ?? config.valori[c.nome];
  const spostati = config.campi.filter((c) => valore[c.nome] != null
    && valore[c.nome] !== config.valori[c.nome]).length;

  const imposta = (nome: keyof ParametriTracciato, v: number) => {
    // Tornare sul predefinito vuol dire **togliere** la scelta, non fissarla: così l'immagine
    // continua a seguire la configurazione se un domani cambia.
    const prossimo = { ...valore };
    if (v === config.valori[nome]) delete prossimo[nome];
    else prossimo[nome] = v;
    onChange(prossimo);
  };

  return (
    <div className="tracciato-pannello">
      <div className="tracciato-campi">
        {campi.map((c) => {
          const v = attuale(c);
          const mosso = valore[c.nome] != null && valore[c.nome] !== config.valori[c.nome];
          return (
            <div className="tracciato-campo" key={c.nome}>
              <label htmlFor={`tr-${c.nome}`}>
                <span className="tracciato-nome">{c.etichetta}</span>
                <span className={mosso ? "tracciato-valore mosso" : "tracciato-valore"}>
                  {arrotonda(v, c.passo)}
                  {mosso && (
                    <button
                      type="button"
                      className="tracciato-ripristina"
                      onClick={() => imposta(c.nome, config.valori[c.nome])}
                      disabled={disabilitato}
                      title={`Torna al predefinito (${arrotonda(config.valori[c.nome], c.passo)})`}
                    >↺</button>
                  )}
                </span>
              </label>
              <input
                id={`tr-${c.nome}`}
                type="range"
                min={c.min}
                max={c.max}
                step={c.passo}
                value={v}
                disabled={disabilitato}
                onChange={(e) => imposta(c.nome, Number(e.target.value))}
                aria-describedby={`tr-${c.nome}-spiega`}
              />
              <p className="muted small" id={`tr-${c.nome}-spiega`}>{c.spiega}</p>
            </div>
          );
        })}
      </div>

      <div className="tracciato-pie">
        {compatto && (
          <button type="button" className="btn small ghost" onClick={() => setTutti((t) => !t)}>
            {tutti ? "Solo i principali" : `Tutti i parametri (${config.campi.length})`}
          </button>
        )}
        <button
          type="button"
          className="btn small ghost"
          onClick={() => onChange({})}
          disabled={disabilitato || spostati === 0}
        >
          Riporta tutto ai predefiniti
        </button>
        <span className="muted small">
          {spostati === 0
            ? "Taratura di serie."
            : `${spostati} ${spostati === 1 ? "parametro spostato" : "parametri spostati"}.`}
          {" "}Le misure in pixel valgono su un lato lungo di {config.riferimento} px e si
          adattano da sole alla grandezza vera dell'immagine.
        </span>
      </div>
    </div>
  );
}

/** Tanti decimali quanti ne distingue il passo del cursore, e non uno di più. */
function arrotonda(v: number, passo: number): string {
  const decimali = passo < 1 ? String(passo).split(".")[1]?.length ?? 1 : 0;
  return v.toLocaleString("it-IT", { minimumFractionDigits: decimali, maximumFractionDigits: decimali });
}

import { useEffect, useState } from "react";
import { api, type CampoTracciato, type ConfigTracciato, type NomeCampoTracciato, type ParametriTracciato, type PresetTracciato } from "../api";

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
  onModalita,
  onTaraturaDiSerie,
}: {
  valore: ParametriTracciato;
  onChange: (v: ParametriTracciato) => void;
  disabilitato?: boolean;
  /** Mostra solo i quattro numeri che contano, con «Tutti i parametri» per aprire il resto. */
  compatto?: boolean;
  /**
   * Chiamata quando il preset scelto porta con sé una modalità diversa.
   *
   * Serve perché metà dei preset sono silhouette in bianco e nero: sceglierne uno e lasciare la
   * pagina su «A colori» vorrebbe dire che i due comandi si contraddicono sotto gli occhi di chi
   * guarda. Chi non ha una scelta di modalità da tenere allineata può non passarla.
   */
  onModalita?: (modalita: string) => void;
  /**
   * Chiamata quando si torna sulla «Taratura di serie», cioè sul preset vuoto.
   *
   * Serve a chi ha da proporre qualcosa di meglio della configurazione — nel ritracciamento è la
   * misura fatta sul disegno — e vuole rimetterla in campo **solo quando gliene viene chiesto**.
   * Senza questo appiglio l'unico momento per riproporla sarebbe l'apertura della finestra, cioè
   * proprio quello in cui cancellerebbe la taratura scelta l'ultima volta.
   *
   * Arriva dopo l'azzeramento: chi la riceve ha l'ultima parola sui cursori. Chi non ha niente da
   * proporre può non passarla, e «Taratura di serie» resta quello che dice di essere.
   */
  onTaraturaDiSerie?: () => void;
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

  const principali: NomeCampoTracciato[] = ["colori", "rumore", "granelli", "tolleranza"];
  const campi = compatto && !tutti
    ? config.campi.filter((c) => principali.includes(c.nome))
    : config.campi;

  const preset = config.preset ?? [];
  const scelto = preset.find((p) => p.codice === valore.preset) ?? null;

  // La base da cui si misura lo scostamento: i numeri del preset se ce n'è uno con numeri, i
  // predefiniti altrimenti. Senza questo, scegliere «3 colori» mostrerebbe «1 parametro spostato»
  // per un valore che chi guarda non ha spostato — e il tasto «ripristina» lo riporterebbe a 24.
  const base = (nome: NomeCampoTracciato): number =>
    (scelto?.valori?.[nome] as number | undefined) ?? config.valori[nome];

  const attuale = (c: CampoTracciato) => valore[c.nome] ?? base(c.nome);
  const spostati = config.campi.filter((c) => valore[c.nome] != null
    && valore[c.nome] !== base(c.nome)).length;

  const imposta = (nome: NomeCampoTracciato, v: number) => {
    // Tornare sul valore di partenza vuol dire **togliere** la scelta, non fissarla: così
    // l'immagine continua a seguire il preset (o la configurazione) se un domani cambiano.
    const prossimo = { ...valore };
    if (v === base(nome)) delete prossimo[nome];
    else prossimo[nome] = v;
    onChange(prossimo);
  };

  const scegliPreset = (codice: string) => {
    // Cambiare preset **azzera i cursori**, e non è una perdita: quei numeri erano scostamenti
    // da un'altra taratura, e riportarli su questa vorrebbe dire applicare a «3 colori» una
    // correzione pensata per «Foto ad alta fedeltà». Il grigio invece viaggia col preset, perché
    // è il preset stesso a deciderlo.
    const p = preset.find((x) => x.codice === codice);
    onChange(codice ? { preset: codice, ...(p?.valori?.grigi ? { grigi: true } : {}) } : {});
    if (p && onModalita) onModalita(p.modalita);
    // Tornare alla taratura di serie è l'unico gesto che chiede di ricalcolare: chi ha una
    // proposta migliore la rimette qui, sopra l'azzeramento appena fatto.
    if (!codice) onTaraturaDiSerie?.();
  };

  // Raggruppati per famiglia, nell'ordine in cui il servizio li manda: chi apre l'elenco senza
  // sapere cosa scegliere deve trovare per primo quello che funziona senza sapere niente.
  const famiglie: { nome: string; voci: PresetTracciato[] }[] = [];
  for (const p of preset) {
    const ultima = famiglie[famiglie.length - 1];
    if (ultima && ultima.nome === p.famiglia) ultima.voci.push(p);
    else famiglie.push({ nome: p.famiglia, voci: [p] });
  }

  return (
    <div className="tracciato-pannello">
      {preset.length > 0 && (
        <div className="tracciato-preset">
          <label htmlFor="tr-preset">
            <span className="tracciato-nome">Preset</span>
          </label>
          <select
            id="tr-preset"
            value={valore.preset ?? ""}
            disabled={disabilitato}
            onChange={(e) => scegliPreset(e.target.value)}
          >
            <option value="">Taratura di serie</option>
            {famiglie.map((f) => (
              <optgroup label={f.nome} key={f.nome}>
                {f.voci.map((p) => (
                  <option value={p.codice} key={p.codice}>{p.nome}</option>
                ))}
              </optgroup>
            ))}
          </select>
          <p className="muted small">
            {scelto ? (
              <>
                <strong>{scelto.descrizione}</strong> {scelto.quando}
              </>
            ) : (
              <>
                Tarature già pronte per un genere di disegno, così non devi accordare nove
                manopole. I nomi sono quelli di Illustrator, <strong>i numeri no</strong>: Adobe
                non li pubblica, e questi sono la nostra lettura misurata sul nostro motore.
              </>
            )}
          </p>
        </div>
      )}

      {scelto?.automatico ? (
        <p className="muted small">
          Questo preset non ha numeri da mostrare: li misura sull'immagine, una per una, al momento
          del tracciato. Per vederli e correggerli, scegline un altro.
        </p>
      ) : scelto?.modalita === "vector" ? (
        <p className="muted small">
          Questo preset traccia una <strong>silhouette in bianco e nero</strong>: il disegno lo
          decide la soglia di luminanza, non i cursori delle tinte. Su un'immagine a colori la
          appiattisce a nero pieno.
        </p>
      ) : (
      <div className="tracciato-campi">
        {campi.map((c) => {
          const v = attuale(c);
          const mosso = valore[c.nome] != null && valore[c.nome] !== base(c.nome);
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
                      onClick={() => imposta(c.nome, base(c.nome))}
                      disabled={disabilitato}
                      title={`Torna a ${arrotonda(base(c.nome), c.passo)}`}
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
      )}

      <div className="tracciato-pie">
        {compatto && !scelto?.automatico && scelto?.modalita !== "vector" && (
          <button type="button" className="btn small ghost" onClick={() => setTutti((t) => !t)}>
            {tutti ? "Solo i principali" : `Tutti i parametri (${config.campi.length})`}
          </button>
        )}
        <button
          type="button"
          className="btn small ghost"
          onClick={() => onChange({})}
          disabled={disabilitato || (spostati === 0 && !scelto)}
        >
          Riporta tutto ai predefiniti
        </button>
        <span className="muted small">
          {spostati === 0
            ? scelto ? `Preset «${scelto.nome}», non ritoccato.` : "Taratura di serie."
            : `${spostati} ${spostati === 1 ? "parametro spostato" : "parametri spostati"}`
              + (scelto ? ` rispetto al preset «${scelto.nome}».` : ".")}
          {" "}Le misure in pixel valgono su un lato lungo di {config.riferimento} px e si
          adattano da sole alla grandezza vera dell'immagine. Ogni numero è spiegato con esempi
          nella scheda <strong>Tracciato</strong>.
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

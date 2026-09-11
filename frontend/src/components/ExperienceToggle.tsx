import { esperienzaCorrente, impostaEsperienza, type Esperienza } from "../experience";

/**
 * L'interruttore fra le due vesti.
 *
 * Chiamato "Nastro" e non "new experience": il secondo è il nome che usano i prodotti quando stanno
 * per forzarti un cambio, e qui invece la scelta è reale e reversibile. Un'etichetta che dice cosa
 * fa vale più di una che dice che è nuova.
 *
 * Ricarica la pagina invece di scambiare i componenti a caldo. È un'operazione rara e volontaria, e
 * ripartire da zero evita che pezzi di stato della veste precedente sopravvivano in quella nuova.
 */
export default function ExperienceToggle() {
  const corrente = esperienzaCorrente();
  const attivo = corrente === "nastro";

  const cambia = () => {
    const prossima: Esperienza = attivo ? "classica" : "nastro";
    impostaEsperienza(prossima);
    window.location.reload();
  };

  return (
    <button
      type="button"
      className={`exp-toggle ${attivo ? "on" : ""}`}
      onClick={cambia}
      aria-pressed={attivo}
      title={
        attivo
          ? "Torna alla veste classica. Gli stessi dati, disposti come prima."
          : "Prova la veste Nastro: fondo nero, tutto monospazio, e la revisione diventa galleria e cernita da tastiera. Si torna indietro quando vuoi."
      }
    >
      <span className="exp-lamp" aria-hidden="true" />
      <span className="exp-label">Nastro</span>
    </button>
  );
}

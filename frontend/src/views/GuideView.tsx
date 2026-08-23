export default function GuideView() {
  return (
    <div className="guide">
      <div className="guide-hero">
        <h1>Come funziona Stock Vector Studio</h1>
        <p>
          Carichi un'immagine raster (JPEG/PNG), il sistema la trasforma in file vettoriali pronti per
          la vendita e prepara titolo, keyword e CSV per <strong>Adobe Stock</strong> e <strong>Freepik</strong>.
          Poi consegna i file alla tua pipeline Azure che li pubblica automaticamente.
        </p>
      </div>

      <section className="guide-sec">
        <h2>Il flusso in 6 passi</h2>
        <ol className="guide-steps">
          <li><strong>Pianifica</strong> (opzionale) — nella scheda <em>Opportunità</em> scegli un tema in finestra ottimale.</li>
          <li><strong>Scegli la modalità</strong> — <em>Vettoriale</em> (traccia in SVG/EPS) oppure <em>Immagine</em> (foto e grafiche già pronte, nessun tracciato).</li>
          <li><strong>Carica</strong> — trascini le immagini nella scheda <em>Carica</em> (anche in blocco). In vettoriale vedi subito l'anteprima del tracciato e puoi regolarne la <strong>soglia</strong>, immagine per immagine.</li>
          <li><strong>Consegna</strong> — premi <em>Consegna alla pipeline</em>: gli originali vengono depositati e messi in coda. Da qui in poi <strong>il lavoro non dipende più dall'applicazione</strong>, che puoi chiudere o spegnere.</li>
          <li><strong>Pipeline Azure</strong> <span className="kbadge k-gen">✨</span> — una Function traccia il file e lo deposita in SharePoint (<code>ImagesToClassify</code>); l'AI classifica e scrive titolo, descrizione e keyword; l'EXIF viene scritto e l'upload FTP invia ai siti stock.</li>
          <li><strong>Rivedi nel Backoffice</strong> <span className="kbadge k-det">🔒</span> — a qualche minuto di distanza le immagini compaiono nel <em>Backoffice</em>, con i metadati già scritti: lì li correggi e li mandi al marketplace.</li>
          <li><strong>Pubblicato</strong> — quando la pipeline conferma, l'immagine risulta negli stadi finali del funnel.</li>
        </ol>
        <p className="muted">
          L'elaborazione non avviene più dentro l'applicazione: prima viveva nella memoria dell'API, e un
          riavvio a metà lotto la interrompeva. Ora vive in una coda, e nessuno la può fermare per sbaglio.
        </p>
      </section>

      <section className="guide-sec">
        <h2>Cosa è deterministico, cosa è misurato, cosa è generativo</h2>
        <p>
          Non tutte le parti del sistema si comportano allo stesso modo: alcune danno sempre lo stesso
          risultato, altre dipendono da dati che cambiano nel tempo, altre ancora sono prodotte da un
          modello AI e possono variare a ogni esecuzione. Sapere quale è quale ti dice
          <strong> di cosa fidarti a occhi chiusi e cosa conviene rileggere</strong>.
        </p>
        <div className="knowledge-legend">
          <div className="legend-row">
            <span className="kbadge k-det">🔒 deterministico</span>
            <span>Algoritmo o regola scritta nel codice. Stesso input → stesso output, sempre.</span>
          </div>
          <div className="legend-row">
            <span className="kbadge k-mis">📊 misurato</span>
            <span>Dato reale letto da una fonte esterna. Non è inventato, ma cambia quando cambia la realtà.</span>
          </div>
          <div className="legend-row">
            <span className="kbadge k-gen">✨ generativo</span>
            <span>Prodotto da un modello AI. È un parere motivato, non un fatto: può variare e va riletto.</span>
          </div>
        </div>

        <table className="guide-table">
          <thead>
            <tr><th>Parte del sistema</th><th>Natura</th><th>Nota</th></tr>
          </thead>
          <tbody>
            <tr>
              <td>Vettorializzazione (soglia B/N, SVG/EPS/JPG, Illustrator)</td>
              <td><span className="kbadge k-det">🔒</span></td>
              <td>Stessa immagine e stessa soglia → file identici.</td>
            </tr>
            <tr>
              <td>Punteggio SEO e blocchi (marchi, limiti, duplicati)</td>
              <td><span className="kbadge k-det">🔒</span></td>
              <td>Regole fisse e verificabili.</td>
            </tr>
            <tr>
              <td>CSV Adobe/Freepik, bundle, nomi file</td>
              <td><span className="kbadge k-det">🔒</span></td>
              <td>Formato prevedibile.</td>
            </tr>
            <tr>
              <td>Punteggio di opportunità e finestra 90–20 giorni</td>
              <td><span className="kbadge k-det">🔒</span></td>
              <td>Formula esplicita applicata ai dati misurati.</td>
            </tr>
            <tr>
              <td>Monitoraggio, funnel SharePoint, profondità code</td>
              <td><span className="kbadge k-mis">📊</span></td>
              <td>Stato reale dei tuoi file e delle code Azure.</td>
            </tr>
            <tr>
              <td>Visite/mese, stagionalità, mese di picco, traiettoria 15 gg</td>
              <td><span className="kbadge k-mis">📊</span></td>
              <td>Visite reali delle voci Wikipedia.</td>
            </tr>
            <tr>
              <td>Trend attuali · Notizie a supporto delle previsioni</td>
              <td><span className="kbadge k-mis">📊</span></td>
              <td>Google Trends e Google News: elenchi reali, non generati.</td>
            </tr>
            <tr>
              <td>Stagionali · Catalogo temi di riserva</td>
              <td><span className="kbadge k-det">🔒</span></td>
              <td>Elenchi curati a mano: affidabili ma limitati a ciò che contengono.</td>
            </tr>
            <tr className="row-gen">
              <td><strong>Quali temi proporti oggi</strong> (scheda «Cosa creare»)</td>
              <td><span className="kbadge k-gen">✨</span></td>
              <td>Solo con motore AI attivo. Senza AI l'elenco torna al catalogo fisso.</td>
            </tr>
            <tr className="row-gen">
              <td><strong>Verdetto</strong> usabile / da adattare / non usabile</td>
              <td><span className="kbadge k-gen">✨</span></td>
              <td>Un giudizio, non una sentenza legale: per usi commerciali importanti verifica tu.</td>
            </tr>
            <tr className="row-gen">
              <td><strong>Soggetti da disegnare e prompt</strong></td>
              <td><span className="kbadge k-gen">✨</span></td>
              <td>Spunti creativi, da adattare al tuo stile.</td>
            </tr>
            <tr className="row-gen">
              <td><strong>Titolo, descrizione e tag</strong> delle immagini</td>
              <td><span className="kbadge k-gen">✨</span></td>
              <td>Prodotti dall'AI della <em>tua</em> pipeline (Logic App), non da questo sito. Rileggili prima di pubblicare.</td>
            </tr>
          </tbody>
        </table>

        <div className="guide-note">
          <p>
            <strong>La regola che tiene insieme le due cose:</strong> quando l'agente propone un tema,
            <em> sceglie lui il soggetto ma i numeri non li scrive lui</em>. Visite, stagionalità e picco
            vengono riletti dalla fonte dopo la sua risposta e sovrascritti: se il modello inventasse una
            cifra, non arriverebbe mai a schermo. Per questo un consiglio può essere opinabile, ma i dati
            che lo accompagnano sono sempre verificabili.
          </p>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Vettoriale o immagine?</h2>
        <div className="guide-note">
          <p>
            <strong>◆ Vettoriale</strong> — per silhouette e grafiche da vendere come vettori: l'immagine
            viene tracciata e ottieni <code>SVG</code> + <code>EPS</code>, più un <code>JPG</code>.
            Prima di consegnare vedi l'<strong>anteprima del tracciato</strong> e puoi regolarne la
            <strong> soglia</strong>: il disegno che vedi è esattamente quello che verrà tracciato.
          </p>
          <p>
            <strong>▣ Immagine</strong> — per foto e grafiche <em>già finite</em>: nessuna vettorializzazione,
            nessuna soglia, il file viene solo normalizzato in JPEG. Classificazione, metadati e invio al
            marketplace funzionano esattamente allo stesso modo.
          </p>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Le schede</h2>
        <div className="guide-cards">
          <div className="gcard"><div className="gcard-h">⬆ Carica</div>
            Trascini le immagini, in vettoriale regoli la <strong>soglia del tracciato</strong> guardandone
            l'anteprima, e le <strong>consegni alla pipeline</strong>. Non c'è nulla da compilare: titoli e
            keyword li scrive la pipeline.</div>
          <div className="gcard"><div className="gcard-h">📊 Monitoraggio</div>
            I <strong>lotti che hai consegnato</strong>, con la coda d'ingresso e, per ogni file,
            <strong> "Dove si trova?"</strong> fra gli stadi SharePoint. È una ricevuta locale: le immagini
            vivono nella pipeline, non qui.</div>
          <div className="gcard"><div className="gcard-h">🔀 Pipeline</div>
            <strong>Le mie immagini</strong> con "Dove si trova?", il <strong>funnel</strong> degli stadi SharePoint,
            la <strong>profondità delle code</strong> Azure e la ricerca traccia-un-file.</div>
          <div className="gcard"><div className="gcard-h">📈 Opportunità</div>
            Eventi futuri ordinati per <strong>punteggio di opportunità</strong>, con keyword pronte e link di ricerca
            Adobe/Freepik per valutare la concorrenza prima di creare.</div>
          <div className="gcard"><div className="gcard-h">🩺 Sistema</div>
            Stato dei servizi (backend, Azure Table, motore, pipeline), verifica SharePoint on-demand e i KPI.</div>
          <div className="gcard"><div className="gcard-h">⚙ Configurazione</div>
            Valori attualmente caricati dal backend, stato dei secret senza mostrarli e confronto fra tutte
            le alternative supportate, con requisiti e chiavi di configurazione.</div>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Controllo qualità e SEO <span className="kbadge k-det">🔒 deterministico</span></h2>
        <div className="guide-note">
          <p>
            Ogni immagine riceve un <strong>punteggio 0-100</strong> per <strong>Adobe Stock</strong> e <strong>Freepik</strong>,
            con i problemi da correggere per <em>evitare i rifiuti</em> e <em>emergere nella ricerca</em>.
            Sono <strong>regole fisse</strong>, non un parere: a parità di titolo e keyword il punteggio non cambia mai.
          </p>
          <ul className="guide-tips" style={{ marginTop: 0 }}>
            <li><strong>Errori (bloccano l'invio)</strong>: termini a marchio (es. nomi di brand), titolo mancante o oltre i limiti, keyword assenti o oltre 49.</li>
            <li><strong>Avvisi</strong>: titolo troppo corto, poche keyword, duplicati, parole ripetute (<em>keyword stuffing</em>, penalizza il ranking).</li>
            <li><strong>Suggerimenti</strong>: punta a <strong>25-49 keyword</strong> pertinenti — è il fattore che più incide sulla visibilità.</li>
          </ul>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Come usare le Opportunità di mercato</h2>
        <div className="guide-note">
          <p>
            La scheda <strong>🎯 Cosa creare</strong> è il punto di partenza: propone i temi vendibili
            ordinati per opportunità di oggi. <strong>Quali</strong> temi compaiono dipende dal motore
            attivo: <span className="kbadge k-gen">✨</span> con l'AI l'agente li sceglie liberamente,
            <span className="kbadge k-det">🔒</span> senza AI arrivano dal catalogo predefinito.
            Il <strong>tempismo</strong> e il <strong>punteggio</strong> sono invece sempre calcolati
            con la stessa formula sui dati misurati.
          </p>
          <ul className="guide-tips" style={{ marginTop: 0 }}>
            <li><span className="tbadge t-now">▶ Crea ora</span> sei dentro la finestra ideale (picco fra
              90 e 20 giorni) oppure il tema è caldo in questo momento. <strong>È qui che si guadagna.</strong></li>
            <li><span className="tbadge t-soon">◔ Preparati</span> il picco si avvicina: inizia a produrre
              così sei pronto quando la finestra si apre.</li>
            <li><span className="tbadge t-plan">◷ In calendario</span> fuori stagione: segnatelo, non è la priorità.</li>
            <li><span className="tbadge t-ever">∞ Sempreverde</span> vende tutto l'anno senza picchi: nessuna fretta.</li>
          </ul>
          <p>
            <span className="kbadge k-mis">📊 misurato</span> I numeri sotto ogni tema non sono stimati:
            <strong> 👁 visite/mese</strong> è l'interesse reale del pubblico (voce Wikipedia),
            <strong> 📈 picco N×</strong> quanto il tema si impenna nel mese migliore rispetto alla media,
            <strong> 🗓</strong> quando arriva quel picco, e la <strong>sparkline</strong> mostra l'andamento
            degli ultimi 15 giorni. Valgono anche quando la proposta arriva dall'agente.
          </p>
        </div>
        <div className="guide-note">
          <p>
            Quando è attivo il <strong>motore agentico</strong> l'analisi non è più una lista fissa:
            l'agente propone i temi partendo dalle evidenze del giorno (tendenze, notizie, calendario)
            e li misura prima di suggerirli. Per questo ogni risposta porta un'etichetta che dice chi
            l'ha prodotta — <span className="ebadge e-ai">✨ agente AI</span> oppure
            <span className="ebadge e-rules">⚙ regole predefinite</span>, il catalogo di riserva usato
            quando l'AI non è configurata o non risponde.
          </p>
          <p>
            Le risposte dell'agente restano in <strong>cache per 12 ore</strong> (il chip
            <span className="cachechip">⏱ generato … fa</span> indica quanto sono fresche), così la lista
            resta stabile mentre lavori e non si paga una chiamata a ogni ricarica. Il pulsante
            <strong> ↻ Rigenera</strong> chiede un'analisi completamente nuova. La cache sopravvive al
            riavvio ed è separata per stile, categoria e paese.
          </p>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Gli altri tre segnali</h2>
        <ul className="guide-tips">
          <li><strong>🔥 Trend attuali</strong> <span className="kbadge k-mis">📊</span> — cosa si cerca <em>adesso</em> (Google Trends).
            L'elenco è reale, non generato. Utile per cogliere un'onda breve, ma gran parte dei trend sono
            persone o marchi: attiva «Solo temi utilizzabili» per vedere subito ciò su cui puoi lavorare.</li>
          <li><strong>🔮 Previsti dalle notizie</strong> <span className="kbadge k-mis">📊</span><span className="kbadge k-det">🔒</span> — temi che <em>esploderanno</em>, dedotti dalla
            copertura stampa di eventi futuri. Le notizie sono reali; la selezione dei temi da sorvegliare è
            un elenco fisso, quindi copre solo gli ambiti previsti. Il vantaggio è creare mesi prima della concorrenza.</li>
          <li><strong>📅 Stagionali</strong> <span className="kbadge k-det">🔒</span><span className="kbadge k-mis">📊</span> — ricorrenze a data fissa da un calendario curato,
            con la domanda misurata sull'interesse reale.</li>
        </ul>
        <div className="guide-note">
          <p>
            La <strong>saturazione</strong> (quanti contenuti simili esistono già) non è leggibile
            automaticamente perché i siti stock bloccano l'accesso: apri il <strong>link di ricerca</strong>,
            guarda il numero di risultati e — nella scheda Stagionali — inseriscilo per affinare il punteggio.
            <em> Meno risultati = meno concorrenza = più opportunità.</em>
          </p>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Il filtro «vendibile»: da tema a prompt <span className="kbadge k-gen">✨ generativo</span></h2>
        <p>
          Non tutto ciò che è di tendenza si può vendere. Un trend può essere una persona, una squadra,
          un film o un fatto di cronaca: soggetti che Adobe e Freepik <strong>rifiutano</strong> perché
          coperti da diritti d'immagine, marchio o copyright. Quando valuti un'idea o guardi i trend attuali,
          ricevi quindi un verdetto — <strong>un parere motivato, non una verifica legale</strong>:
        </p>
        <ul className="guide-tips">
          <li><span className="vbadge v-ok">✔ Usabile</span> il soggetto è generico e disegnabile: procedi pure.</li>
          <li><span className="vbadge v-adapt">◑ Da adattare</span> il <em>nome</em> è protetto ma il tema dietro no.
            Esempio: «Baltimore Orioles» è una squadra — non citarla, ma <em>baseball</em> vende benissimo.</li>
          <li><span className="vbadge v-reject">✕ Non usabile</span> nessun soggetto sfruttabile, oppure riguarda
            cronaca e tragedie: escluso a priori dai contenuti commerciali.</li>
        </ul>
        <div className="guide-note">
          <p>
            Ogni tema porta con sé i <strong>soggetti disegnabili</strong> e i <strong>prompt già pronti</strong>
            da incollare nel generatore: sono scritti per produrre forme pulite e tracciabili (sfondo bianco,
            alto contrasto, niente testo). Scegli lo stile — silhouette, vettoriale piatto o line art — con il
            selettore <em>Prompt</em> in alto.
          </p>
          <p>
            Hai un'idea tua? Scrivila nel campo <strong>«Hai un'idea?»</strong> — anche in italiano — e ottieni
            subito verdetto e prompt. Il filtro riconosce i termini italiani, quindi «gatto che corre» o
            «zucca di halloween» funzionano senza tradurre.
          </p>
          <p className="caveat">
            ⚠ Trattandosi di un giudizio generato, per un uso commerciale rilevante — o quando il soggetto
            somiglia a un marchio, un personaggio o una persona reale — <strong>verifica sempre tu</strong>
            prima di pubblicare. Il verdetto ti fa risparmiare tempo, non ti solleva dalla responsabilità.
          </p>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Gli stadi della pipeline (SharePoint)</h2>
        <div className="guide-flow">
          <span className="gflow s0">ImagesToClassify<small>in attesa AI</small></span>
          <span className="garrow">→</span>
          <span className="gflow s1">ImagesToSend<small>post-AI, pre-invio</small></span>
          <span className="garrow">→</span>
          <span className="gflow s2">ImagesSent<small>pubblicati</small></span>
        </div>
        <p className="muted small">
          La presenza di un file in una libreria indica il suo stadio. Un accumulo su <em>In attesa AI</em>
          significa che la classificazione è indietro; su <em>pre-invio</em> che l'upload FTP è indietro.
        </p>
      </section>

      <section className="guide-sec">
        <h2>Titolo e keyword: li scrive la pipeline <span className="kbadge k-gen">✨ generativo</span></h2>
        <div className="guide-note">
          <p>
            Nella scheda <em>Carica</em> non c'è nulla da compilare, ed è voluto: titolo, descrizione e keyword
            li genera il <strong>tuo sistema</strong> (la Logic App <code>metadata-generator-001</code>) dopo che
            la pipeline ha classificato l'immagine. Un valore provvisorio scritto qui sarebbe comunque
            sovrascritto da quello.
          </p>
          <p>
            Li trovi già scritti nel <strong>Backoffice</strong>, a qualche minuto dalla consegna: è lì che si
            rileggono, si correggono e si mandano al marketplace.
          </p>
          <p className="caveat">
            ⚠ Sono prodotti da un'AI: <strong>rileggili prima di pubblicare</strong>. Il punteggio SEO
            <span className="kbadge k-det">🔒</span> intercetta gli errori formali (marchi, limiti, duplicati), ma non
            può accorgersi se una descrizione è semplicemente sbagliata rispetto all'immagine.
          </p>
        </div>
      </section>

      <section className="guide-sec">
        <h2>Consigli</h2>
        <ul className="guide-tips">
          <li>Parti da <strong>Opportunità</strong>: creare su un tema in finestra ottimale rende molto più che uno fuori stagione.</li>
          <li>Punta a un punteggio SEO <strong>≥ 80</strong> e a <strong>25+ keyword</strong> prima di mandare al marketplace dal Backoffice.</li>
          <li>Mai usare <strong>nomi di marchi</strong> in titolo o keyword: causano il rifiuto (l'app li blocca).</li>
          <li>Se il tracciato non ti convince, sposta la <strong>soglia</strong> prima di consegnare: l'anteprima mostra esattamente ciò che verrà tracciato, e dopo la consegna non si torna indietro.</li>
          <li>Lascia la soglia <strong>automatica</strong> quando l'anteprima già ti convince: la calcola la pipeline sull'originale a piena risoluzione, quindi meglio della stima mostrata qui.</li>
          <li>Dopo la consegna, in <strong>Monitoraggio</strong> premi <em>Dove si trova?</em> per vedere lo stadio di ogni file.</li>
          <li>Controlla la scheda <strong>Sistema</strong> se qualcosa non parte: ti dice quale servizio è giù.</li>
        </ul>
      </section>
    </div>
  );
}

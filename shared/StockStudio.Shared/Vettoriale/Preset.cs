using System;
using System.Collections.Generic;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Una taratura gia' pronta, con un nome e un mestiere.
    ///
    /// ## Perche' i cursori da soli non bastavano
    /// I nove numeri di <see cref="ParametriTracciato"/> sono esposti da un pezzo, ma esporre un
    /// comando non e' insegnare a usarlo: per tirarne fuori un buon disegno bisogna sapere che su
    /// un line art la lisciatura va a zero, che su una fotografia i granelli vanno alzati, e che la
    /// fedelta' e il numero di tinte vanno mossi insieme o si litigano. Chi carica un'immagine
    /// vuole dire "questo e' un logo", non accordare nove manopole.
    ///
    /// E' la stessa ragione per cui Illustrator, che quegli stessi comandi li ha tutti, sopra ci
    /// mette un elenco di preset ed e' quello che quasi tutti usano.
    ///
    /// ## Da dove vengono i numeri qui sotto -- leggere prima di fidarsi
    /// I nomi sono quelli di Illustrator, **i numeri no**. Adobe non pubblica i valori dei propri
    /// preset da nessuna parte: non stanno in un file leggibile (sono compilati dentro il plugin),
    /// e la Scripting Reference li dichiara bloccati -- `storeToPreset()` su un preset di serie
    /// risponde `false`. Cercarli nella documentazione, nei forum e nel codice pubblico non ha dato
    /// niente: si trova la prosa che descrive a cosa serve ciascuno, mai una tabella.
    ///
    /// Quel che invece e' documentato, e che rende il ricalco onesto invece che immaginato, e' la
    /// **semantica dei comandi**, che e' la stessa nostra:
    ///
    ///     Illustrator (TracingOptions)   unita'                 nostro
    ///     pathFitting     0 - 10         distanza dal bordo     Tolleranza
    ///     cornerAngle     0 - 180        gradi                  AngoloSpigolo
    ///     minArea                        pixel quadrati         Granelli
    ///     maxColors       2 - 256        numero di tinte        NumeroColori
    ///
    /// Tre di questi quattro hanno la **stessa unita' di misura** dei nostri, non una equivalente:
    /// i gradi sono gradi e i pixel quadrati sono pixel quadrati. Quindi non c'e' una conversione
    /// da indovinare -- c'e' da scegliere dove mettere ogni preset lungo scale che gia'
    /// combaciano, e quello si puo' fare misurando.
    ///
    /// Una differenza vera resta, ed e' a nostro favore: i numeri di Illustrator valgono sui pixel
    /// dell'immagine com'e', i nostri sono riferiti a <see cref="ParametriTracciato.LatoDiRiferimento"/>
    /// e <see cref="ParametriTracciato.PerImmagine"/> li riporta alla grandezza vera. Lo stesso
    /// preset su una consegna a tremila e a seimila pixel da' lo stesso disegno; in Illustrator no.
    ///
    /// ## Quindi cosa sono
    /// Sono la **nostra** lettura di quei preset, tarata sul nostro motore e misurata sulle
    /// immagini vere del portfolio. Servono a dire in una parola che genere di disegno si sta
    /// caricando. Non promettono di uscire identici a Illustrator, e l'interfaccia lo dice.
    /// </summary>
    public class Preset
    {
        /// <summary>Come si chiama nei messaggi e nelle scelte salvate. Non si traduce e non cambia.</summary>
        public string Codice { get; set; } = "";

        /// <summary>Come si legge nell'elenco.</summary>
        public string Nome { get; set; } = "";

        /// <summary>In che famiglia sta, per raggrupparli nell'elenco.</summary>
        public string Famiglia { get; set; } = "";

        /// <summary>Che disegno ne esce, in una riga.</summary>
        public string Descrizione { get; set; } = "";

        /// <summary>Su che immagini conviene, detto in modo che si riconosca la propria.</summary>
        public string QuandoUsarlo { get; set; } = "";

        /// <summary>
        /// A colori, in bianco e nero, o da decidere guardando: vedi <see cref="Modalita"/>.
        /// Un preset porta con se' anche questa scelta, perche' meta' dei preset di Illustrator
        /// sono in bianco e nero e sceglierli senza cambiare modalita' non farebbe niente.
        /// </summary>
        public string ModalitaTracciato { get; set; } = Vettoriale.Modalita.Colore;

        /// <summary>
        /// I nove numeri, oppure null quando il preset vuol dire "guarda l'immagine e decidi tu"
        /// (vedi <see cref="Disegno.Consiglia"/>).
        /// </summary>
        public ParametriTracciato? Parametri { get; set; }

        /// <summary>
        /// Per i preset in bianco e nero, dove tagliare fra bianco e nero. Null lascia la soglia
        /// automatica di Otsu, che e' quel che si vuole quasi sempre.
        /// </summary>
        public int? Soglia { get; set; }

        /// <summary>
        /// Vero solo per l'automatico: i numeri si misurano sull'immagine invece di essere scritti.
        ///
        /// ## Perche' non basta guardare se <see cref="Parametri"/> e' null
        /// Perche' e' null per due ragioni opposte. Sull'automatico manca perche' i numeri **vanno
        /// misurati**; sui preset in bianco e nero manca perche' i numeri del tracciato a colori
        /// li' **non si usano**, e misurarli sarebbe scaricare e analizzare l'immagine per buttare
        /// via il risultato un istante dopo. Due significati sotto lo stesso valore sono un difetto
        /// che aspetta: meglio dirlo.
        /// </summary>
        public bool MisuraLImmagine { get; set; }

        /// <summary>Vero quando i numeri non sono scritti qui: o si misurano, o non servono.</summary>
        public bool SenzaNumeri { get { return Parametri == null; } }

        /// <summary>
        /// Il codice del preset che si applica quando non se ne e' scelto nessuno.
        /// E' l'automatico: l'unica scelta che non puo' essere sbagliata per il disegno che arriva.
        /// </summary>
        public const string CodicePredefinito = "automatico";

        // ------------------------------------------------------------------------------------
        // L'elenco
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Tutti i preset, nell'ordine in cui conviene leggerli: prima l'automatico, poi quelli a
        /// colori dal piu' fedele al piu' sintetico, poi quelli in bianco e nero.
        ///
        /// L'ordine non e' estetico. Chi apre l'elenco senza sapere cosa scegliere deve trovare
        /// per primo quello che funziona senza sapere niente, e i due terzi di chi carica si
        /// fermano li'.
        /// </summary>
        public static IReadOnlyList<Preset> Tutti { get { return _tutti; } }

        private static readonly Preset[] _tutti = new[]
        {
            // --------------------------------------------------------------------------------
            // Il nostro, che in Illustrator non c'e'
            // --------------------------------------------------------------------------------
            new Preset
            {
                Codice = CodicePredefinito,
                Nome = "Automatico",
                Famiglia = "Consigliato",
                Descrizione = "Misura l'immagine e sceglie i nove numeri da sola, spiegando perché.",
                QuandoUsarlo = "Sempre, se non sai quale scegliere. È l'unico che guarda davvero " +
                               "il disegno invece di applicargli una taratura decisa prima.",
                ModalitaTracciato = Vettoriale.Modalita.Automatico,
                Parametri = null,
                MisuraLImmagine = true,
            },

            // --------------------------------------------------------------------------------
            // A colori -- dal piu' fedele al piu' sintetico
            // --------------------------------------------------------------------------------
            new Preset
            {
                Codice = "foto-alta-fedelta",
                Nome = "Foto ad alta fedeltà",
                Famiglia = "A colori",
                Descrizione = "Tante tinte e curve attaccate al bordo. Il file più pesante che facciamo.",
                QuandoUsarlo = "Fotografie e dipinti digitali, quando conta somigliare all'originale " +
                               "più che avere un file leggero. Attenzione: può fare parecchi megabyte.",
                ModalitaTracciato = Vettoriale.Modalita.Colore,
                Parametri = new ParametriTracciato
                {
                    // Tutte le tinte che sappiamo dare, e la passata che le rimette insieme quasi
                    // spenta: qui due verdi vicini sono due verdi, non uno sbagliato due volte.
                    NumeroColori = 64,
                    SogliaUnione = 250,
                    // Su una fotografia la grana c'e' davvero. Toglierla del tutto spianerebbe
                    // anche il dettaglio che si sta cercando di conservare.
                    RiduzioneRumore = 1,
                    RaggioLisciatura = 1,
                    LisciaturaAutomatica = false,
                    // Quasi nessun granello: su una fotografia le macchie piccole sono il dettaglio.
                    Granelli = 12,
                    Morbidezza = 2.5,
                    GiriLisciatura = 20,
                    // La fedelta' piu' stretta dell'elenco: e' questo numero, piu' delle tinte, a
                    // decidere quanti nodi ha il file.
                    Tolleranza = 0.8,
                    AngoloSpigolo = 65,
                },
            },
            new Preset
            {
                Codice = "foto-bassa-fedelta",
                Nome = "Foto a bassa fedeltà",
                Famiglia = "A colori",
                Descrizione = "La stessa fotografia ridotta a poche campiture larghe e pulite.",
                QuandoUsarlo = "Fotografie da cui vuoi un effetto illustrato, o quando l'alta " +
                               "fedeltà ha prodotto un file troppo pesante da caricare.",
                ModalitaTracciato = Vettoriale.Modalita.Colore,
                Parametri = new ParametriTracciato
                {
                    NumeroColori = 16,
                    SogliaUnione = 2200,
                    RiduzioneRumore = 3,
                    RaggioLisciatura = 1,
                    LisciaturaAutomatica = false,
                    Granelli = 700,
                    Morbidezza = 2.5,
                    GiriLisciatura = 20,
                    Tolleranza = 4.0,
                    AngoloSpigolo = 65,
                },
            },
            new Preset
            {
                Codice = "colori-3",
                Nome = "3 colori",
                Famiglia = "A colori",
                Descrizione = "Tre tinte in tutto, più il fondo. Campiture grandi e nette.",
                QuandoUsarlo = "Serigrafie, poster, adesivi: tutto quel che va stampato con pochi " +
                               "inchiostri o deve restare leggibile piccolissimo.",
                ModalitaTracciato = Vettoriale.Modalita.Colore,
                Parametri = ATinteContate(3),
            },
            new Preset
            {
                Codice = "colori-6",
                Nome = "6 colori",
                Famiglia = "A colori",
                Descrizione = "Sei tinte: abbastanza per un'illustrazione semplice con qualche ombra.",
                QuandoUsarlo = "Icone a più colori, mascotte, illustrazioni piatte. È il punto " +
                               "d'equilibrio fra pochi colori e disegno riconoscibile.",
                ModalitaTracciato = Vettoriale.Modalita.Colore,
                Parametri = ATinteContate(6),
            },
            new Preset
            {
                Codice = "colori-16",
                Nome = "16 colori",
                Famiglia = "A colori",
                Descrizione = "Sedici tinte: ombreggiature a fasce, senza sfumature continue.",
                QuandoUsarlo = "Illustrazioni ombreggiate, personaggi, paesaggi stilizzati. " +
                               "È quel che somiglia di più alla maggior parte del portfolio.",
                ModalitaTracciato = Vettoriale.Modalita.Colore,
                Parametri = ATinteContate(16),
            },
            new Preset
            {
                Codice = "line-art-colori",
                Nome = "Line art a colori",
                Famiglia = "A colori",
                Descrizione = "Tratti netti, spigoli vivi, nessuna lisciatura. Il colore resta quello.",
                QuandoUsarlo = "Disegni a contorno, set di icone, loghi: quelli dove il bordo è " +
                               "netto per scelta. Usa questo e non «Line art», che li annerisce.",
                ModalitaTracciato = Vettoriale.Modalita.Colore,
                Parametri = new ParametriTracciato
                {
                    // Poche tinte, perche' un line art ne ha davvero poche, e la passata di unione
                    // tirata su per non spaccare un tratto in due sfumature dello stesso blu.
                    NumeroColori = 8,
                    SogliaUnione = 2000,
                    // Niente pulizia e niente lisciatura: su un disegno a tinte piatte l'originale
                    // non liscia niente, e ogni sfocatura si legge come un difetto -- punte
                    // smussate, vuoti fra i tratti che si stringono. E' la misura di Disegno.
                    RiduzioneRumore = 0,
                    RaggioLisciatura = 0,
                    LisciaturaAutomatica = false,
                    // Bassi: su un line art una macchiolina e' un occhio o una narice, non rumore.
                    Granelli = 40,
                    Morbidezza = 2.5,
                    GiriLisciatura = 20,
                    // Stretta, perche' i tratti sono sottili e una curva che puo' vagare quanto il
                    // tratto lo attraversa invece di seguirlo.
                    Tolleranza = 1.0,
                    // Piu' basso del solito: un disegno geometrico ha angoli veri da tenere.
                    AngoloSpigolo = 45,
                },
            },
            new Preset
            {
                Codice = "grigi",
                Nome = "Scala di grigi",
                Famiglia = "A colori",
                Descrizione = "Toglie il colore e traccia i valori: dal nero al bianco per gradi.",
                QuandoUsarlo = "Quando vuoi la versione in grigi di un'illustrazione a colori, o " +
                               "quando il colore dell'originale non è quello che ti serve.",
                ModalitaTracciato = Vettoriale.Modalita.Colore,
                Parametri = new ParametriTracciato
                {
                    ScalaDiGrigi = true,
                    // Senza colore restano solo i valori, e ne servono di piu' per non fare
                    // fasce: la stessa scena a 16 tinte di grigio si vede a bande.
                    NumeroColori = 32,
                    SogliaUnione = 700,
                    RiduzioneRumore = 2,
                    RaggioLisciatura = 1,
                    LisciaturaAutomatica = false,
                    Granelli = 200,
                    Morbidezza = 2.5,
                    GiriLisciatura = 20,
                    Tolleranza = 2.2,
                    AngoloSpigolo = 65,
                },
            },

            // --------------------------------------------------------------------------------
            // In bianco e nero -- silhouette, una sola passata di potrace
            // --------------------------------------------------------------------------------
            new Preset
            {
                Codice = "logo-bn",
                Nome = "Logo in bianco e nero",
                Famiglia = "Bianco e nero",
                Descrizione = "Una sagoma sola, nera su trasparente, con i bordi puliti.",
                QuandoUsarlo = "Marchi e simboli già in bianco e nero. Su un disegno a colori " +
                               "lo appiattisce a nero pieno: se è colorato, scegli un preset a colori.",
                ModalitaTracciato = Vettoriale.Modalita.BiancoENero,
                Parametri = null,
                Soglia = null,   // Otsu: sceglie il taglio guardando l'istogramma
            },
            new Preset
            {
                Codice = "silhouette",
                Nome = "Silhouette",
                Famiglia = "Bianco e nero",
                Descrizione = "Solo la sagoma esterna, piena. Quel che resta di una figura controluce.",
                QuandoUsarlo = "Profili, sagome di animali, ombre. Quando del soggetto ti serve " +
                               "la forma e non il dettaglio interno.",
                ModalitaTracciato = Vettoriale.Modalita.BiancoENero,
                Parametri = null,
                // Taglio alto: prende come "disegno" anche i mezzi toni, cosi' la sagoma si chiude
                // invece di bucarsi dove il soggetto e' chiaro.
                Soglia = 170,
            },
            new Preset
            {
                Codice = "line-art-bn",
                Nome = "Line art in bianco e nero",
                Famiglia = "Bianco e nero",
                Descrizione = "I tratti scuri di un disegno, senza il colore e senza il fondo.",
                QuandoUsarlo = "Scansioni a matita o a china, fumetti, incisioni. Il fondo chiaro " +
                               "sparisce e restano i segni.",
                ModalitaTracciato = Vettoriale.Modalita.BiancoENero,
                Parametri = null,
                // Taglio basso: tiene solo quel che e' davvero scuro, cosi' la carta ingiallita e
                // le ombre della scansione non diventano inchiostro.
                Soglia = 100,
            },
            new Preset
            {
                Codice = "disegno-tecnico",
                Nome = "Disegno tecnico",
                Famiglia = "Bianco e nero",
                Descrizione = "Linee sottili tenute tutte, anche le più leggere.",
                QuandoUsarlo = "Schemi, piante, diagrammi, mappe: dove una linea persa è un " +
                               "errore e non un dettaglio in meno.",
                ModalitaTracciato = Vettoriale.Modalita.BiancoENero,
                Parametri = null,
                // Piu' alto ancora: su un disegno tecnico il tratto e' spesso grigio chiaro, e un
                // taglio severo lo cancellerebbe.
                Soglia = 200,
            },
        };

        /// <summary>
        /// La famiglia dei preset "a N tinte", che in Illustrator sono tre voci separate e da noi
        /// cambiano un numero solo.
        ///
        /// Sta in una funzione per non ripetere tre volte gli stessi otto numeri: cosi' se un
        /// giorno si scopre che su pochi colori conviene alzare i granelli, lo si scrive una volta
        /// e vale per tutte e tre. Quel che cambia davvero con le tinte e' la passata di unione --
        /// con poche tinte va tirata su, altrimenti la riduzione spacca una campitura in due
        /// sfumature della stessa tinta e una delle due sparisce.
        /// </summary>
        private static ParametriTracciato ATinteContate(int tinte)
        {
            return new ParametriTracciato
            {
                NumeroColori = tinte,
                SogliaUnione = tinte <= 3 ? 2600 : tinte <= 6 ? 2000 : 1500,
                RiduzioneRumore = 2,
                RaggioLisciatura = 1,
                LisciaturaAutomatica = false,
                Granelli = tinte <= 3 ? 400 : tinte <= 6 ? 300 : 200,
                Morbidezza = 2.5,
                GiriLisciatura = 20,
                Tolleranza = tinte <= 3 ? 3.0 : tinte <= 6 ? 2.8 : 2.7,
                AngoloSpigolo = 65,
            };
        }

        // ------------------------------------------------------------------------------------
        // Cercarli
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Il preset con quel codice, o null. Un codice sconosciuto torna null invece di
        /// ripiegare sull'automatico: chi chiama deve poter distinguere "non ha scelto" da "ha
        /// scelto una cosa che non esiste piu'", perche' nel secondo caso c'e' una scelta salvata
        /// da migrare e nasconderla vorrebbe dire tracciare in silenzio con altri numeri.
        /// </summary>
        public static Preset? Trova(string? codice)
        {
            if (string.IsNullOrWhiteSpace(codice)) return null;
            var c = codice!.Trim();
            foreach (var p in _tutti)
                if (string.Equals(p.Codice, c, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>
        /// I parametri di questo preset, pronti da usare: una copia, convalidata.
        ///
        /// La copia serve perche' l'elenco e' statico e condiviso fra tutte le richieste: chi
        /// ricevesse l'oggetto vero e ne cambiasse un numero -- per adattarlo all'immagine, che e'
        /// esattamente quel che fa <see cref="ParametriTracciato.PerImmagine"/> -- lo cambierebbe
        /// per tutti quelli che vengono dopo, fino al riavvio.
        /// </summary>
        public ParametriTracciato? ParametriCopia()
        {
            if (Parametri == null) return null;
            return Parametri.Convalidato();
        }
    }
}

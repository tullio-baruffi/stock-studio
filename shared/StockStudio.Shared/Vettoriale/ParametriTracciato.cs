using System;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// I numeri con cui si vettorializza a colori, in un posto solo.
    ///
    /// ## Perche' esiste
    /// Erano costanti sparse fra <see cref="Tavolozza"/> e <see cref="Contorni"/>, ognuna tarata
    /// sull'illustrazione che stava sul banco quel giorno. Finche' il tracciato serviva a se stesso
    /// andava bene; da quando lo si confronta con quel che esce da Illustrator non basta piu',
    /// perche' la taratura giusta **dipende dal disegno**: un logo a tinte piatte vuole contorni
    /// tirati e spigoli vivi, un'illustrazione ombreggiata vuole curve morbide e nessun granello.
    ///
    /// Raccolti qui, gli stessi numeri si possono mostrare a chi carica l'immagine e cambiare senza
    /// ricompilare. Chi non li tocca ottiene <see cref="Predefiniti"/>, che sono la taratura
    /// misurata sul confronto con Illustrator descritto in <see cref="Morbidezza"/>.
    ///
    /// ## Come leggerli
    /// Tre di questi numeri dicono **quanto rumore togliere** prima di disegnare
    /// (<see cref="RiduzioneRumore"/>, <see cref="RaggioLisciatura"/>, <see cref="Granelli"/>), due
    /// dicono **quanto lisciare il confine** una volta trovato (<see cref="Morbidezza"/>,
    /// <see cref="GiriLisciatura"/>), due dicono **come ridurlo a curve**
    /// (<see cref="Tolleranza"/>, <see cref="AngoloSpigolo"/>), due dicono **quante tinte**
    /// (<see cref="NumeroColori"/>, <see cref="SogliaUnione"/>).
    /// </summary>
    public class ParametriTracciato
    {
        /// <summary>
        /// Quante tinte al massimo. Vedi la nota lunga su VectorizeOptions.NumeroColori: e' un
        /// tetto, non una promessa, perche' le tinte che descrivono una frangia di contorno invece
        /// di una zona vengono scartate.
        /// </summary>
        public int NumeroColori { get; set; } = 24;

        /// <summary>
        /// Quanto insistere nel rimettere insieme le tinte che descrivono la stessa cosa. Zero
        /// disattiva la passata. Vedi <see cref="Tavolozza.UnionePredefinita"/>.
        /// </summary>
        public double SogliaUnione { get; set; } = Tavolozza.UnionePredefinita;

        /// <summary>
        /// Il raggio della mediana che si passa sull'immagine **prima** di ridurla a poche tinte.
        /// Zero la salta. E' riferito a <see cref="LatoDiRiferimento"/>.
        ///
        /// ## Perche' serve, e perche' una mediana e non una sfocatura
        /// Un JPEG non ha campiture piatte: ha campiture che ondeggiano di qualche livello, e lungo
        /// ogni contorno netto ha l'alone della compressione. Quando si riduce a quindici tinte,
        /// quell'ondeggiamento decide da che parte cade il pixel, e il confine fra due tinte esce
        /// frastagliato prima ancora che qualcuno provi a disegnarlo. E' il difetto che si vedeva
        /// sulle bolle: cerchi perfetti nell'originale, poligoni bitorzoluti nel tracciato.
        ///
        /// Una sfocatura lo toglierebbe, ma allargherebbe anche la frangia di ogni bordo, spostando
        /// il confine invece di stabilizzarlo. La mediana no: dove il vicinato e' di un colore solo
        /// restituisce quel colore, e un bordo netto resta netto -- toglie il rumore **senza**
        /// toccare la forma. Vedi <see cref="Rumore"/>.
        ///
        /// Misurato sull'illustrazione delle balene a 1536x2752, a parita' di tutto il resto:
        ///     raggio 0 -> 577 contorni, 10064 nodi
        ///     raggio 1 -> 561 contorni,  9398 nodi
        ///     raggio 2 -> 548 contorni,  8885 nodi
        ///     raggio 3 -> 562 contorni,  8758 nodi  (oltre il due non guadagna piu')
        /// </summary>
        public int RiduzioneRumore { get; set; } = 2;

        /// <summary>
        /// Quanto si sfoca l'appartenenza a ciascuna tinta per raddrizzare la scalinata di pixel:
        /// vedi <see cref="Tavolozza.LisciaPerTracciato"/>. Zero salta la passata.
        ///
        /// Non si scala con l'immagine, ed e' l'unico raggio che non lo fa: la scalinata da
        /// togliere e' alta un pixel del reticolo, sempre.
        ///
        /// **Uno, non due.** Era due per una taratura fatta su una sola illustrazione, ombreggiata
        /// e senza spigoli. Su un disegno a tinte piatte due si vede: misurato al massimo
        /// ingrandimento su un line art, la V fra due ciocche si arrotonda e il vuoto bianco fra
        /// loro si stringe. Uno toglie la scalinata senza toccare la forma; e dove il disegno e'
        /// fatto di tinte piatte <see cref="LisciaturaAutomatica"/> scende anche sotto.
        /// </summary>
        public int RaggioLisciatura { get; set; } = 1;

        /// <summary>
        /// Se la lisciatura debba adattarsi al disegno invece di valere quella scritta sopra.
        ///
        /// ## Perche' proprio questa
        /// Perche' e' l'unico parametro che fa un danno **visibile e opposto** sui due generi che
        /// passano di qui. Su un'illustrazione ombreggiata lisciare assomiglia a quel che
        /// l'originale gia' fa, e toglie la scalinata del reticolo; su un line art l'originale non
        /// liscia niente -- i bordi sono netti per scelta del disegnatore -- e ogni sfocatura si
        /// legge come un difetto: punte smussate, vuoti che si stringono.
        ///
        /// ## Come si riconosce il caso
        /// Non dallo spessore dei tratti, che sui due generi puo' essere identico, ma da **quanto
        /// il disegno e' fatto di tinte piatte**: si guarda di quanto ogni pixel si scosta dalla
        /// tinta a cui e' stato assegnato. Su tinte piatte quello scarto e' quasi zero, su una
        /// sfumatura no. Misurato sulle immagini vere del portfolio:
        ///     line art (2 tinte):        scarto 2,5 - 3,2
        ///     illustrazioni (7-15 tinte): scarto 4,2 - 4,3
        ///
        /// Chi muove il cursore della lisciatura spegne questa scelta: l'ha guardata lui
        /// l'immagine, e vince chi guarda.
        /// </summary>
        public bool LisciaturaAutomatica { get; set; } = true;

        /// <summary>
        /// Sotto quanti pixel una macchia e' rumore invece che un dettaglio. Zero non ne toglie
        /// nessuna. E' un'area, quindi <see cref="PerImmagine"/> la scala col **quadrato**.
        ///
        /// ## Perche' un numero e non una misura automatica
        /// Prima la misura si ricavava dalla grandezza dell'immagine, e zero voleva dire "falla
        /// tu". Era un'ambiguita' costosa da quando questo numero si mostra: sull'illustrazione di
        /// riferimento l'automatica valeva 46, quindi spostando il cursore da 0 a 10 si passava da
        /// 46 a 8 -- alzandolo si toglievano **meno** granelli. Un comando che va al contrario e'
        /// peggio di un comando che manca.
        ///
        /// Ora la scala e' monotona e zero vuol dire zero. La misura automatica non serve piu',
        /// perche' e' <see cref="PerImmagine"/> a riportare questo numero alla grandezza vera --
        /// e lo fa per tutti i parametri, non piu' solo per questo.
        ///
        /// E' il numero che pesa di piu' sul confronto con Illustrator, perche' non toglie nodi da
        /// un contorno: toglie contorni interi. Misurato sulle balene a 1536x2752:
        ///     0    -> 537 contorni, 11580 nodi
        ///     120  -> 412 contorni,  9670 nodi  (Illustrator: 334 contorni)
        ///     200  -> 313 contorni,  7564 nodi, ma le pieghe della pinna si spezzano
        ///
        /// ## Perche' e' sceso da 150 a 60
        /// Perche' il primo valore era stato scelto per **far quadrare il numero di contorni** con
        /// quello di Illustrator, e quello era il bersaglio sbagliato: Illustrator arriva a 334
        /// contorni **tenendo** dettagli che noi buttavamo per arrivare allo stesso numero.
        ///
        /// Misurato sulla balena, guardando invece il disegno:
        ///     150 -> l'occhio e' una palla nera piena, le macchioline del dorso spariscono
        ///      60 -> l'occhio ha il suo riflesso bianco e le macchioline ci sono
        /// Entrambi sono nell'originale, ed entrambi sono nel tracciato di Illustrator. Costa 467
        /// contorni invece di 603 e trenta kilobyte, che e' poco per due dettagli che si guardano.
        ///
        /// Per riferimento, il comando equivalente di Illustrator (minArea) vale 25 px quadrati
        /// nei suoi preset, misurati sui pixel veri dell'immagine. Sessanta riferiti a tremila
        /// pixel diventano cinquanta su un'immagine da 2752 e sedici su una da 1536: siamo
        /// nell'ordine di grandezza giusto, non piu' sei volte sopra.
        /// </summary>
        public int Granelli { get; set; } = 60;

        /// <summary>
        /// Di quanti pixel il contorno puo' allontanarsi dalla scalinata misurata mentre lo si
        /// liscia. E' riferito a <see cref="LatoDiRiferimento"/>.
        ///
        /// Oggi e' un limite che quasi non morde: la lisciatura di Taubin, con i pesi scelti, sposta
        /// i vertici molto meno di cosi'. Resta perche' e' la garanzia che una taratura piu' decisa
        /// non possa mai smussare uno spigolo vero -- misurato, portarlo da 1 a 3,5 pixel non cambia
        /// un nodo del risultato.
        /// </summary>
        public double Morbidezza { get; set; } = 2.5;

        /// <summary>
        /// Quanti giri di lisciatura. Serve che siano abbastanza da far arrivare ogni punto dove
        /// deve: pochi giri e il lisciatore si ferma prima di aver finito, tanti non cambiano piu'
        /// niente perche' la lisciatura converge.
        /// </summary>
        public int GiriLisciatura { get; set; } = 20;

        /// <summary>
        /// Di quanto la curva puo' scostarsi dai punti misurati, in pixel riferiti a
        /// <see cref="LatoDiRiferimento"/>.
        ///
        /// Non e' l'errore sulla forma: i punti stanno sugli spigoli interi dei pixel e portano
        /// mezzo pixel di quantizzazione, quindi una tolleranza stretta non avvicina alla forma vera
        /// -- ricalca il rumore. E' il numero che decide **quanti nodi** ha il file. Misurato sulle
        /// balene a 1536x2752, con il resto fermo:
        ///     1,2 -> 7059 nodi
        ///     1,6 -> 6487 nodi
        ///     2,5 -> 5818 nodi
        ///     3,0 -> 5584 nodi (poi si appiattisce: il numero di archi e' un pavimento)
        /// </summary>
        public double Tolleranza { get; set; } = 2.7;

        /// <summary>
        /// Oltre quanti gradi di svolta il confine ha uno spigolo vero, da tenere, invece di una
        /// curva. Piu' in alto si perdono gli angoli retti dei disegni geometrici, piu' in basso
        /// ogni ondulazione diventa uno spigolo.
        /// </summary>
        public double AngoloSpigolo { get; set; } = 65;

        /// <summary>
        /// Se togliere il colore prima di ridurre a tinte, tracciando i soli valori.
        ///
        /// ## Perche' non basta chiedere poche tinte
        /// Verrebbe da pensare che un'immagine ridotta a poche tinte diventi quasi grigia. Non
        /// succede: la riduzione sceglie le tinte **piu' presenti**, e se l'illustrazione e' blu e
        /// arancione restano blu e arancione, solo meno. Per avere i grigi bisogna toglierlo, il
        /// colore, e farlo **prima** che le tinte vengano scelte -- dopo, le tinte sono gia' quelle
        /// sbagliate e desaturarle darebbe grigi scelti male.
        ///
        /// ## Perche' la luminanza e non la media dei tre canali
        /// Perche' l'occhio non pesa uguale i tre canali: un verde pieno e un blu pieno hanno la
        /// stessa media e luminosita' molto diverse. Con la media, un disegno verde su blu
        /// diventerebbe un rettangolo grigio uniforme -- il disegno sparirebbe. Con i pesi della
        /// luminanza resta la differenza che si vedeva.
        /// </summary>
        public bool ScalaDiGrigi { get; set; }

        /// <summary>
        /// La stessa immagine senza colore, pronta per la riduzione a tinte.
        ///
        /// Torna l'array **originale** quando la scala di grigi non e' chiesta: chi chiama lo fa su
        /// ogni tracciato, e copiare qualche decina di megabyte per non fare niente si paga su
        /// tutte le immagini per servirne una.
        /// </summary>
        public static byte[] SenzaColore(byte[] rgb)
        {
            if (rgb == null) return rgb!;
            var g = new byte[rgb.Length];
            for (int i = 0; i + 2 < rgb.Length; i += 3)
            {
                // I pesi di Rec. 601, gli stessi che usa la soglia del bianco e nero: due misure
                // di luminosita' diverse dentro lo stesso programma sarebbero un difetto in attesa.
                var v = (byte)((rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) / 1000);
                g[i] = v; g[i + 1] = v; g[i + 2] = v;
            }
            return g;
        }

        /// <summary>La taratura di serie: quella misurata nel confronto con Illustrator.</summary>
        public static ParametriTracciato Predefiniti
        {
            get { return new ParametriTracciato(); }
        }

        /// <summary>
        /// Il lato lungo a cui si riferiscono le misure in pixel di questi parametri.
        ///
        /// Serve perche' un raggio di due pixel non vuol dire la stessa cosa su un francobollo e su
        /// un manifesto: sulla stessa illustrazione consegnata a tremila pixel e a seimila, la
        /// stessa taratura dava due disegni diversi -- pulito il primo, pieno di granelli il
        /// secondo. Dichiarando a quale misura i numeri si riferiscono, <see cref="PerImmagine"/>
        /// li riporta alla grandezza vera e la resa smette di dipendere da quanto era grande il
        /// file che si e' caricato.
        /// </summary>
        public const int LatoDiRiferimento = 3000;

        /// <summary>
        /// Gli stessi parametri, riportati alla grandezza dell'immagine che si sta lavorando.
        ///
        /// ## Cosa si scala e cosa no
        /// Si scala quel che misura una **cosa del disegno**: il raggio con cui si toglie il rumore,
        /// quanto e' piccola una macchia per essere un granello (che e' un'area, e quindi va col
        /// quadrato), di quanto la curva puo' scostarsi. Raddoppiando i pixel, quelle misure
        /// raddoppiano perche' raddoppia cio' che descrivono.
        ///
        /// Non si scala la lisciatura della mappa delle tinte: quella toglie la **scalinata del
        /// reticolo**, che e' alta un pixel qualunque sia la grandezza dell'immagine. Scalarla
        /// vorrebbe dire, su un'immagine grande, mangiare il disegno invece dei gradini.
        ///
        /// Non si scalano nemmeno i numeri che non sono lunghezze: quante tinte, quanti giri, quanti
        /// gradi.
        /// </summary>
        public ParametriTracciato PerImmagine(int larghezza, int altezza)
        {
            var lato = Math.Max(larghezza, altezza);
            var s = lato <= 0 ? 1.0 : (double)lato / LatoDiRiferimento;
            // Sotto un terzo e sopra il quadruplo non si sta piu' adattando una taratura: si sta
            // lavorando un'immagine per cui quella taratura non e' stata pensata.
            if (s < 0.33) s = 0.33;
            if (s > 4) s = 4;

            var p = Convalidato();
            // Un raggio che si arrotonda a zero spegnerebbe la passata su un'immagine piccola: se
            // era chiesta, resta almeno uno.
            p.RiduzioneRumore = p.RiduzioneRumore < 1 ? 0
                              : Math.Max(1, (int)Math.Round(p.RiduzioneRumore * s));
            p.Granelli = p.Granelli < 1 ? 0 : Math.Max(1, (int)Math.Round(p.Granelli * s * s));
            p.Morbidezza *= s;
            p.Tolleranza *= s;
            return p.Convalidato();
        }

        /// <summary>
        /// Rimette ogni numero dentro i limiti in cui ha senso. Si chiama su tutto quel che arriva
        /// da fuori: una tolleranza negativa o un raggio di venti pixel non sono tarature, sono
        /// modi di far girare a vuoto il calcolatore.
        /// </summary>
        public ParametriTracciato Convalidato()
        {
            return new ParametriTracciato
            {
                ScalaDiGrigi = ScalaDiGrigi,
                NumeroColori = Limita(NumeroColori, 2, 64),
                SogliaUnione = SogliaUnione < 0 ? 0 : SogliaUnione > 20000 ? 20000 : SogliaUnione,
                RiduzioneRumore = Limita(RiduzioneRumore, 0, 8),
                RaggioLisciatura = Limita(RaggioLisciatura, 0, 6),
                LisciaturaAutomatica = LisciaturaAutomatica,
                Granelli = Limita(Granelli, 0, 20000),
                Morbidezza = Limita(Morbidezza, 0, 12),
                GiriLisciatura = Limita(GiriLisciatura, 0, 60),
                Tolleranza = Limita(Tolleranza, 0.1, 12),
                AngoloSpigolo = Limita(AngoloSpigolo, 15, 170),
            };
        }

        /// <summary>Il coseno che <see cref="Contorni"/> confronta, ricavato da <see cref="AngoloSpigolo"/>.</summary>
        public double CosenoSpigolo
        {
            get { return Math.Cos(Limita(AngoloSpigolo, 15, 170) * Math.PI / 180.0); }
        }

        private static int Limita(int v, int min, int max) { return v < min ? min : v > max ? max : v; }

        private static double Limita(double v, double min, double max)
        {
            if (double.IsNaN(v)) return min;
            return v < min ? min : v > max ? max : v;
        }
    }
}

using System;
using System.Collections.Generic;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>Un colore della tavolozza, con quanti pixel gli appartengono.</summary>
    public struct Colore
    {
        public byte R { get; private set; }
        public byte G { get; private set; }
        public byte B { get; private set; }
        public int Pixel { get; set; }

        public Colore(byte r, byte g, byte b, int pixel) { R = r; G = g; B = b; Pixel = pixel; }

        public string Esadecimale
        {
            get { return "#" + R.ToString("x2") + G.ToString("x2") + B.ToString("x2"); }
        }

        /// <summary>Luminanza BT.709, la stessa misura con cui si sceglie la soglia in bianco e nero.</summary>
        public double Luminanza { get { return 0.2126 * R + 0.7152 * G + 0.0722 * B; } }
    }

    /// <summary>
    /// Riduce un'immagine a poche decine di colori, per taglio mediano.
    ///
    /// ## Perche' non basta prendere i colori piu' frequenti
    /// Una foto ha decine di migliaia di colori distinti e nessuno di essi e' frequente: prendere i
    /// primi N per conteggio darebbe N sfumature quasi identiche della stessa zona, e butterebbe via
    /// tutto il resto. Il taglio mediano invece divide ripetutamente lo spazio dei colori a meta'
    /// lungo il canale in cui l'immagine e' piu' varia, finche' non restano N scatole: ogni scatola
    /// diventa un colore, e le zone poco frequenti ma cromaticamente distanti sopravvivono.
    ///
    /// ## Perche' scritto qui e non preso da una libreria
    /// La libreria di immagini che usano web app e Function esiste solo su .NET moderno, mentre
    /// questo codice deve girare in entrambe -- una e' su .NET 8, l'altra su .NET 6 -- e passa
    /// quindi da un progetto compatibile con tutte e due. Scriverlo qui costa un centinaio di righe
    /// e in cambio la quantizzazione e' la stessa da entrambe le parti: se divergesse, la stessa
    /// immagine darebbe due file diversi a seconda di chi l'ha lavorata.
    /// </summary>
    public static class Tavolozza
    {
        /// <summary>
        /// I colori dell'immagine, dal piu' esteso al meno esteso, piu' la mappa che dice a quale
        /// colore appartiene ogni pixel.
        /// </summary>
        public class Esito
        {
            public Colore[] Colori { get; set; } = new Colore[0];
            /// <summary>Un indice nella tavolozza per ogni pixel, riga per riga.</summary>
            public byte[] Indici { get; set; } = new byte[0];
            /// <summary>
            /// Quali pixel stanno su un contorno. Serve a chiunque debba misurare il colore di una
            /// zona: la frangia di bordo e' una mescolanza, e includerla falsa ogni media.
            /// </summary>
            public bool[] SuBordo { get; set; } = new bool[0];
            /// <summary>
            /// Quali pixel appartengono davvero al disegno, cioe' non sono trasparenti.
            ///
            /// I trasparenti hanno comunque un indice -- ogni pixel ne ha uno -- ma quell'indice non
            /// vuol dire niente: non e' entrato in tavolozza e non va tracciato. Chi costruisce le
            /// maschere deve guardare qui, non fidarsi dell'indice.
            /// </summary>
            public bool[] Opaco { get; set; } = new bool[0];
        }

        /// <summary>
        /// Quanti bit per canale si tengono nel conteggio iniziale. Cinque bastano: 32 livelli per
        /// canale distinguono tutto quel che l'occhio distingue in un'illustrazione, e riducono
        /// l'istogramma da sedici milioni di caselle a trentaduemila, che si scorrono in fretta.
        /// </summary>
        private const int Bit = 5;
        private const int Livelli = 1 << Bit;      // 32
        private const int Scarto = 8 - Bit;        // 3

        /// <summary>
        /// Quanto insistere, di norma, nel rimettere insieme le tinte che descrivono la stessa cosa.
        ///
        /// E' l'unica costante di questa classe che resta **empirica**: le altre sono limiti
        /// percettivi o cambi di segno, questa e' un taglio scelto guardando i dati. Misurata su un
        /// campione di 52 illustrazioni del portfolio, dove le coppie da fondere arrivavano a 861 e
        /// quelle da tenere partivano da 1921: 1300 sta in mezzo con un fattore due di margine da
        /// entrambe le parti.
        ///
        /// Sta qui, con un nome, invece che sepolta dentro il metodo, perche' chi carica puo'
        /// scavalcarla dalla pagina di caricamento: e' il numero che potrebbe aver bisogno di una
        /// revisione su illustrazioni molto diverse da quelle su cui e' stato misurato.
        ///
        /// Provata la scala completa da 0 a 2200 su illustrazioni vere: sotto 700 non fonde niente,
        /// e **sopra 1300 il risultato non cambia piu'**. Il limite non e' questa soglia ma le due
        /// prove che vengono dopo -- il controllo sui pixel dell'originale e il tetto dei 22 livelli
        /// per canale -- quindi alzarla non rende la fusione piu' aggressiva, la lascia solo passare
        /// piu' candidate che poi vengono scartate lo stesso.
        /// </summary>
        public const double UnionePredefinita = 1300.0;

        /// <summary>
        /// L'immagine ha colori, o e' in pratica in bianco e nero?
        ///
        /// Serve a scegliere da soli la lavorazione giusta invece di chiederlo ogni volta: una
        /// silhouette tracciata a colori sprecherebbe otto passate per ottenere due tinte, e una
        /// illustrazione a colori ridotta a silhouette perderebbe tutto tranne la sagoma.
        ///
        /// Si guarda la **saturazione**, non il numero di colori distinti: una scansione in scala
        /// di grigi ha migliaia di colori distinti e nessuna tinta. Un pixel conta come colorato
        /// quando la distanza fra il suo canale piu' alto e il piu' basso supera la soglia, cioe'
        /// quando non e' un grigio.
        /// </summary>
        /// <param name="rgb">Pixel RGB, tre byte l'uno.</param>
        /// <param name="sogliaSaturazione">Scarto minimo fra canali perche' un pixel sia colorato.</param>
        /// <param name="frazioneMinima">Quanta parte dell'immagine deve essere colorata.</param>
        /// <param name="opachi">
        /// Quali pixel appartengono al disegno. I trasparenti non si guardano: su un logo ritagliato
        /// sono la maggioranza dell'immagine, e contarli come grigi farebbe scendere la frazione di
        /// colorati sotto la soglia -- un disegno a colori verrebbe lavorato come silhouette.
        /// </param>
        public static bool HaColori(byte[] rgb, int sogliaSaturazione = 24, double frazioneMinima = 0.02,
                                    bool[]? opachi = null)
        {
            if (rgb == null || rgb.Length < 3) return false;

            var pixel = rgb.Length / 3;
            var colorati = 0;
            // Su immagini grandi si campiona: la risposta e' una frazione, e una frazione si stima
            // benissimo su centomila punti presi a passo regolare invece che su dieci milioni.
            var passo = pixel > 200000 ? pixel / 100000 : 1;
            var esaminati = 0;

            for (var i = 0; i < pixel; i += passo)
            {
                if (opachi != null && !opachi[i]) continue;
                var p = i * 3;
                int r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
                var max = r > g ? (r > b ? r : b) : (g > b ? g : b);
                var min = r < g ? (r < b ? r : b) : (g < b ? g : b);
                if (max - min >= sogliaSaturazione) colorati++;
                esaminati++;
            }

            return esaminati > 0 && (double)colorati / esaminati >= frazioneMinima;
        }

        /// <summary>
        /// Riduce l'immagine a <paramref name="quanti"/> colori.
        /// I pixel arrivano come RGB, tre byte l'uno, riga per riga senza riempimenti.
        /// </summary>
        /// <param name="sogliaUnione">
        /// Quanto insistere nel rimettere insieme le tinte che descrivono la stessa cosa: vedi
        /// <see cref="FondiTinteSpezzate"/>. Zero o meno disattiva del tutto quella passata; il
        /// valore predefinito e' quello misurato sul portfolio.
        /// </param>
        /// <param name="opachi">
        /// Quali pixel appartengono al disegno, quando l'immagine ha un canale di trasparenza.
        /// Null vuol dire che sono tutti opachi, cioe' il caso di un JPEG.
        ///
        /// I trasparenti restano fuori dal conteggio dei colori: se entrassero, il fondo ritagliato
        /// di un logo si prenderebbe una tinta della tavolozza -- di solito il nero, perche' e' quel
        /// che c'e' sotto un alfa a zero -- e quella tinta verrebbe poi tracciata come un rettangolo
        /// grande quanto la tavola. Misurato su un logo con fondo trasparente: il nero occupava il
        /// 46% dell'immagine ed era la prima voce della tavolozza.
        /// </param>
        public static Esito Riduci(byte[] rgb, int larghezza, int altezza, int quanti,
                                   double sogliaUnione = UnionePredefinita, bool[]? opachi = null)
        {
            if (rgb == null) throw new ArgumentNullException("rgb");
            if (quanti < 2) quanti = 2;
            if (quanti > 64) quanti = 64;

            // Si quantizza su una copia appianata, ma i colori restituiti descrivono comunque
            // l'immagine vera: il filtro toglie l'increspatura, non sposta le tinte.
            var piano = Appiana(rgb, larghezza, altezza);

            var pixel = larghezza * altezza;

            // La tavolozza si costruisce **solo sui pixel interni alle campiture**, escludendo
            // quelli che stanno su un contorno. Vedi PixelDiBordo: e' la differenza fra una
            // tavolozza di colori e una tavolozza di mescolanze.
            var suBordo = PixelDiBordo(piano, larghezza, altezza);

            // Il confine fra disegno e trasparenza e' un contorno a tutti gli effetti: i pixel che
            // ci stanno sopra sono mescolanze fra il disegno e il nulla, e in tavolozza non vanno.
            if (opachi != null)
                for (var y = 0; y < altezza; y++)
                    for (var x = 0; x < larghezza; x++)
                    {
                        var i = y * larghezza + x;
                        if (!opachi[i]) { suBordo[i] = true; continue; }
                        if ((x > 0 && !opachi[i - 1]) || (x < larghezza - 1 && !opachi[i + 1]) ||
                            (y > 0 && !opachi[i - larghezza]) ||
                            (y < altezza - 1 && !opachi[i + larghezza])) suBordo[i] = true;
                    }

            var istogramma = new Dictionary<int, int>();
            for (var i = 0; i < pixel; i++)
            {
                if (suBordo[i]) continue;
                var p = i * 3;
                var chiave = ((piano[p] >> Scarto) << (Bit * 2))
                           | ((piano[p + 1] >> Scarto) << Bit)
                           | (piano[p + 2] >> Scarto);
                int n;
                istogramma[chiave] = istogramma.TryGetValue(chiave, out n) ? n + 1 : 1;
            }

            // Se l'immagine fosse tutta contorni non resterebbe niente su cui lavorare: si ripiega
            // su tutti i pixel, che e' il comportamento di prima. I trasparenti restano fuori
            // comunque: quello non e' un ripiego, e' un colore che non esiste.
            if (istogramma.Count < quanti * 2)
            {
                istogramma.Clear();
                for (var i = 0; i < pixel; i++)
                {
                    if (opachi != null && !opachi[i]) continue;
                    var p = i * 3;
                    var chiave = ((piano[p] >> Scarto) << (Bit * 2))
                               | ((piano[p + 1] >> Scarto) << Bit)
                               | (piano[p + 2] >> Scarto);
                    int n;
                    istogramma[chiave] = istogramma.TryGetValue(chiave, out n) ? n + 1 : 1;
                }
            }
            // Un'immagine tutta trasparente non ha colori da ridurre.
            if (istogramma.Count == 0)
                return new Esito
                {
                    Colori = new Colore[0],
                    Indici = new byte[pixel],
                    SuBordo = suBordo,
                    Opaco = opachi ?? TuttiOpachi(pixel),
                };

            var scatole = new List<Scatola> { Scatola.Da(istogramma) };

            // Si divide sempre la scatola che "sbaglia di piu'": quella che copre il volume di
            // colore piu' ampio pesato per quanti pixel contiene. Dividere la piu' popolosa
            // spaccherebbe in due il fondo uniforme lasciando insieme colori diversissimi.
            while (scatole.Count < quanti)
            {
                var scelta = -1;
                double peggio = 0;
                for (var i = 0; i < scatole.Count; i++)
                {
                    var s = scatole[i];
                    if (!s.Divisibile) continue;
                    var errore = s.Volume * Math.Sqrt(s.Pixel);
                    if (errore > peggio) { peggio = errore; scelta = i; }
                }
                if (scelta < 0) break;

                Scatola? a, b;
                if (!scatole[scelta].Dividi(out a, out b)) break;
                scatole[scelta] = a!;
                scatole.Add(b!);
            }

            // Dal piu' esteso: chi apre il file trova i gruppi nell'ordine in cui contano, e nel
            // disegno il colore di fondo finisce sotto a tutti gli altri.
            scatole.Sort(delegate (Scatola x, Scatola y) { return y.Pixel.CompareTo(x.Pixel); });

            var colori = new Colore[scatole.Count];
            for (var i = 0; i < scatole.Count; i++) colori[i] = scatole[i].Medio();

            // Il taglio mediano decide *dove* tagliare, non dove mettere il colore: le scatole
            // restano rettangoli e il colore medio di un rettangolo puo' cadere lontano da dove
            // stanno davvero i pixel. Qualche passo di Lloyd riposiziona ogni colore nel centro
            // dei pixel che gli appartengono, e questo cambia esattamente i casi che si notano --
            // misurato: un pesce arancione su fondo marrone usciva rosa slavato con otto tinte,
            // e con la stessa tavolozza raffinata esce arancione.
            Raffina(istogramma, colori);

            // Poi si ricontrolla se e' rimasto fuori qualcosa di importante. Serve perche' sia il
            // taglio mediano sia Lloyd minimizzano l'errore *totale*, e un oggetto piccolo non
            // sposta un totale: un pesce arancione di ottocento pixel su un milione e mezzo vale
            // lo 0,06% dell'errore, quindi nessuno dei due gli assegna mai una tinta, nemmeno
            // dandogliene sedici -- misurato sull'immagine vera, dove le tinte in piu' finivano
            // tutte su marroni intermedi mentre il pesce restava beige.
            SalvaIColoriDimenticati(istogramma, colori, pixel);

            // Le tinte quasi identiche fra loro si fondono. Non e' un'ottimizzazione: due tinte
            // che nessuno distingue creano dentro una campitura piatta un confine che non esiste,
            // potrace lo traccia, e ne esce una banda fantasma -- misurato, una banda larga
            // sull'1,8% dell'immagine dentro il fondo bianco. Meglio poche tinte pulite che molte
            // torbide.
            colori = UnisciGemelle(istogramma, colori);

            // La fusione libera posti, e i posti liberati vanno spesi: chi ha chiesto sedici tinte
            // ne vuole sedici, e i colori rimasti fuori sono proprio quelli piccoli e saturi che
            // l'occhio cerca per primo -- le guance, il miele, le bollicine.
            // Provato anche a rioccupare i posti che la fusione libera, dandoli ai colori peggio
            // rappresentati: SCARTATO. Qualunque criterio di scelta -- la distanza massima, la
            // massa per la distanza -- premia le frange di antialiasing, che sono lontane da tutto
            // proprio perche' sono mescolanze. Misurato: i posti finivano a marroni di transizione
            // con bordo/area 0,45 e il file raddoppiava (231 -> 479 KB) senza che la guancia
            // entrasse in tavolozza. La leva giusta e' il numero di tinte chieste, non un
            // riempimento d'ufficio.

            // La mappa da casella dell'istogramma a indice di tavolozza si costruisce una volta
            // sola: assegnare ogni pixel cercando il colore piu' vicino fra N costerebbe
            // pixel x N confronti, qui sono trentaduemila caselle e basta.
            var perChiave = new Dictionary<int, byte>(istogramma.Count);
            foreach (var chiave in istogramma.Keys)
            {
                var r = Centro((chiave >> (Bit * 2)) & (Livelli - 1));
                var g = Centro((chiave >> Bit) & (Livelli - 1));
                var b = Centro(chiave & (Livelli - 1));

                byte migliore = 0;
                var minimo = double.MaxValue;
                for (byte i = 0; i < colori.Length; i++)
                {
                    double dr = colori[i].R - r, dg = colori[i].G - g, db = colori[i].B - b;
                    var d = dr * dr + dg * dg + db * db;
                    if (d < minimo) { minimo = d; migliore = i; }
                }
                perChiave[chiave] = migliore;
            }

            var indici = new byte[pixel];
            for (var i = 0; i < pixel; i++)
            {
                var p = i * 3;
                var chiave = ((piano[p] >> Scarto) << (Bit * 2))
                           | ((piano[p + 1] >> Scarto) << Bit)
                           | (piano[p + 2] >> Scarto);
                indici[i] = Assegna(perChiave, colori, chiave);
            }

            Spolvera(indici, larghezza, altezza);
            Liscia(indici, larghezza, altezza);
            // Prima si rimettono insieme le tinte spezzate, poi si tolgono le frange: una frangia
            // riconosciuta come tale va tolta, ma una linea spaccata in due va ricucita, non
            // dimezzata.
            colori = FondiTinteSpezzate(colori, rgb, indici, larghezza, altezza, pixel, sogliaUnione);
            colori = TogliNastriIntermedi(colori, indici, opachi, larghezza, altezza, pixel);

            var opaco = opachi ?? TuttiOpachi(pixel);

            // L'ultima parola sul colore la danno i pixel veri, non le caselle dell'istogramma.
            AffinaSuiPixelVeri(colori, indici, rgb, suBordo, opaco, pixel);

            // I pixel trasparenti non si contano: non appartengono a nessuna tinta, e sommarli
            // gonfierebbe proprio la tinta che il trasparente ha per caso sotto di se'.
            var conteggi = new int[colori.Length];
            for (var i = 0; i < pixel; i++) if (opaco[i]) conteggi[indici[i]]++;
            for (var i = 0; i < colori.Length; i++) colori[i].Pixel = conteggi[i];

            return new Esito { Colori = colori, Indici = indici, SuBordo = suBordo, Opaco = opaco };
        }

        /// <summary>
        /// L'ultima parola sul colore di ogni tinta: la media dei pixel veri che le sono toccati.
        ///
        /// ## Perche' serve, dopo tutto il resto
        /// Fin qui i colori vengono dalle caselle dell'istogramma, che raggruppano otto livelli per
        /// canale: qualunque cosa si faccia, il risultato e' arrotondato alla casella. Il taglio
        /// mediano e Lloyd scelgono **quali** tinte, e lo fanno bene; ma il valore preciso di
        /// ciascuna resta quello di una scatola, non quello dei pixel.
        ///
        /// Qui la scelta e' gia' fatta e ogni pixel sa a quale tinta appartiene: a quel punto il
        /// colore giusto non e' una stima, e' una media che si puo' calcolare esattamente.
        ///
        /// ## Perche' i pixel di frangia restano fuori
        /// Sono mescolanze fra due tinte (vedi PixelDiBordo). Contarli tirerebbe ogni campitura
        /// verso il colore di chi le sta accanto -- e siccome quasi ogni campitura confina con la
        /// linea nera del disegno, il risultato sarebbe che tutto si scurisce un po'. E' esattamente
        /// il difetto che si voleva togliere.
        /// </summary>
        private static void AffinaSuiPixelVeri(Colore[] colori, byte[] indici, byte[] rgb,
                                               bool[] suBordo, bool[] opaco, int pixel)
        {
            var n = colori.Length;
            if (n == 0) return;

            var sr = new long[n];
            var sg = new long[n];
            var sb = new long[n];
            var quanti = new long[n];

            for (var i = 0; i < pixel; i++)
            {
                if (!opaco[i] || suBordo[i]) continue;
                int t = indici[i];
                if (t >= n) continue;
                var p = i * 3;
                sr[t] += rgb[p];
                sg[t] += rgb[p + 1];
                sb[t] += rgb[p + 2];
                quanti[t]++;
            }

            // Sotto una manciata di pixel la media e' un'opinione, non una misura: una tinta con
            // quattro pixel interni si sposterebbe dove capita. Meglio lasciarla dove il taglio
            // mediano l'aveva messa.
            const int Minimo = 24;

            for (var t = 0; t < n; t++)
            {
                if (quanti[t] < Minimo) continue;
                colori[t] = new Colore(
                    (byte)((sr[t] + quanti[t] / 2) / quanti[t]),
                    (byte)((sg[t] + quanti[t] / 2) / quanti[t]),
                    (byte)((sb[t] + quanti[t] / 2) / quanti[t]),
                    colori[t].Pixel);
            }
        }

        private static bool[] TuttiOpachi(int pixel)
        {
            var v = new bool[pixel];
            for (var i = 0; i < pixel; i++) v[i] = true;
            return v;
        }

        /// <summary>
        /// Fonde due tinte che sono le due meta' di una stessa cosa.
        ///
        /// ## I due difetti che risolve, che sono lo stesso difetto
        /// 1. **Contorno tracciato due volte.** Una linea scura con una leggera variazione di tono
        ///    viene spaccata in due tinte quasi identiche, e il confine fra le due corre *dentro* la
        ///    linea: potrace lo traccia, e ne escono l'occhio sbriciolato e le striature lungo i
        ///    profili. Misurato: due marroni a distanza 459, e un file che fondendoli scende da
        ///    461 a 232 KB -- meta' del peso era il contorno disegnato due volte.
        /// 2. **Manto a chiazze.** Una campitura ampia e appena ombreggiata viene divisa fra due
        ///    marroni indistinguibili, e il confine fra loro serpeggia nel rumore del JPEG: ne esce
        ///    un mimetico militare al posto di un manto liscio. Misurato sul manto degli orsi: due
        ///    marroni a distanza 612, 450.750 e 227.892 pixel.
        /// Sono lo stesso difetto -- una regione sola divisa in due -- visto su forme diverse.
        ///
        /// ## Perche' non basta la distanza fra colori
        /// La fusione che avviene prima dell'assegnazione lavora sull'istogramma e conosce solo il
        /// colore. Ma le coppie da fondere stanno a 459, 612 e 620, mentre quelle da **non** fondere
        /// -- i marroni che danno volume ai corpi, l'azzurro chiaro del riflesso sull'acqua -- stanno
        /// a 730, 741, 981 e 1109. Gli intervalli si sovrappongono: nessuna soglia sul colore separa
        /// i due casi, e alzarla quanto basta per la prima prende anche la seconda. E' gia' successo,
        /// e gli orsi erano tornati piatti.
        ///
        /// ## La misura che li separa
        /// Si guarda **quanto le due tinte si toccano fra loro**, cosa che l'istogramma non sa: la
        /// quota di perimetro che ciascuna spende sull'altra. Due meta' della stessa regione si
        /// toccano quasi solo fra loro; due zone diverse che si sfiorano, no.
        ///
        /// Le due misure vanno prese **insieme**, non come due soglie separate: si fonde quando la
        /// differenza di colore e' piccola *in rapporto* a quanto le due tinte sono la stessa
        /// regione. Piu' si toccano, piu' differenza si perdona.
        ///
        ///     coppia                       distanza  quota   rapporto   esito
        ///     contorno spezzato  (orsi)        459    57%        805    fondere
        ///     manto a chiazze    (manti)       612    77%        795    fondere
        ///     manto a chiazze    (five)        620    72%        861    fondere
        ///     riflesso acqua     (vector)      730    38%       1921    tenere
        ///     ombra del corpo    (vector)      741    17%       4359    tenere
        ///     volume del corpo   (orsi)       1109    44%       2520    tenere
        ///
        /// Fra 861 e 1921 c'e' un fattore due: la soglia a 1300 sta in mezzo con margine da
        /// entrambe le parti, invece che sul filo. Rilevato su quattro illustrazioni vere, non su
        /// casi costruiti -- tarare su casi fabbricati e' l'errore gia' pagato due volte qui.
        ///
        /// Una versione precedente chiedeva invece che **entrambe** le tinte fossero nastri sottili.
        /// Prendeva il contorno spezzato ma non il manto a chiazze, che di nastro non ha niente:
        /// le sue due meta' hanno appena il 9% e il 12% dei pixel su un confine.
        /// </summary>
        /// ## La seconda misura, indipendente: il confine esiste davvero?
        /// La quota da sola non basta, e si vede solo guardando molte immagini. Su un campione di
        /// 52 illustrazioni vere del portfolio la regola della quota fondeva 59 coppie, e **venti
        /// di quelle stavano su un bordo vero**: le avrebbe cancellate. Il caso peggiore era una
        /// coppia di rossi scuri che condividevano il 93% del perimetro -- quindi "una regione
        /// sola" secondo la quota -- mentre l'immagine di partenza li' faceva un salto quattro
        /// volte piu' grande di quello dichiarato dalla tavolozza.
        ///
        /// Si misura percio' anche **quanto salta davvero l'originale attraverso quel confine**,
        /// campionando qualche pixel al di qua e al di la' per scavalcare l'antialiasing. Poi si
        /// confronta col salto che la tavolozza dichiara:
        ///
        ///     salto vero / salto dichiarato  <  1   la tavolozza esagera: il confine e' inventato
        ///     salto vero / salto dichiarato  >= 1   la tavolozza sottostima: il bordo c'e'
        ///
        /// Il valore 1 non e' una soglia tarata: e' il punto in cui il rapporto cambia segno. Sotto,
        /// il disegno mostra un gradino che nell'immagine non c'e'; sopra, il gradino c'e' ed e'
        /// perfino piu' marcato di come viene reso. Misurato sul campione: la stragrande maggioranza
        /// dei confini sta intorno a 1,0 -- la tavolozza fa il suo mestiere -- e la coda vicino a
        /// zero sono i confini inventati. Le due coppie di manto a chiazze note stanno a 0,42 e
        /// 0,85; le coppie di ombreggiatura vera a 1,21 e 1,50.
        ///
        /// Le due misure sono indipendenti -- una guarda la forma, l'altra i pixel di partenza --
        /// e servono entrambe: la prima trova le regioni spaccate, la seconda scarta quelle in cui
        /// la spaccatura corrisponde a qualcosa di vero.
        ///
        /// Quando i campioni puliti sono troppo pochi non si fonde: significa che la struttura e'
        /// piu' sottile della distanza di campionamento, e su un dubbio e' meglio lasciare com'e'.
        /// </summary>
        private static Colore[] FondiTinteSpezzate(Colore[] colori, byte[] rgb, byte[] indici,
                                                   int larghezza, int altezza, int pixel,
                                                   double sogliaUnione)
        {
            if (colori.Length < 3 || larghezza < 3 || altezza < 3) return colori;
            // Zero o meno significa "non unire niente": serve a chi vuole vedere la tavolozza grezza,
            // e a poter escludere questa passata quando si indaga su un difetto.
            if (sogliaUnione <= 0) return colori;

            // Distanza di colore ammessa per ogni punto di perimetro condiviso. Vedi la tabella
            // sopra: sotto questo valore ci sono solo regioni spaccate in due, sopra solo zone
            // distinte. Arriva da fuori perche' e' l'unica costante empirica di questa classe.
            var sogliaRapporto = sogliaUnione;

            // Oltre questa distanza sono due colori diversi e non si discute, per quanto si tocchino:
            // un tetto che impedisce al rapporto di giustificare da solo una fusione vistosa.
            const double MaiOltre = 22.0 * 22 * 3;

            // Quanto ci si allontana dal confine per misurare il salto vero: tre pixel bastano a
            // scavalcare l'antialiasing e la frangia del JPEG senza uscire da campiture normali.
            const int Scostamento = 3;

            // Sotto questo numero di campioni puliti la misura non e' attendibile e non si fonde.
            const int CampioniMinimi = 30;

            // Tre giri: fondere due meta' puo' rendere evidente una terza scheggia della stessa
            // struttura. Oltre il terzo non e' mai cambiato niente, e un ciclo senza tetto su una
            // tavolozza degenere non finirebbe.
            for (var giro = 0; giro < 3; giro++)
            {
                var n = colori.Length;
                var area = new int[n];
                var condiviso = new int[n * n];
                var salto = new double[n * n];
                var campioni = new int[n * n];

                for (var y = 0; y < altezza; y++)
                {
                    var riga = y * larghezza;
                    for (var x = 0; x < larghezza; x++)
                    {
                        var i = riga + x;
                        int a = indici[i];
                        area[a]++;

                        if (x < larghezza - 1)
                        {
                            int b = indici[i + 1];
                            if (b != a)
                            {
                                condiviso[a * n + b]++; condiviso[b * n + a]++;
                                // Si campiona solo se i due punti lontani appartengono ancora alle
                                // due tinte: altrimenti si starebbe misurando un altro confine.
                                int sx = x - Scostamento, dx = x + 1 + Scostamento;
                                if (sx >= 0 && dx < larghezza
                                    && indici[riga + sx] == a && indici[riga + dx] == b)
                                {
                                    var v = ScartoQuadrato(rgb, riga + sx, riga + dx);
                                    salto[a * n + b] += v; salto[b * n + a] += v;
                                    campioni[a * n + b]++; campioni[b * n + a]++;
                                }
                            }
                        }
                        if (y < altezza - 1)
                        {
                            int b = indici[i + larghezza];
                            if (b != a)
                            {
                                condiviso[a * n + b]++; condiviso[b * n + a]++;
                                int sy = y - Scostamento, dy = y + 1 + Scostamento;
                                if (sy >= 0 && dy < altezza
                                    && indici[sy * larghezza + x] == a && indici[dy * larghezza + x] == b)
                                {
                                    var v = ScartoQuadrato(rgb, sy * larghezza + x, dy * larghezza + x);
                                    salto[a * n + b] += v; salto[b * n + a] += v;
                                    campioni[a * n + b]++; campioni[b * n + a]++;
                                }
                            }
                        }
                    }
                }

                var perimetro = new int[n];
                for (var a = 0; a < n; a++)
                    for (var b = 0; b < n; b++) perimetro[a] += condiviso[a * n + b];

                // Si fonde la coppia col rapporto piu' basso, non la prima che capita: e' quella di
                // cui si e' piu' sicuri, e i giri successivi rivalutano il resto sulla mappa nuova.
                var tieni = -1;
                var togli = -1;
                var migliore = sogliaRapporto;

                for (var a = 0; a < n; a++)
                {
                    if (area[a] == 0 || perimetro[a] == 0) continue;
                    for (var b = a + 1; b < n; b++)
                    {
                        if (area[b] == 0 || perimetro[b] == 0) continue;

                        var c = condiviso[a * n + b];
                        if (c == 0) continue;

                        double dr = colori[a].R - colori[b].R, dg = colori[a].G - colori[b].G,
                               db = colori[a].B - colori[b].B;
                        var distanza = dr * dr + dg * dg + db * db;
                        if (distanza >= MaiOltre) continue;

                        // Prima prova, sulla forma: quanto le due tinte si toccano fra loro. Si usa
                        // la quota piu' alta delle due perche' una meta' puo' essere molto piu'
                        // grande dell'altra: la piccola e' quasi tutta a contatto, la grande no, e
                        // sarebbe la grande a nascondere il difetto.
                        var quota = (double)c / Math.Min(perimetro[a], perimetro[b]);
                        var rapporto = distanza / quota;
                        if (rapporto >= migliore) continue;

                        // Seconda prova, sui pixel di partenza: quel confine esiste davvero? Senza
                        // questa, su un campione di 52 illustrazioni venti confini veri su
                        // cinquantanove sarebbero stati cancellati.
                        if (campioni[a * n + b] < CampioniMinimi) continue;
                        if (salto[a * n + b] / campioni[a * n + b] >= distanza) continue;

                        migliore = rapporto;
                        // Sopravvive la piu' estesa; il fondo (indice 0) non si tocca mai.
                        if (b == 0) { tieni = b; togli = a; }
                        else if (a == 0) { tieni = a; togli = b; }
                        else if (area[a] >= area[b]) { tieni = a; togli = b; }
                        else { tieni = b; togli = a; }
                    }
                }

                if (togli < 0) return colori;

                // Il colore che resta e' la media pesata delle due: tenere quello della piu' estesa
                // sposterebbe il contorno verso uno dei due toni invece di metterlo in mezzo.
                double pa = area[tieni], pb = area[togli], tot = pa + pb;
                colori[tieni] = new Colore(
                    Arrotonda((colori[tieni].R * pa + colori[togli].R * pb) / tot),
                    Arrotonda((colori[tieni].G * pa + colori[togli].G * pb) / tot),
                    Arrotonda((colori[tieni].B * pa + colori[togli].B * pb) / tot),
                    (int)tot);

                var nuova = new List<Colore>();
                var mappa = new byte[n];
                for (var i = 0; i < n; i++)
                {
                    if (i == togli) continue;
                    mappa[i] = (byte)nuova.Count;
                    nuova.Add(colori[i]);
                }
                mappa[togli] = mappa[tieni];
                for (var i = 0; i < pixel; i++) indici[i] = mappa[indici[i]];
                colori = nuova.ToArray();
            }

            return colori;
        }

        /// <summary>
        /// Elimina le tinte che descrivono una frangia di confine invece di una zona.
        ///
        /// Escludere i pixel di contorno dalla tavolozza toglie quasi tutti gli aloni, ma non
        /// quelli fra due campiture chiare che si toccano senza una linea scura in mezzo: li' la
        /// transizione e' larga e dolce, quindi non supera la soglia di contorno e una tinta la
        /// descrive. Ne esce una banda pallida attorno alla forma -- misurato: lo 0,1%
        /// dell'immagine, sottile ma visibile.
        ///
        /// ## Le due prove, che servono entrambe
        /// 1. **Geometria**: molti pixel della zona stanno su un confine. Una zona vera e'
        ///    compatta; una frangia e' un nastro e il rapporto sale.
        /// 2. **Colore**: la tinta giace sul segmento fra due altre, cioe' e' una loro mescolanza.
        ///
        /// Insieme escludono i due errori simmetrici: un dettaglio sottile ma vero (un baffo) e' un
        /// nastro ma non e' una mescolanza; un marrone intermedio e' una mescolanza ma forma una
        /// zona compatta. Verificato su entrambi i casi.
        ///
        /// ## Due strade, perche' la prova geometrica non e' sempre forte
        /// Sopra 0,55 il nastro e' evidente e basta la prova cromatica generale: la tinta e' una
        /// mescolanza di **due qualsiasi** delle altre.
        ///
        /// Fra 0,25 e 0,55 no. Li' cadono anche le bande di ombreggiatura di un oggetto piccolo,
        /// che sono strisce sottili quanto una frangia, e la prova cromatica generale non le
        /// distingue affatto: un grigio neutro giace sul segmento fra il nero e il bianco **per
        /// costruzione**, quindi sembra sempre una mescolanza. Misurato: abbassando qui la soglia
        /// senza altro, i grigi della carrozzeria di un drone sparivano assorbiti dal verde del
        /// fondo -- il disegno usciva col drone verde.
        ///
        /// Serve quindi una prova diversa, di **vicinato**: una frangia e' la cucitura fra due
        /// campiture molto piu' grandi di lei, e quelle due le stanno intorno. Una banda di
        /// ombreggiatura invece sta fra bande sue pari. Il rapporto separa le due famiglie con un
        /// margine larghissimo -- misurato sull'illustrazione da 8 megapixel: le due vere frange
        /// avevano vicini 40 e 65 volte piu' grandi, mentre ogni banda di ombreggiatura stava fra
        /// 0,4 e 2,5. Il taglio a 8 sta in mezzo con un fattore quattro da entrambe le parti, e su
        /// un logo a tinte piatte, dove non c'e' una sola frangia, il valore piu' alto e' 1,8.
        ///
        /// ## Perche' si guarda l'area tolta e non quante tinte si tolgono
        /// La versione precedente si fermava quando i candidati erano piu' della meta' della
        /// tavolozza, per non smontarla. Ma e' esattamente il caso di chi chiede troppe tinte: le
        /// frange **sono** la maggioranza, e la guardia disattivava la passata proprio quando
        /// serviva. Misurato: chiedendone 48 su un disegno che ne ha sei, quindici candidati su
        /// ventidue venivano riconosciuti e poi rimessi dentro tutti -- 75 KB invece di 8.
        ///
        /// Quel che va davvero protetto non e' il numero di voci ma i pixel: una frangia e' sottile
        /// per definizione, quindi i candidati messi insieme coprono una fetta minima. Misurato:
        /// il 2,6% dell'immagine in quel caso. Se coprissero molto di piu' vorrebbe dire che il
        /// criterio non descrive questa immagine, e allora e' meglio non toccare niente: il limite
        /// sta al 15%, un ordine di grandezza sopra il misurato.
        ///
        /// ## Perche' si ripete
        /// Togliere una frangia riaccosta le due zone che separava, e al nuovo confine puo'
        /// affiorare la frangia successiva. Il ciclo converge da solo perche' ogni giro lascia una
        /// tavolozza piu' rada, e in una tavolozza rada e' piu' difficile stare in mezzo a due
        /// tinte: la prova cromatica diventa via via piu' severa invece che piu' permissiva.
        ///
        /// I pixel della frangia passano alla piu' vicina fra le due tinte che la circondano, che
        /// e' quel che rende netto il confine. La tavolozza si accorcia: nessuna ricostruzione,
        /// nessuna rimessa a fuoco, quindi non si possono creare nuovi problemi.
        /// </summary>
        private static Colore[] TogliNastriIntermedi(Colore[] colori, byte[] indici, bool[]? opachi,
                                                     int larghezza, int altezza, int pixel)
        {
            if (larghezza < 3 || altezza < 3) return colori;

            // Le statistiche vanno fatte sui soli pixel del disegno: contando anche i trasparenti,
            // una zona affacciata sul ritaglio sembrerebbe piu' estesa e meno bordata di quel che e'.
            var quantiPixel = pixel;
            if (opachi != null)
            {
                quantiPixel = 0;
                for (var i = 0; i < pixel; i++) if (opachi[i]) quantiPixel++;
                if (quantiPixel == 0) return colori;
            }

            // Piu' giri di questi non ne servono: ogni giro accorcia la tavolozza, e senza un
            // limite un criterio sbagliato potrebbe continuare a mordere.
            for (var giro = 0; giro < 4; giro++)
            {
                if (colori.Length < 4) break;
                var accorciata = UnGiroDiNastri(colori, indici, opachi, larghezza, altezza, pixel, quantiPixel);
                if (accorciata == null) break;
                colori = accorciata;
            }

            return colori;
        }

        /// <summary>
        /// Un solo giro di <see cref="TogliNastriIntermedi"/>. Restituisce null quando non c'e'
        /// niente da togliere, cosi' chi chiama sa che ha finito.
        /// </summary>
        private static Colore[]? UnGiroDiNastri(Colore[] colori, byte[] indici, bool[]? opachi,
                                                int larghezza, int altezza, int pixel, int quantiPixel)
        {
            var quante = colori.Length;
            var area = new int[quante];
            var bordo = new int[quante];
            // Quanti contatti di confine ha ogni tinta con ogni altra: e' la prova di vicinato.
            var vicini = new int[quante * quante];
            for (var y = 1; y < altezza - 1; y++)
            {
                var riga = y * larghezza;
                for (var x = 1; x < larghezza - 1; x++)
                {
                    var i = riga + x;
                    if (opachi != null && !opachi[i]) continue;
                    var v = indici[i];
                    area[v]++;
                    var tocca = false;
                    var b = v * quante;
                    Vicino(indici, opachi, i - 1, v, b, vicini, ref tocca);
                    Vicino(indici, opachi, i + 1, v, b, vicini, ref tocca);
                    Vicino(indici, opachi, i - larghezza, v, b, vicini, ref tocca);
                    Vicino(indici, opachi, i + larghezza, v, b, vicini, ref tocca);
                    if (tocca) bordo[v]++;
                }
            }

            var daTogliere = new List<int>();
            long areaTolta = 0;
            for (var i = 0; i < quante; i++)
            {
                if (area[i] == 0) continue;
                // Il fondo non si tocca: e' la sola tinta di cui si sa che non e' una frangia.
                if (i == 0) continue;
                // Sopra questa quota si tratta di una campitura vera con un contorno lungo.
                if (area[i] > quantiPixel / 25) continue;
                var quantoNastro = (double)bordo[i] / area[i];
                if (quantoNastro < 0.25) continue;

                if (quantoNastro >= 0.55)
                {
                    // Quanta prova cromatica serve dipende da quanto e' forte quella geometrica:
                    // una zona in cui **ogni** pixel sta su un confine e' un nastro senza dubbio,
                    // e le si puo' chiedere meno somiglianza con una mescolanza. Serve perche' una
                    // frangia fra tre zone -- contorno, campitura e fondo insieme -- non cade
                    // esattamente su nessun segmento.
                    var sogliaColore = quantoNastro >= 0.9 ? 20.0 : 9.0;
                    if (DistanzaDalSegmento(colori, i) > sogliaColore) continue;
                }
                else if (!CucituraFraDuePiuGrandi(colori, area, vicini, quante, i))
                {
                    continue;
                }

                daTogliere.Add(i);
                areaTolta += area[i];
            }
            if (daTogliere.Count == 0) return null;
            // Se i candidati coprissero una fetta larga dell'immagine vorrebbe dire che il criterio
            // non la descrive: meglio non toccare niente che smontare la tavolozza.
            if (areaTolta > (long)quantiPixel * 15 / 100) return null;
            // Sotto due tinte non c'e' piu' un disegno.
            if (quante - daTogliere.Count < 2) return null;

            var togli = new bool[quante];
            foreach (var i in daTogliere) togli[i] = true;

            // Nuova tavolozza compattata, piu' la corrispondenza fra vecchi e nuovi indici.
            var nuova = new List<Colore>();
            var mappa = new byte[quante];
            for (var i = 0; i < quante; i++)
            {
                if (togli[i]) continue;
                mappa[i] = (byte)nuova.Count;
                nuova.Add(colori[i]);
            }
            // Ogni tinta tolta va alla piu' vicina fra quelle rimaste.
            for (var i = 0; i < quante; i++)
            {
                if (!togli[i]) continue;
                byte migliore = 0;
                var minimo = double.MaxValue;
                for (byte k = 0; k < nuova.Count; k++)
                {
                    double dr = nuova[k].R - colori[i].R, dg = nuova[k].G - colori[i].G, db = nuova[k].B - colori[i].B;
                    var d = dr * dr + dg * dg + db * db;
                    if (d < minimo) { minimo = d; migliore = k; }
                }
                mappa[i] = migliore;
            }

            for (var i = 0; i < pixel; i++) indici[i] = mappa[indici[i]];
            return nuova.ToArray();
        }

        /// <summary>
        /// Registra il contatto con un pixel vicino. Un vicino trasparente segna il confine ma non
        /// vota per nessuna tinta: li' finisce il disegno, non comincia un'altra campitura.
        /// </summary>
        private static void Vicino(byte[] indici, bool[]? opachi, int dove, byte mia, int baseRiga,
                                   int[] vicini, ref bool tocca)
        {
            if (opachi != null && !opachi[dove]) { tocca = true; return; }
            var u = indici[dove];
            if (u == mia) return;
            tocca = true;
            vicini[baseRiga + u]++;
        }

        /// <summary>
        /// La tinta indicata e' la cucitura fra due campiture molto piu' grandi di lei?
        ///
        /// E' la prova che distingue una frangia da una banda di ombreggiatura quando la sola
        /// geometria non basta (vedi <see cref="TogliNastriIntermedi"/>). Si guardano i due vicini
        /// con cui confina di piu' e si chiede tre cose: che siano loro a circondarla quasi tutta,
        /// che siano entrambi molto piu' estesi di lei, e che il suo colore stia sul segmento fra i
        /// **loro** due -- non fra due tinte qualsiasi, che per un grigio sarebbe sempre vero.
        /// </summary>
        private static bool CucituraFraDuePiuGrandi(Colore[] colori, int[] area, int[] vicini,
                                                    int quante, int quale)
        {
            var b = quale * quante;
            long totale = 0;
            int primo = -1, secondo = -1;
            for (var j = 0; j < quante; j++)
            {
                if (j == quale) continue;
                totale += vicini[b + j];
                if (primo < 0 || vicini[b + j] > vicini[b + primo]) { secondo = primo; primo = j; }
                else if (secondo < 0 || vicini[b + j] > vicini[b + secondo]) secondo = j;
            }
            if (primo < 0 || secondo < 0 || totale == 0) return false;

            // Se il confine e' spartito fra molte tinte non sta cucendo due cose: sta in mezzo a un
            // gruppo, che e' quel che fa una banda di ombreggiatura.
            if (vicini[b + primo] + vicini[b + secondo] < totale * 60 / 100) return false;

            // Il fattore che separa davvero le due famiglie. Misurato: frange a 40 e 65, bande di
            // ombreggiatura fra 0,4 e 2,5, tinte piatte di un logo fino a 1,8.
            var minore = Math.Min(area[primo], area[secondo]);
            if (minore < (long)area[quale] * 8) return false;

            return DistanzaDalSegmentoFra(colori, quale, primo, secondo) <= 9.0;
        }

        /// <summary>
        /// Quanto la tinta indicata e' lontana dal segmento fra due tinte **date**. A differenza di
        /// <see cref="DistanzaDalSegmento"/>, che cerca la coppia piu' comoda fra tutte, qui la
        /// coppia e' quella che la circonda davvero: e' cio' che impedisce a un grigio di sembrare
        /// una mescolanza solo perche' ogni grigio sta fra il nero e il bianco.
        /// </summary>
        private static double DistanzaDalSegmentoFra(Colore[] colori, int quale, int a, int b)
        {
            var c = colori[quale];
            double vx = colori[b].R - colori[a].R;
            double vy = colori[b].G - colori[a].G;
            double vz = colori[b].B - colori[a].B;
            var lung = vx * vx + vy * vy + vz * vz;
            if (lung < 1) return double.MaxValue;

            double wx = c.R - colori[a].R, wy = c.G - colori[a].G, wz = c.B - colori[a].B;
            var s = (wx * vx + wy * vy + wz * vz) / lung;
            if (s < 0) s = 0; else if (s > 1) s = 1;

            var dx = wx - s * vx; var dy = wy - s * vy; var dz = wz - s * vz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Quanto la tinta indicata e' lontana dal segmento che unisce la coppia di tinte piu'
        /// vicina a lei. Piccolo significa "e' una loro mescolanza", cioe' una frangia di confine.
        /// Si guardano solo le posizioni centrali del segmento: agli estremi ogni colore e'
        /// banalmente vicino a se stesso.
        /// </summary>
        private static double DistanzaDalSegmento(Colore[] colori, int quale)
        {
            var migliore = double.MaxValue;
            var c = colori[quale];
            for (var a = 0; a < colori.Length; a++)
            {
                if (a == quale) continue;
                for (var b = a + 1; b < colori.Length; b++)
                {
                    if (b == quale) continue;
                    double vx = colori[b].R - colori[a].R;
                    double vy = colori[b].G - colori[a].G;
                    double vz = colori[b].B - colori[a].B;
                    var lung = vx * vx + vy * vy + vz * vz;
                    if (lung < 1) continue;

                    double wx = c.R - colori[a].R, wy = c.G - colori[a].G, wz = c.B - colori[a].B;
                    var s = (wx * vx + wy * vy + wz * vz) / lung;
                    // Intervallo ampio: una frangia puo' somigliare molto piu' a un lato che
                    // all'altro, e restringerlo la lascerebbe passare -- misurato, una frangia a
                    // s=0,14 fra il contorno e la campitura sfuggiva con il limite a 0,15.
                    if (s < 0.08 || s > 0.92) continue;

                    var dx = wx - s * vx; var dy = wy - s * vy; var dz = wz - s * vz;
                    var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d < migliore) migliore = d;
                }
            }
            return migliore;
        }

        /// <summary>
        /// Liscia i confini fra le zone di colore in vista del tracciato.
        ///
        /// ## Il difetto
        /// Il contorno di una campitura, nella mappa dei colori, e' una **scalinata**: dove
        /// nell'originale c'e' una diagonale sfumata, dopo la quantizzazione c'e' una successione
        /// di gradini alti un pixel. potrace la asseconda, e ne escono contorni che da lontano
        /// sembrano giusti e ingranditi ondeggiano -- linee imprecise invece che linee pulite.
        ///
        /// ## Perche' non basta <see cref="Liscia"/>
        /// Quella regola cambia un pixel solo quando sei degli otto vicini la contraddicono: toglie
        /// i granelli isolati, non la scalinata, che e' fatta di pixel ciascuno in buona compagnia.
        ///
        /// ## Come
        /// Si sfoca l'appartenenza a ciascuna tinta e si riassegna ogni pixel a quella che vince.
        /// Mediando su un intorno, un gradino alto un pixel diventa una rampa, e il confine finisce
        /// a meta' della rampa -- cioe' lungo la diagonale invece che lungo gli scalini.
        ///
        /// Si sfocano le **appartenenze**, non le maschere una per una. E' la differenza fra avere
        /// e non avere fessure: sfocando ogni maschera per conto suo, due zone vicine si ritirano
        /// entrambe dal confine comune e fra loro resta una riga di fondo. Qui invece il risultato
        /// e' ancora una partizione -- ogni pixel appartiene a una tinta e a una sola -- quindi le
        /// zone continuano a combaciare esattamente.
        ///
        /// ## Perche' il raggio e' uno
        /// Perche' e' alto un pixel il difetto da togliere. Provato anche due: le linee escono un
        /// filo piu' morbide, ma gli angoli netti si smussano visibilmente -- e uno spigolo di una
        /// lettera e' forma, non rumore. Sopra il raggio uno si smette di togliere la scalinata e si
        /// comincia a togliere il disegno.
        ///
        /// Misurato sull'illustrazione da 8 megapixel: l'SVG scende da 216 a 206 KB e i contorni
        /// ingranditi diventano curve continue invece che ondulate.
        /// </summary>
        public static void LisciaPerTracciato(Esito esito, int larghezza, int altezza)
        {
            if (esito == null || esito.Colori.Length < 2) return;
            if (larghezza < 3 || altezza < 3) return;

            var indici = esito.Indici;
            var opaco = esito.Opaco;
            var quante = esito.Colori.Length;
            var migliore = new byte[indici.Length];
            var scelta = new byte[indici.Length];
            Array.Copy(indici, scelta, indici.Length);

            var indicatore = new byte[indici.Length];
            for (var c = 0; c < quante; c++)
            {
                // Il trasparente non vota: se contasse, il confine del ritaglio attirerebbe verso
                // fuori le tinte che gli stanno accanto.
                for (var i = 0; i < indici.Length; i++)
                    indicatore[i] = indici[i] == c && (opaco.Length == 0 || opaco[i]) ? (byte)255 : (byte)0;
                // Due passate di media mobile: una sola lascerebbe un profilo a spigoli, due
                // approssimano una campana e danno una rampa liscia.
                var sfocato = Media(Media(indicatore, larghezza, altezza, RaggioLisciatura),
                                    larghezza, altezza, RaggioLisciatura);
                for (var i = 0; i < indici.Length; i++)
                {
                    if (opaco.Length != 0 && !opaco[i]) continue;
                    var v = sfocato[i];
                    // A parita' vince la tinta che il pixel aveva gia': questa passata deve togliere
                    // i gradini, non spostare i confini.
                    if (v > migliore[i] || (v == migliore[i] && indici[i] == c))
                    {
                        migliore[i] = v;
                        scelta[i] = (byte)c;
                    }
                }
            }

            Array.Copy(scelta, indici, indici.Length);

            // I conteggi seguono i pixel: chi legge la tavolozza si aspetta che dicano il vero.
            var conteggi = new int[quante];
            for (var i = 0; i < indici.Length; i++)
                if (opaco.Length == 0 || opaco[i]) conteggi[indici[i]]++;
            for (var i = 0; i < quante; i++) esito.Colori[i].Pixel = conteggi[i];
        }

        /// <summary>Quanto si sfoca l'appartenenza: vedi <see cref="LisciaPerTracciato"/>.</summary>
        private const int RaggioLisciatura = 1;

        /// <summary>Media mobile separabile su una finestra quadrata di lato 2r+1.</summary>
        private static byte[] Media(byte[] campo, int larghezza, int altezza, int raggio)
        {
            var passaggio = new byte[campo.Length];
            var uscita = new byte[campo.Length];
            var n = 2 * raggio + 1;

            for (var y = 0; y < altezza; y++)
            {
                var riga = y * larghezza;
                var somma = 0;
                for (var x = -raggio; x <= raggio; x++) somma += campo[riga + Stretta(x, larghezza)];
                for (var x = 0; x < larghezza; x++)
                {
                    passaggio[riga + x] = (byte)(somma / n);
                    somma -= campo[riga + Stretta(x - raggio, larghezza)];
                    somma += campo[riga + Stretta(x + raggio + 1, larghezza)];
                }
            }
            for (var x = 0; x < larghezza; x++)
            {
                var somma = 0;
                for (var y = -raggio; y <= raggio; y++) somma += passaggio[Stretta(y, altezza) * larghezza + x];
                for (var y = 0; y < altezza; y++)
                {
                    uscita[y * larghezza + x] = (byte)(somma / n);
                    somma -= passaggio[Stretta(y - raggio, altezza) * larghezza + x];
                    somma += passaggio[Stretta(y + raggio + 1, altezza) * larghezza + x];
                }
            }
            return uscita;
        }

        /// <summary>Fuori dai bordi si ripete l'ultimo pixel, invece di inventare nero.</summary>
        private static int Stretta(int v, int limite)
        {
            return v < 0 ? 0 : v > limite - 1 ? limite - 1 : v;
        }

        /// <summary>
        /// Toglie il pulviscolo: le macchie troppo piccole per essere un dettaglio.
        ///
        /// ## Perche' serve
        /// Dopo la quantizzazione la mappa dei colori e' costellata di macchioline da pochi pixel,
        /// che sono rumore del JPEG o pixel di transizione fra due campiture. Misurato
        /// sull'illustrazione da 8 megapixel: **1.668 macchie, di cui 1.276 sotto i 64 pixel, con
        /// mediana 8**. Non si vedono, ma costano tre volte:
        /// 1. ognuna e' un contorno chiuso in piu' nel file;
        /// 2. ognuna posa due o piu' **nodi** sul confine della campitura che la ospita, e ogni nodo
        ///    spezza quel confine in due archi che poi si raccordano ad angolo;
        /// 3. il file cresce. Misurato: senza questa passata l'SVG pesava 769 KB, e l'82% degli
        ///    archi era lungo una sola curva.
        ///
        /// ## Come
        /// Ogni macchia sotto la soglia passa alla tinta con cui **confina di piu'**: e' la scelta
        /// che sposta meno il disegno, perche' la macchia sparisce dentro la campitura che gia' la
        /// circonda. Si procede dalla piu' piccola, cosi' due granelli accostati si fondono prima
        /// nel piu' grande e poi insieme nella campitura, invece di scambiarsi i pixel.
        ///
        /// Una macchia isolata nel trasparente non ha con chi fondersi e resta dov'e': li' non e'
        /// pulviscolo, e' l'unico disegno che c'e'.
        /// </summary>
        /// <param name="soglia">Sotto quanti pixel una macchia e' rumore: vedi <see cref="SogliaGranelli"/>.</param>
        public static void TogliIGranelli(Esito esito, int larghezza, int altezza, int soglia)
        {
            if (esito == null || esito.Colori.Length < 2 || soglia < 2) return;
            var pixel = larghezza * altezza;
            if (esito.Indici.Length != pixel) return;

            var indici = esito.Indici;
            var opaco = esito.Opaco;
            var haOpaco = opaco != null && opaco.Length == pixel;

            // Le macchie: componenti connesse per lato, non per angolo. Due granelli che si toccano
            // solo in diagonale sono due granelli, e vanno tolti tutti e due.
            var macchia = new int[pixel];
            for (var i = 0; i < pixel; i++) macchia[i] = -1;
            var membri = new List<List<int>>();
            var coda = new int[pixel];

            for (var s = 0; s < pixel; s++)
            {
                if (macchia[s] >= 0) continue;
                if (haOpaco && !opaco![s]) continue;
                var tinta = indici[s];
                var id = membri.Count;
                var testa = 0; var fine = 0;
                coda[fine++] = s; macchia[s] = id;
                while (testa < fine)
                {
                    var i = coda[testa++];
                    var x = i % larghezza; var y = i / larghezza;
                    if (x > 0) Accoda(i - 1, tinta, id, indici, opaco, haOpaco, macchia, coda, ref fine);
                    if (x < larghezza - 1) Accoda(i + 1, tinta, id, indici, opaco, haOpaco, macchia, coda, ref fine);
                    if (y > 0) Accoda(i - larghezza, tinta, id, indici, opaco, haOpaco, macchia, coda, ref fine);
                    if (y < altezza - 1) Accoda(i + larghezza, tinta, id, indici, opaco, haOpaco, macchia, coda, ref fine);
                }
                var lista = new List<int>(fine);
                for (var k = 0; k < fine; k++) lista.Add(coda[k]);
                membri.Add(lista);
            }

            var ordine = new List<int>(membri.Count);
            for (var i = 0; i < membri.Count; i++) if (membri[i].Count < soglia) ordine.Add(i);
            ordine.Sort((a, b) => membri[a].Count.CompareTo(membri[b].Count));

            var confine = new int[esito.Colori.Length];
            foreach (var id in ordine)
            {
                var lista = membri[id];
                // Fondendosi con una vicina la macchia puo' essere gia' cresciuta oltre la soglia:
                // in tal caso non e' piu' pulviscolo.
                if (lista.Count >= soglia) continue;

                Array.Clear(confine, 0, confine.Length);
                var mia = indici[lista[0]];
                foreach (var i in lista)
                {
                    var x = i % larghezza; var y = i / larghezza;
                    if (x > 0) Conta(i - 1, mia, indici, opaco, haOpaco, confine);
                    if (x < larghezza - 1) Conta(i + 1, mia, indici, opaco, haOpaco, confine);
                    if (y > 0) Conta(i - larghezza, mia, indici, opaco, haOpaco, confine);
                    if (y < altezza - 1) Conta(i + larghezza, mia, indici, opaco, haOpaco, confine);
                }

                var vincitrice = -1;
                var quanto = 0;
                for (var c = 0; c < confine.Length; c++)
                    if (confine[c] > quanto) { quanto = confine[c]; vincitrice = c; }
                if (vincitrice < 0) continue;   // isolata nel trasparente: non si tocca

                foreach (var i in lista) indici[i] = (byte)vincitrice;

                // La macchia inghiottita entra a far parte di quella che l'ha assorbita, cosi' il
                // conto delle dimensioni resta vero per i passi successivi.
                var assorbente = -1;
                foreach (var i in lista)
                {
                    var x = i % larghezza; var y = i / larghezza;
                    if (x > 0 && Vicina(i - 1, vincitrice, indici, macchia, id, out assorbente)) break;
                    if (x < larghezza - 1 && Vicina(i + 1, vincitrice, indici, macchia, id, out assorbente)) break;
                    if (y > 0 && Vicina(i - larghezza, vincitrice, indici, macchia, id, out assorbente)) break;
                    if (y < altezza - 1 && Vicina(i + larghezza, vincitrice, indici, macchia, id, out assorbente)) break;
                }
                if (assorbente >= 0)
                {
                    foreach (var i in lista) macchia[i] = assorbente;
                    membri[assorbente].AddRange(lista);
                    lista.Clear();
                }
            }

            var conteggi = new int[esito.Colori.Length];
            for (var i = 0; i < pixel; i++)
                if (!haOpaco || opaco![i]) conteggi[indici[i]]++;
            for (var i = 0; i < esito.Colori.Length; i++) esito.Colori[i].Pixel = conteggi[i];
        }

        private static void Accoda(int i, byte tinta, int id, byte[] indici, bool[]? opaco,
                                   bool haOpaco, int[] macchia, int[] coda, ref int fine)
        {
            if (macchia[i] >= 0) return;
            if (haOpaco && !opaco![i]) return;
            if (indici[i] != tinta) return;
            macchia[i] = id;
            coda[fine++] = i;
        }

        private static void Conta(int i, byte mia, byte[] indici, bool[]? opaco, bool haOpaco, int[] confine)
        {
            if (haOpaco && !opaco![i]) return;
            var c = indici[i];
            if (c == mia) return;
            confine[c]++;
        }

        private static bool Vicina(int i, int vincitrice, byte[] indici, int[] macchia, int id, out int quale)
        {
            quale = -1;
            if (macchia[i] == id || macchia[i] < 0) return false;
            if (indici[i] != vincitrice) return false;
            quale = macchia[i];
            return true;
        }

        /// <summary>
        /// Sotto quanti pixel una macchia e' rumore invece che un dettaglio.
        ///
        /// Si scala con l'immagine perche' "piccolo" dipende da quanto e' grande il foglio: venti
        /// pixel sono un granello su quattro megapixel e un dettaglio su un francobollo. E' la
        /// stessa misura che si dava a potrace quando era lui a scartarli.
        /// </summary>
        public static int SogliaGranelli(int larghezza, int altezza)
        {
            var n = (long)larghezza * altezza / 90000;
            return n < 4 ? 4 : n > 60 ? 60 : (int)n;
        }

        /// <summary>
        /// Liscia i confini fra le zone di colore.
        ///
        /// ## A cosa serve, che la spolveratura non fa
        /// La spolveratura toglie i granelli isolati. Ma dove una sfumatura attraversa il confine
        /// fra due tinte il confine non e' una linea: e' una frangia larga e frastagliata, fatta di
        /// pixel che hanno ciascuno dei vicini uguali a se'. potrace traccia quella frangia dente
        /// per dente, e il risultato e' il bordo marmorizzato sul corpo di una figura ombreggiata.
        ///
        /// ## La regola, e perche' non mangia i contorni
        /// Un pixel passa alla tinta dei vicini solo quando **almeno sei degli otto** la
        /// condividono. Su una frangia frastagliata questo accade spesso e la frangia si compatta;
        /// su un bordo vero non accade mai, perche' un bordo divide l'intorno grosso modo a meta'
        /// e nessuna delle due parti arriva a sei. E' la stessa idea della mediana, applicata pero'
        /// alle etichette invece che ai colori -- che e' quel che potrace legge davvero.
        /// </summary>
        private static void Liscia(byte[] indici, int larghezza, int altezza)
        {
            if (larghezza < 3 || altezza < 3) return;

            // Due passate: la prima compatta la frangia, la seconda chiude quel che la prima ha
            // lasciato a meta'. Oltre la seconda i confini non si muovono piu' e si rischia solo
            // di erodere i dettagli sottili.
            for (var passata = 0; passata < 2; passata++)
            {
                var origine = (byte[])indici.Clone();
                var conteggio = new int[256];
                var cambiati = 0;

                for (var y = 1; y < altezza - 1; y++)
                {
                    for (var x = 1; x < larghezza - 1; x++)
                    {
                        var i = y * larghezza + x;
                        var v = origine[i];

                        byte prevalente = v;
                        var quanti = 0;
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                var w = origine[i + dy * larghezza + dx];
                                var c = ++conteggio[w];
                                if (c > quanti) { quanti = c; prevalente = w; }
                            }
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                                if (dx != 0 || dy != 0) conteggio[origine[i + dy * larghezza + dx]] = 0;

                        if (quanti >= 6 && prevalente != v) { indici[i] = prevalente; cambiati++; }
                    }
                }
                if (cambiati == 0) break;
            }
        }

        /// <summary>
        /// Fonde le tinte che nessuno distinguerebbe, restituendo una tavolozza piu' corta.
        ///
        /// Due tinte a meno di una dozzina di livelli l'una dall'altra sono lo stesso colore per
        /// chi guarda, ma non per l'algoritmo: la mappa le separa, e il confine fra le due diventa
        /// un tracciato che potrace disegna dentro una campitura uniforme. Il risultato e' una
        /// banda fantasma che sporca il disegno e pesa nel file.
        ///
        /// Si tiene la tinta piu' estesa delle due, e si rimette a fuoco quel che resta.
        /// </summary>
        private static Colore[] UnisciGemelle(Dictionary<int, int> istogramma, Colore[] colori)
        {
            // Sotto questa distanza due colori sono lo stesso colore: circa dodici livelli per
            // canale, il limite sotto cui l'occhio non separa due campiture affiancate.
            //
            // Il valore e' stato alzato a diciotto per far combaciare due tinte che descrivevano
            // la stessa ombreggiatura in un caso di prova, e su quel caso funzionava. Sull'immagine
            // vera ha tolto un marrone intermedio -- distante ottocento, cioe' dentro la soglia
            // piu' larga ma fuori da questa -- e gli orsi hanno perso il volume. E' il rischio di
            // tarare su un caso costruito: la soglia larga risolveva un difetto che avevo
            // fabbricato io e ne creava uno che l'utente vedeva davvero.
            const double StessoColore = 12.0 * 12.0 * 3;

            // Si fonde, si rimette a fuoco, e **si ricontrolla**. Il giro serve perche' Raffina
            // sposta le tinte superstiti verso il centro dei pixel che hanno ereditato: due tinte
            // che prima della fusione erano appena oltre la soglia possono finire dentro, e senza
            // un secondo controllo restano separate per sempre.
            //
            // Misurato sull'immagine degli orsi: due marroni di contorno a distanza 379 -- sotto la
            // soglia di 432 -- sopravvivevano entrambi, perche' erano finiti li' *dopo* l'ultimo
            // controllo. Il contorno veniva quindi tracciato due volte con due scuri diversi, e ne
            // uscivano l'occhio sbriciolato e le striature lungo le linee.
            var vivi = new List<Colore>(colori);
            var cambiata = false;

            // Otto giri sono una rete di sicurezza: in pratica ne bastano due, e senza un tetto una
            // fusione che riaprisse sempre la successiva girerebbe all'infinito.
            for (var giro = 0; giro < 8; giro++)
            {
                var fusoNelGiro = false;
                var fuso = true;
                while (fuso && vivi.Count > 2)
                {
                    fuso = false;
                    for (var i = 0; i < vivi.Count && !fuso; i++)
                    {
                        for (var j = i + 1; j < vivi.Count; j++)
                        {
                            double dr = vivi[i].R - vivi[j].R, dg = vivi[i].G - vivi[j].G, db = vivi[i].B - vivi[j].B;
                            if (dr * dr + dg * dg + db * db >= StessoColore) continue;

                            // Si sacrifica la meno estesa: quella piu' grande descrive la campitura
                            // vera, la piccola ne e' una scheggia.
                            vivi.RemoveAt(vivi[i].Pixel >= vivi[j].Pixel ? j : i);
                            fuso = true;
                            fusoNelGiro = true;
                            break;
                        }
                    }
                }

                if (!fusoNelGiro) break;
                cambiata = true;

                // Rimessa a fuoco sulla tavolozza accorciata. Il giro successivo verifica se questo
                // spostamento ha avvicinato due superstiti oltre il lecito.
                var passo = vivi.ToArray();
                Raffina(istogramma, passo);
                vivi = new List<Colore>(passo);
            }

            return cambiata ? vivi.ToArray() : colori;
        }

        /// <summary>
        /// La tinta piu' vicina a una casella di colore, calcolata alla prima richiesta e poi
        /// ricordata.
        ///
        /// Serve perche' la tavolozza nasce dai soli pixel interni, mentre l'assegnazione riguarda
        /// **tutti** i pixel: quelli di contorno hanno colori che nell'istogramma non compaiono, e
        /// vanno comunque messi da qualche parte -- dal lato piu' vicino, che e' proprio quel che
        /// rende netto il bordo.
        /// </summary>
        private static byte Assegna(Dictionary<int, byte> cache, Colore[] colori, int chiave)
        {
            byte gia;
            if (cache.TryGetValue(chiave, out gia)) return gia;

            var r = Centro((chiave >> (Bit * 2)) & (Livelli - 1));
            var g = Centro((chiave >> Bit) & (Livelli - 1));
            var b = Centro(chiave & (Livelli - 1));

            byte migliore = 0;
            var minimo = double.MaxValue;
            for (byte i = 0; i < colori.Length; i++)
            {
                double dr = colori[i].R - r, dg = colori[i].G - g, db = colori[i].B - b;
                var d = dr * dr + dg * dg + db * db;
                if (d < minimo) { minimo = d; migliore = i; }
            }
            cache[chiave] = migliore;
            return migliore;
        }

        /// <summary>
        /// Segna i pixel che stanno su un contorno, per tenerli fuori dalla tavolozza.
        ///
        /// ## Perche' e' il punto decisivo
        /// Fra una campitura e il contorno scuro che la circonda c'e' sempre una frangia di pixel
        /// intermedi: l'antialiasing del disegno piu' la compressione JPEG. Quella frangia e'
        /// numerosa e cromaticamente coerente, quindi una quantizzazione che la guarda le assegna
        /// una tinta **propria**. Il danno e' doppio: nasce un alone che segue ogni contorno e
        /// sporca il disegno, e si perde uno dei colori veri, perche' le tinte sono un numero fisso.
        ///
        /// Misurato su un caso in stile: **tre tinte su otto** finite sugli aloni, e un pesce
        /// arancione rimasto fuori dalla tavolozza. Spiega insieme il disegno sporco, i file
        /// pesanti e i colori mancanti -- tre sintomi con una causa sola.
        ///
        /// Toglierli **dopo** non funziona: i pixel di frangia restano, e la tinta successiva si
        /// riforma sopra di loro. Provato e misurato: eliminando gli aloni a posteriori se ne
        /// ricreavano due su tre. L'unico rimedio e' non farli entrare.
        ///
        /// ## Come si riconosce un pixel di frangia
        /// Non basta chiedersi "sta vicino a un contorno?": la risposta sarebbe si' anche per il
        /// **cuore** di una linea sottile, che verrebbe cancellata dalla tavolozza. Provato e
        /// misurato: escludendo una fascia attorno ai contorni, un profilo scuro spesso nove pixel
        /// spariva del tutto e le figure restavano senza linea.
        ///
        /// La domanda giusta e' un'altra: **questo colore e' un estremo o una via di mezzo?** In un
        /// intorno che attraversa un contorno ci sono il colore scuro della linea, quello chiaro
        /// della campitura, e in mezzo la frangia. Il cuore della linea e' il minimo del suo
        /// intorno, la campitura il massimo: entrambi estremi, entrambi colori veri. La frangia sta
        /// nel mezzo, e solo lei viene esclusa.
        ///
        /// Chi resta fuori riceve comunque un colore alla fine, il piu' vicino fra quelli scelti:
        /// cosi' la frangia si divide fra i due lati e il bordo diventa netto.
        /// </summary>
        private static bool[] PixelDiBordo(byte[] rgb, int larghezza, int altezza)
        {
            var fuori = new bool[larghezza * altezza];
            if (larghezza < 3 || altezza < 3) return fuori;

            // Sotto questo salto l'intorno e' una campitura uniforme e non c'e' nessuna frangia:
            // trentadue livelli su 255 tollerano il rumore di compressione e le ombreggiature
            // dolci, e si fermano davanti al gradino di un contorno.
            const int Salto = 32;

            for (var y = 1; y < altezza - 1; y++)
            {
                var riga = y * larghezza;
                for (var x = 1; x < larghezza - 1; x++)
                {
                    var i = riga + x;
                    var c = i * 3;
                    var mio = (rgb[c] * 54 + rgb[c + 1] * 183 + rgb[c + 2] * 19) >> 8;

                    var minimo = 255;
                    var massimo = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var v = ((y + dy) * larghezza + (x + dx)) * 3;
                            var l = (rgb[v] * 54 + rgb[v + 1] * 183 + rgb[v + 2] * 19) >> 8;
                            if (l < minimo) minimo = l;
                            if (l > massimo) massimo = l;
                        }

                    var ampiezza = massimo - minimo;
                    if (ampiezza <= Salto) continue;      // campitura: colore vero, resta dentro

                    // Un quarto di margine ai due capi: chi ci sta dentro e' un estremo, cioe' il
                    // cuore della linea o il pieno della campitura. Chi sta in mezzo e' frangia.
                    var margine = ampiezza / 4;
                    if (mio <= minimo + margine || mio >= massimo - margine) continue;

                    fuori[i] = true;
                }
            }

            return fuori;
        }

        /// <summary>
        /// Appiana l'immagine con una mediana, prima di ridurre i colori.
        ///
        /// ## Perche'
        /// La spolveratura toglie i granelli isolati, e basta finche' l'immagine e' fatta di tinte
        /// piatte. Con le **ombreggiature morbide** no: dove una sfumatura attraversa il confine
        /// fra due tinte, i pixel non si alternano in punti isolati ma su fasce larghe, e ogni
        /// pixel ha vicini come lui -- quindi la spolveratura, giustamente, non lo tocca. Il
        /// risultato e' una macchia marmorizzata sul corpo di un orso, che e' esattamente il
        /// difetto segnalato.
        ///
        /// ## Perche' la mediana e non una sfocatura
        /// Una sfocatura media i pixel e mangia i contorni: su un'illustrazione con la linea nera
        /// intorno alle figure sarebbe un disastro. La mediana prende il valore centrale, quindi
        /// su un bordo netto restituisce il colore di uno dei due lati e il bordo resta netto,
        /// mentre su una zona increspata restituisce il valore prevalente e l'increspatura sparisce.
        ///
        /// La finestra e' 3x3: abbastanza per togliere l'alternanza, poco per spostare un contorno.
        /// </summary>
        private static byte[] Appiana(byte[] rgb, int larghezza, int altezza)
        {
            if (larghezza < 3 || altezza < 3) return rgb;

            var fuori = (byte[])rgb.Clone();
            var f = new byte[9];

            for (var y = 1; y < altezza - 1; y++)
            {
                for (var x = 1; x < larghezza - 1; x++)
                {
                    var centro = (y * larghezza + x) * 3;
                    for (var canale = 0; canale < 3; canale++)
                    {
                        var k = 0;
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                                f[k++] = rgb[((y + dy) * larghezza + (x + dx)) * 3 + canale];

                        // Mediana di nove valori con una piccola rete di ordinamento: bastano
                        // diciannove confronti e nessuna allocazione, che su quattro megapixel
                        // per tre canali e' la differenza fra un secondo e una decina.
                        Scambia(f, 0, 1); Scambia(f, 3, 4); Scambia(f, 6, 7);
                        Scambia(f, 1, 2); Scambia(f, 4, 5); Scambia(f, 7, 8);
                        Scambia(f, 0, 1); Scambia(f, 3, 4); Scambia(f, 6, 7);
                        Scambia(f, 0, 3); Scambia(f, 5, 8); Scambia(f, 4, 7);
                        Scambia(f, 3, 6); Scambia(f, 1, 4); Scambia(f, 2, 5);
                        Scambia(f, 4, 7); Scambia(f, 4, 2); Scambia(f, 6, 4);
                        Scambia(f, 4, 2);
                        fuori[centro + canale] = f[4];
                    }
                }
            }
            return fuori;
        }

        private static void Scambia(byte[] v, int a, int b)
        {
            if (v[a] > v[b]) { var t = v[a]; v[a] = v[b]; v[b] = t; }
        }

        /// <summary>
        /// Toglie i pixel isolati dalla mappa dei colori.
        ///
        /// ## Perche' servono
        /// Un JPEG non ha aree piatte: la compressione lascia oscillare di qualche livello anche
        /// una campitura uniforme. Dove il colore vero cade a meta' fra due tinte della tavolozza,
        /// i pixel si alternano fra le due, e il risultato non e' una sfumatura ma un pulviscolo.
        /// potrace non sa che e' rumore: traccia ogni granello, e una faccia beige esce screziata
        /// come se fosse sporca. Misurato sul caso di prova: lo 0,17% dei pixel con otto tinte,
        /// lo 0,75% con ventiquattro -- il difetto **peggiora** aumentando i colori, che e' il
        /// contrario di quel che si spera facendolo.
        ///
        /// ## Come
        /// Ogni pixel il cui indice non compare in nessuno dei quattro vicini viene sostituito con
        /// l'indice piu' frequente attorno. Solo i pixel completamente isolati: un dettaglio vero,
        /// per quanto piccolo, ha almeno un vicino del suo stesso colore, quindi resta intatto.
        /// </summary>
        private static void Spolvera(byte[] indici, int larghezza, int altezza)
        {
            if (larghezza < 3 || altezza < 3) return;

            // Si legge da una copia: correggendo sul posto, un pixel gia' corretto cambierebbe il
            // giudizio sul vicino, e la pulizia si propagherebbe come una macchia.
            var origine = (byte[])indici.Clone();
            var conteggio = new int[256];

            for (var y = 1; y < altezza - 1; y++)
            {
                var riga = y * larghezza;
                for (var x = 1; x < larghezza - 1; x++)
                {
                    var i = riga + x;
                    var v = origine[i];
                    if (origine[i - 1] == v || origine[i + 1] == v ||
                        origine[i - larghezza] == v || origine[i + larghezza] == v) continue;

                    var migliore = v;
                    var quanti = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var c = ++conteggio[origine[i + dy * larghezza + dx]];
                            if (c > quanti) { quanti = c; migliore = origine[i + dy * larghezza + dx]; }
                        }
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                            if (dx != 0 || dy != 0) conteggio[origine[i + dy * larghezza + dx]] = 0;

                    indici[i] = migliore;
                }
            }
        }

        /// <summary>
        /// Da' una tinta ai colori che l'ottimizzazione ha scartato perche' occupano poco spazio.
        ///
        /// ## Il problema
        /// Sia il taglio mediano sia Lloyd minimizzano l'errore **totale**, e in un totale un
        /// oggetto piccolo non pesa: un pesce arancione di ottocento pixel su un milione e mezzo
        /// puo' essere completamente sbagliato senza spostare la somma. Il risultato e' che un
        /// dettaglio dal colore unico -- proprio quello che l'occhio cerca per primo -- diventa
        /// beige, e aumentare le tinte non lo salva, perche' le tinte nuove vanno comunque dove
        /// sta la massa.
        ///
        /// ## La regola
        /// Si cerca il colore piu' lontano da tutte le tinte scelte, fra quelli che occupano
        /// almeno una frazione minima dell'immagine, e gli si dedica una tinta. La frazione minima
        /// serve a non inseguire il rumore di compressione, che e' fatto proprio di colori strani
        /// con pochissimi pixel.
        ///
        /// Si sacrifica una delle tinte esistenti -- la piu' vicina a un'altra, cioe' quella che
        /// distingue di meno -- invece di aggiungerne una: chi ha chiesto otto tinte ne vuole otto,
        /// e ogni tinta e' una passata di potrace.
        /// </summary>
        private static void SalvaIColoriDimenticati(Dictionary<int, int> istogramma, Colore[] colori, int pixel)
        {
            if (colori.Length < 4) return;

            // Quanti pixel deve valere un colore per meritare una tinta. E' basso di proposito:
            // il filtro vero e' la **distanza**, non la quantita'. Il rumore di compressione fa
            // colori strani ma sempre vicini a quelli veri, quindi non supera mai la distanza
            // minima; un soggetto dal colore unico la supera anche con pochi pixel.
            var minimo = Math.Max(60, pixel / 20000);

            // Meno di questo e' gia' rappresentato bene: circa trenta livelli di distanza. La barra
            // e' alta perche' qui si **sacrifica** una tinta esistente: si rinuncia a qualcosa, e
            // deve valerne la pena.
            const double DistanzaMinima = 30 * 30 * 3;

            for (var tentativo = 0; tentativo < 2; tentativo++)
            {
                double peggiore = 0;
                int chiavePeggiore = -1;

                foreach (var kv in istogramma)
                {
                    double r = ((kv.Key >> (Bit * 2)) & (Livelli - 1)) << Scarto;
                    double g = ((kv.Key >> Bit) & (Livelli - 1)) << Scarto;
                    double b = (kv.Key & (Livelli - 1)) << Scarto;

                    var vicino = double.MaxValue;
                    for (var i = 0; i < colori.Length; i++)
                    {
                        double dr = colori[i].R - r, dg = colori[i].G - g, db = colori[i].B - b;
                        var d = dr * dr + dg * dg + db * db;
                        if (d < vicino) vicino = d;
                    }
                    if (vicino < DistanzaMinima || vicino <= peggiore) continue;

                    // I pixel di un soggetto piccolo non stanno in una casella sola: l'antialiasing
                    // e il JPEG li spargono su una decina di caselle vicine, e nessuna di esse da
                    // sola raggiungerebbe la soglia. Si sommano quindi le caselle attorno a ogni
                    // candidato -- misurato: senza questa somma il pesce non veniva mai salvato,
                    // perche' le sue ottocento occorrenze erano divise in gruppetti da poche decine.
                    var insieme = 0;
                    foreach (var kv2 in istogramma)
                    {
                        double r2 = ((kv2.Key >> (Bit * 2)) & (Livelli - 1)) << Scarto;
                        double g2 = ((kv2.Key >> Bit) & (Livelli - 1)) << Scarto;
                        double b2 = (kv2.Key & (Livelli - 1)) << Scarto;
                        var dd = (r2 - r) * (r2 - r) + (g2 - g) * (g2 - g) + (b2 - b) * (b2 - b);
                        if (dd <= DistanzaMinima) insieme += kv2.Value;
                    }
                    if (insieme < minimo) continue;

                    peggiore = vicino;
                    chiavePeggiore = kv.Key;
                }

                if (chiavePeggiore < 0) return;

                // Quale tinta togliere: quella piu' vicina a un'altra, che e' la meno utile a
                // distinguere. Mai la prima, che e' il fondo.
                var daSostituire = -1;
                var minDistanza = double.MaxValue;
                for (var i = 1; i < colori.Length; i++)
                    for (var j = 0; j < colori.Length; j++)
                    {
                        if (i == j) continue;
                        double dr = colori[i].R - colori[j].R, dg = colori[i].G - colori[j].G, db = colori[i].B - colori[j].B;
                        var d = dr * dr + dg * dg + db * db;
                        if (d < minDistanza) { minDistanza = d; daSostituire = i; }
                    }
                if (daSostituire < 0 || minDistanza >= peggiore) return;

                colori[daSostituire] = DaChiave(chiavePeggiore);

                // Rimessa a fuoco: la tinta nuova va portata al centro dei pixel che ora le
                // toccano, e le vicine vanno lasciate riassestare attorno.
                Raffina(istogramma, colori);
            }
        }


        /// <summary>
        /// Quanto distano i colori di due pixel dell'immagine di partenza, in distanza quadrata:
        /// la stessa unita' con cui si misurano le distanze fra tinte, cosi' i due valori si
        /// possono confrontare direttamente.
        /// </summary>
        private static double ScartoQuadrato(byte[] rgb, int pixelA, int pixelB)
        {
            var p = pixelA * 3;
            var q = pixelB * 3;
            double dr = rgb[p] - rgb[q], dg = rgb[p + 1] - rgb[q + 1], db = rgb[p + 2] - rgb[q + 2];
            return dr * dr + dg * dg + db * db;
        }

        /// <summary>
        /// Il livello di colore al centro della casella d'istogramma che lo contiene.
        ///
        /// La casella raccoglie otto livelli contigui. Riassumerla con il suo **angolo basso** --
        /// che e' quel che faceva il semplice spostamento a sinistra -- sposta ogni colore verso il
        /// basso da zero a sette livelli, sempre nella stessa direzione: una distorsione
        /// sistematica, non un arrotondamento. Sul bianco pieno si vedeva a occhio nudo, perche'
        /// 255 usciva 248.
        /// </summary>
        private static int Centro(int livello)
        {
            return (livello << Scarto) | (1 << (Scarto - 1));
        }

        /// <summary>Il colore al centro di una casella dell'istogramma.</summary>
        private static Colore DaChiave(int chiave)
        {
            return new Colore(
                (byte)Centro((chiave >> (Bit * 2)) & (Livelli - 1)),
                (byte)Centro((chiave >> Bit) & (Livelli - 1)),
                (byte)Centro(chiave & (Livelli - 1)), 0);
        }

        /// <summary>
        /// Sposta ogni colore nel centro dei pixel che gli appartengono, ripetutamente.
        ///
        /// E' l'algoritmo di Lloyd, applicato alle caselle dell'istogramma invece che ai pixel:
        /// trentaduemila caselle pesate valgono milioni di pixel e costano una frazione del tempo.
        /// Poche passate bastano -- dopo la quarta o quinta i colori si spostano di meno di un
        /// livello, e continuare sarebbe tempo speso per una differenza che nessuno vede.
        /// </summary>
        private static void Raffina(Dictionary<int, int> istogramma, Colore[] colori)
        {
            if (colori.Length < 2) return;

            var n = colori.Length;
            var sr = new double[n];
            var sg = new double[n];
            var sb = new double[n];
            var peso = new double[n];

            for (var giro = 0; giro < 6; giro++)
            {
                Array.Clear(sr, 0, n); Array.Clear(sg, 0, n);
                Array.Clear(sb, 0, n); Array.Clear(peso, 0, n);

                foreach (var kv in istogramma)
                {
                    var chiave = kv.Key;
                    var q = kv.Value;
                    double r = Centro((chiave >> (Bit * 2)) & (Livelli - 1));
                    double g = Centro((chiave >> Bit) & (Livelli - 1));
                    double b = Centro(chiave & (Livelli - 1));

                    var migliore = 0;
                    var minimo = double.MaxValue;
                    for (var i = 0; i < n; i++)
                    {
                        double dr = colori[i].R - r, dg = colori[i].G - g, db = colori[i].B - b;
                        var d = dr * dr + dg * dg + db * db;
                        if (d < minimo) { minimo = d; migliore = i; }
                    }
                    sr[migliore] += r * q; sg[migliore] += g * q; sb[migliore] += b * q;
                    peso[migliore] += q;
                }

                var spostamento = 0.0;
                for (var i = 0; i < n; i++)
                {
                    // Un colore rimasto senza pixel si lascia dov'e': spostarlo a caso non
                    // aggiungerebbe una tinta utile, ne toglierebbe una gia' assestata.
                    if (peso[i] <= 0) continue;
                    var nr = Arrotonda(sr[i] / peso[i]);
                    var ng = Arrotonda(sg[i] / peso[i]);
                    var nb = Arrotonda(sb[i] / peso[i]);
                    spostamento += Math.Abs(nr - colori[i].R) + Math.Abs(ng - colori[i].G) + Math.Abs(nb - colori[i].B);
                    colori[i] = new Colore(nr, ng, nb, colori[i].Pixel);
                }
                if (spostamento < n) break;   // meno di un livello a colore: si e' assestato
            }
        }

        private static byte Arrotonda(double v)
        {
            var i = (int)Math.Round(v);
            return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }

        /// <summary>Una scatola nello spazio dei colori: un intervallo per canale.</summary>
        private class Scatola
        {
            private readonly List<int> _chiavi;
            private readonly Dictionary<int, int> _conteggi;
            private int _r0, _r1, _g0, _g1, _b0, _b1;

            public int Pixel { get; private set; }

            private Scatola(List<int> chiavi, Dictionary<int, int> conteggi)
            {
                _chiavi = chiavi;
                _conteggi = conteggi;
                Ricalcola();
            }

            public static Scatola Da(Dictionary<int, int> istogramma)
            {
                return new Scatola(new List<int>(istogramma.Keys), istogramma);
            }

            public bool Divisibile { get { return _chiavi.Count > 1; } }

            /// <summary>Quanto e' ampia la scatola: il lato piu' lungo conta il doppio degli altri.</summary>
            public double Volume
            {
                get
                {
                    double dr = _r1 - _r0, dg = _g1 - _g0, db = _b1 - _b0;
                    return Math.Max(dr, Math.Max(dg, db)) + (dr + dg + db) / 3.0;
                }
            }

            private void Ricalcola()
            {
                _r0 = _g0 = _b0 = int.MaxValue;
                _r1 = _g1 = _b1 = int.MinValue;
                Pixel = 0;
                foreach (var k in _chiavi)
                {
                    int r = (k >> (Bit * 2)) & (Livelli - 1);
                    int g = (k >> Bit) & (Livelli - 1);
                    int b = k & (Livelli - 1);
                    if (r < _r0) _r0 = r; if (r > _r1) _r1 = r;
                    if (g < _g0) _g0 = g; if (g > _g1) _g1 = g;
                    if (b < _b0) _b0 = b; if (b > _b1) _b1 = b;
                    Pixel += _conteggi[k];
                }
            }

            /// <summary>
            /// Taglia in due lungo il canale piu' esteso, alla mediana **dei pixel** e non delle
            /// caselle: due meta' con lo stesso numero di caselle possono contenere una il fondo e
            /// l'altra quattro pixel di rumore.
            /// </summary>
            public bool Dividi(out Scatola? a, out Scatola? b)
            {
                a = null; b = null;
                if (_chiavi.Count < 2) return false;

                int dr = _r1 - _r0, dg = _g1 - _g0, db = _b1 - _b0;
                int canale = dr >= dg && dr >= db ? 2 : dg >= db ? 1 : 0;
                var spostamento = canale * Bit;

                _chiavi.Sort(delegate (int x, int y)
                {
                    var vx = (x >> spostamento) & (Livelli - 1);
                    var vy = (y >> spostamento) & (Livelli - 1);
                    return vx.CompareTo(vy);
                });

                var meta = Pixel / 2;
                var somma = 0;
                var taglio = 0;
                for (var i = 0; i < _chiavi.Count - 1; i++)
                {
                    somma += _conteggi[_chiavi[i]];
                    if (somma >= meta) { taglio = i + 1; break; }
                }
                if (taglio <= 0 || taglio >= _chiavi.Count) taglio = _chiavi.Count / 2;

                a = new Scatola(_chiavi.GetRange(0, taglio), _conteggi);
                b = new Scatola(_chiavi.GetRange(taglio, _chiavi.Count - taglio), _conteggi);
                return true;
            }

            /// <summary>Il colore medio della scatola, pesato sui pixel.</summary>
            public Colore Medio()
            {
                double r = 0, g = 0, b = 0;
                long n = 0;
                foreach (var k in _chiavi)
                {
                    var q = _conteggi[k];
                    r += Centro((k >> (Bit * 2)) & (Livelli - 1)) * (double)q;
                    g += Centro((k >> Bit) & (Livelli - 1)) * (double)q;
                    b += Centro(k & (Livelli - 1)) * (double)q;
                    n += q;
                }
                if (n == 0) return new Colore(0, 0, 0, 0);
                return new Colore(Arrotonda(r / n), Arrotonda(g / n), Arrotonda(b / n), (int)n);
            }

            private static byte Arrotonda(double v)
            {
                var i = (int)Math.Round(v);
                return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
            }
        }
    }
}




















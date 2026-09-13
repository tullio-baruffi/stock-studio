using System;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Di che pasta e' fatto il disegno, per poterlo lavorare di conseguenza.
    ///
    /// ## Il difetto che risolve
    /// Una sola taratura per tutto e' una taratura sbagliata per quasi tutto. Quella di serie era
    /// stata misurata su un'illustrazione ombreggiata, e sui line art del portfolio -- icone, animali
    /// a contorno, loghi -- faceva un danno visibile: le punte si smussavano e i vuoti fra i tratti
    /// si stringevano. Non perche' i numeri fossero sbagliati in assoluto, ma perche' erano giusti
    /// per un altro genere di disegno.
    ///
    /// ## Cosa si misura, e perche' non lo spessore
    /// Verrebbe da guardare quanto sono sottili i tratti. Non funziona: un line art e
    /// un'illustrazione possono avere tratti larghi uguali, e infatti li hanno -- misurato sul
    /// portfolio, 19 px il leone a contorno e 19 px la balena ombreggiata alla stessa grandezza.
    ///
    /// Quel che li separa e' **come cambia il colore attraversando un bordo**. In un line art salta
    /// da una tinta all'altra in un pixel o due, perche' il disegnatore l'ha voluto netto; in
    /// un'illustrazione passa per decine di valori intermedi. Lisciare il primo si vede subito;
    /// sul secondo la lisciatura assomiglia a quel che l'originale gia' fa.
    ///
    /// Si misura come scarto medio fra ogni pixel e la tinta a cui la tavolozza l'ha assegnato.
    /// Misurato sulle immagini vere del portfolio:
    ///     line art a due tinte:          2,5  2,6  3,0  3,2
    ///     illustrazioni a 7 e 15 tinte:  4,2  4,3
    /// </summary>
    public static class Disegno
    {
        /// <summary>
        /// Sotto questo scarto il disegno e' fatto di tinte piatte, e non va lisciato.
        ///
        /// Sta in mezzo fra i due gruppi misurati -- 3,2 il line art piu' sfumato, 4,2
        /// l'illustrazione piu' piatta -- dove il margine e' massimo da entrambe le parti. Un
        /// taglio piu' alto comincerebbe a togliere la lisciatura a illustrazioni che ne hanno
        /// bisogno; piu' basso la lascerebbe su line art che ne vengono rovinati.
        /// </summary>
        public const double ScartoTintePiatte = 3.7;

        /// <summary>
        /// Quante tinte usa la sonda. E' un numero fisso e diverso da quello del tracciato,
        /// apposta: la misura deve dire com'e' fatto il **disegno**, non come e' stato tarato il
        /// tracciato. Chiedendo le stesse tinte del tracciato, alzarle abbasserebbe lo scarto e la
        /// stessa immagine cambierebbe genere per una scelta che col disegno non c'entra.
        /// </summary>
        private const int TinteDellaSonda = 16;

        /// <summary>
        /// Quanto il disegno si discosta dalle proprie tinte: vedi <see cref="Disegno"/>.
        ///
        /// Si misura sui pixel **come sono arrivati**, prima di qualunque pulizia: togliere il
        /// rumore appiattisce, e misurare dopo farebbe sembrare a tinte piatte anche quel che non
        /// lo e'. E' l'errore che questa misura ha fatto la prima volta -- con la mediana davanti,
        /// pure un'illustrazione ombreggiata scendeva sotto la soglia.
        /// </summary>
        /// <summary>
        /// Quel che si riesce a dire di un disegno guardandolo, prima di tracciarlo.
        ///
        /// Serve a proporre una taratura invece di applicarne una uguale per tutti. Ogni voce e'
        /// una misura, non un giudizio: il giudizio lo fa <see cref="Consiglia"/>, e tenerli
        /// separati vuol dire che si puo' discutere della regola senza rimisurare.
        /// </summary>
        public class Misure
        {
            /// <summary>Quanto il disegno si discosta dalle proprie tinte: basso = tinte piatte.</summary>
            public double Scarto;
            /// <summary>Quante tinte distingue la sonda: poche = disegno grafico, molte = illustrazione.</summary>
            public int Tinte;
            /// <summary>
            /// Lo spessore tipico di una struttura, in pixel: due volte l'area diviso il perimetro.
            /// Per un tratto lungo e largo s vale s, ed e' la misura che dice quanto si puo'
            /// lisciare o semplificare senza mangiarlo.
            /// </summary>
            public double Spessore;
            /// <summary>Che frazione del foglio e' disegno invece che fondo.</summary>
            public double Inchiostro;
            /// <summary>Il lato lungo dell'immagine misurata.</summary>
            public int Lato;

            /// <summary>Vero quando il disegno e' fatto di tinte piatte e non va lisciato.</summary>
            public bool ATintePiatte { get { return Scarto < ScartoTintePiatte; } }

            /// <summary>Come si chiama, in una parola, quel che si e' misurato.</summary>
            public string Genere
            {
                get { return ATintePiatte ? "disegno a tinte piatte" : "illustrazione sfumata"; }
            }
        }

        /// <summary>
        /// Guarda il disegno e riferisce com'e' fatto. Una passata sola, riusata da tutto il resto.
        /// </summary>
        public static Misure Guarda(byte[] rgb, int larghezza, int altezza, bool[]? opachi)
        {
            var m = new Misure { Lato = Math.Max(larghezza, altezza) };
            if (rgb == null || larghezza < 1 || altezza < 1) return m;
            if (rgb.Length < (long)larghezza * altezza * 3) return m;

            var maschera = opachi != null && opachi.Length == larghezza * altezza ? opachi : null;
            var sonda = Tavolozza.Riduci(rgb, larghezza, altezza, TinteDellaSonda,
                                         Tavolozza.UnionePredefinita, maschera);
            if (sonda.Colori.Length == 0) return m;

            m.Tinte = sonda.Colori.Length;
            var indici = sonda.Indici;
            var opaco = sonda.Opaco;

            // Lo scarto dalle proprie tinte: vedi il commento della classe.
            double somma = 0;
            long quanti = 0;
            for (var i = 0; i < indici.Length; i++)
            {
                if (opaco.Length != 0 && !opaco[i]) continue;
                var c = sonda.Colori[indici[i]];
                double dr = rgb[i * 3] - c.R, dg = rgb[i * 3 + 1] - c.G, db = rgb[i * 3 + 2] - c.B;
                somma += Math.Sqrt(dr * dr + dg * dg + db * db);
                quanti++;
            }
            m.Scarto = quanti == 0 ? 0 : somma / quanti;

            // Lo spessore: il fondo non e' una struttura da conservare, quindi la tinta piu' estesa
            // si esclude dal conto dell'inchiostro.
            var conteggi = new long[sonda.Colori.Length];
            for (var i = 0; i < indici.Length; i++)
                if (opaco.Length == 0 || opaco[i]) conteggi[indici[i]]++;
            var fondo = 0;
            for (var i = 1; i < conteggi.Length; i++) if (conteggi[i] > conteggi[fondo]) fondo = i;
            long inchiostro = 0;
            for (var i = 0; i < conteggi.Length; i++) if (i != fondo) inchiostro += conteggi[i];

            long confini = 0;
            for (var y = 0; y < altezza; y++)
                for (var x = 0; x < larghezza; x++)
                {
                    var i = y * larghezza + x;
                    if (x + 1 < larghezza && indici[i] != indici[i + 1]) confini++;
                    if (y + 1 < altezza && indici[i] != indici[i + larghezza]) confini++;
                }

            m.Spessore = confini > 0 ? 2.0 * inchiostro / confini : 0;
            m.Inchiostro = (double)inchiostro / ((long)larghezza * altezza);
            return m;
        }

        /// <summary>
        /// La taratura che questo disegno chiede, espressa come chiunque altro la scrive: riferita
        /// a <see cref="ParametriTracciato.LatoDiRiferimento"/>, cosi' che
        /// <see cref="ParametriTracciato.PerImmagine"/> la riporti poi alla grandezza vera senza
        /// applicare due volte la stessa scala.
        ///
        /// ## Da cosa viene ogni numero
        /// **Lisciatura** dallo scarto: zero se il disegno e' a tinte piatte, uno se e' sfumato.
        /// E' la scelta che pesa di piu' e l'unica misurata a fondo (vedi <see cref="Disegno"/>).
        ///
        /// **Granelli** e **riduzione del rumore** dallo spessore, ma solo su un disegno a tinte
        /// piatte e mai sopra il predefinito: li' le macchioline piccole sono dettagli veri, mentre
        /// su un'illustrazione sfumata sono frammenti d'ombra da togliere, e abbassarli
        /// peggiorerebbe il disegno.
        ///
        /// **Tinte** da quante ne distingue la sonda, con un margine: chiederne molte di piu' non
        /// ne inventa e costa tempo, chiederne meno butta via colori che ci sono.
        ///
        /// Tolleranza, angolo, morbidezza e giri restano quelli di serie: misurandoli sulle
        /// immagini vere non si e' vista una regola che li leghi al disegno, e inventarne una
        /// sarebbe peggio che lasciarli dove sono.
        /// </summary>
        public static ParametriTracciato Consiglia(Misure m, ParametriTracciato? partenza = null)
        {
            var p = (partenza ?? ParametriTracciato.Predefiniti).Convalidato();
            if (m == null || m.Lato < 1 || m.Spessore <= 0) return p;

            // Dalla grandezza vera a quella convenzionale: i numeri qui sotto si misurano sui
            // pixel di questa immagine, ma vanno scritti come li scriverebbe chiunque altro.
            var s = (double)m.Lato / ParametriTracciato.LatoDiRiferimento;
            if (s < 0.33) s = 0.33;
            if (s > 4) s = 4;

            p.RaggioLisciatura = m.ATintePiatte ? 0 : 1;
            p.LisciaturaAutomatica = false;          // e' una scelta fatta guardando: e' esplicita

            // Granelli e riduzione del rumore si abbassano **solo** su un disegno a tinte piatte,
            // e mai sopra il predefinito.
            //
            // Non e' prudenza: e' che le due famiglie hanno pulviscolo di natura opposta. Su un
            // line art le macchioline piccole sono dettagli veri -- un occhio, una narice, la punta
            // di una ciocca -- e vanno tenute. Su un'illustrazione sfumata sono invece frammenti
            // che la riduzione a tinte ha staccato da una banda d'ombra, e toglierli e' tutto il
            // guadagno. Misurato sulla balena: derivando i granelli dallo spessore anche li', i
            // contorni salivano da 404 a 590 e il file da 407 a 546 KB -- il consiglio peggiorava
            // il disegno che la taratura di serie gia' faceva bene.
            if (m.ATintePiatte)
            {
                var granelli = m.Spessore * m.Spessore / 4.0 / (s * s);
                p.Granelli = (int)Math.Round(Limita(granelli, 8, p.Granelli));

                var rumore = m.Spessore / 8.0 / s;
                p.RiduzioneRumore = (int)Math.Round(Limita(rumore, 0, p.RiduzioneRumore));
            }

            p.NumeroColori = (int)Math.Round(Limita(m.Tinte * 1.5, 6, 48));

            return p.Convalidato();
        }

        /// <summary>La taratura consigliata guardando direttamente i pixel.</summary>
        public static ParametriTracciato Consiglia(byte[] rgb, int larghezza, int altezza,
                                                   bool[]? opachi, ParametriTracciato? partenza = null)
        {
            return Consiglia(Guarda(rgb, larghezza, altezza, opachi), partenza);
        }

        private static double Limita(double v, double min, double max)
        {
            if (double.IsNaN(v)) return min;
            return v < min ? min : v > max ? max : v;
        }

        /// <summary>
        /// Quanto il disegno si discosta dalle proprie tinte: vedi <see cref="Disegno"/>.
        ///
        /// Si misura sui pixel **come sono arrivati**, prima di qualunque pulizia: togliere il
        /// rumore appiattisce, e misurare dopo farebbe sembrare a tinte piatte anche quel che non
        /// lo e'.
        /// </summary>
        public static double Scarto(byte[] rgb, int larghezza, int altezza, bool[]? opachi)
        {
            return Guarda(rgb, larghezza, altezza, opachi).Scarto;
        }

        /// <summary>Vero quando il disegno e' fatto di tinte piatte e non va lisciato.</summary>
        public static bool ATintePiatte(byte[] rgb, int larghezza, int altezza, bool[]? opachi)
        {
            return Scarto(rgb, larghezza, altezza, opachi) < ScartoTintePiatte;
        }

        /// <summary>
        /// Il raggio di lisciatura giusto per questo disegno, quando si e' scelto di lasciar
        /// decidere. Zero sulle tinte piatte, il valore chiesto altrove.
        /// </summary>
        public static int LisciaturaPer(byte[] rgb, int larghezza, int altezza, bool[]? opachi,
                                        ParametriTracciato p)
        {
            if (p == null) return 1;
            if (!p.LisciaturaAutomatica) return p.RaggioLisciatura;
            return ATintePiatte(rgb, larghezza, altezza, opachi) ? 0 : p.RaggioLisciatura;
        }
    }
}

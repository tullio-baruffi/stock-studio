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
        /// <param name="rgb">Tre byte per pixel, composti su bianco, non ancora ripuliti.</param>
        /// <param name="opachi">
        /// Quali pixel sono disegno. Null **o vuoto** vuol dire tutti: e' la stessa convenzione che
        /// usa <see cref="Tavolozza.Esito.Opaco"/>, che vuoto lo e' per difetto, e non normalizzarla
        /// qui farebbe cadere la sonda proprio sulle immagini senza trasparenza -- cioe' quasi tutte.
        /// </param>
        public static double Scarto(byte[] rgb, int larghezza, int altezza, bool[]? opachi)
        {
            if (rgb == null || larghezza < 1 || altezza < 1) return 0;
            if (rgb.Length < (long)larghezza * altezza * 3) return 0;

            var maschera = opachi != null && opachi.Length == larghezza * altezza ? opachi : null;
            var sonda = Tavolozza.Riduci(rgb, larghezza, altezza, TinteDellaSonda,
                                         Tavolozza.UnionePredefinita, maschera);
            if (sonda.Colori.Length == 0) return 0;

            var indici = sonda.Indici;
            var opaco = sonda.Opaco;
            double somma = 0;
            long quanti = 0;

            for (var i = 0; i < indici.Length; i++)
            {
                if (opaco.Length != 0 && !opaco[i]) continue;
                if (i * 3 + 2 >= rgb.Length) break;
                var c = sonda.Colori[indici[i]];
                double dr = rgb[i * 3] - c.R;
                double dg = rgb[i * 3 + 1] - c.G;
                double db = rgb[i * 3 + 2] - c.B;
                somma += Math.Sqrt(dr * dr + dg * dg + db * db);
                quanti++;
            }

            return quanti == 0 ? 0 : somma / quanti;
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

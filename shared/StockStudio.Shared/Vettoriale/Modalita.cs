using System;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Cosa fare di un file caricato, detto in un posto solo.
    ///
    /// Le modalita' sono tre e una e' nuova, il che e' esattamente il momento in cui un sistema
    /// si rompe in silenzio: da qualche parte resta scritto `mode == "vector"`, la modalita' nuova
    /// non ci passa, e il difetto non e' un errore ma un file trattato come una fotografia.
    /// Qui si chiede "e' vettoriale?" invece di confrontare stringhe, cosi' aggiungere una
    /// modalita' domani vuol dire toccare questo file e nessun altro.
    /// </summary>
    public static class Modalita
    {
        /// <summary>Silhouette in bianco e nero: una soglia di luminanza e una passata di potrace.</summary>
        public const string BiancoENero = "vector";

        /// <summary>Tracciato a colori: una passata di potrace per tinta.</summary>
        public const string Colore = "colore";

        /// <summary>Nessun tracciato: l'immagine si consegna com'e'.</summary>
        public const string Raster = "raster";

        /// <summary>
        /// Riporta quel che arriva da fuori a una delle tre modalita'.
        /// Sconosciuto o vuoto diventa bianco e nero, che e' il comportamento di sempre.
        /// </summary>
        public static string Normalizza(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return BiancoENero;
            var m = mode!.Trim();
            if (string.Equals(m, Raster, StringComparison.OrdinalIgnoreCase)) return Raster;
            if (string.Equals(m, Colore, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "color", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "vector-color", StringComparison.OrdinalIgnoreCase)) return Colore;
            return BiancoENero;
        }

        /// <summary>
        /// Vero per tutto cio' che produce curve, sia in bianco e nero sia a colori.
        ///
        /// E' la domanda che si fanno le regole di Adobe e la validazione: a loro non interessa
        /// quante tinte ha il file, interessa che sia un vettoriale.
        /// </summary>
        public static bool EVettoriale(string? mode)
        {
            var m = Normalizza(mode);
            return m == BiancoENero || m == Colore;
        }

        /// <summary>Vero quando si e' chiesto esplicitamente il colore.</summary>
        public static bool EAColori(string? mode)
        {
            return Normalizza(mode) == Colore;
        }
    }
}

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
        /// <summary>
        /// Si guarda l'immagine e si decide: a colori se colori ne ha, silhouette se non ne ha.
        ///
        /// E' il predefinito, e prima non esisteva. Le altre due modalita' sono **imposizioni**:
        /// dicono al motore di non guardare. Imporre il bianco e nero su un'illustrazione a colori
        /// non produce un errore -- produce un disegno a tinta unita, che e' il difetto peggiore
        /// perche' sembra un risultato. E' quel che succedeva a chi caricava senza toccare niente:
        /// la pagina partiva su bianco e nero, e ogni line art a colori tornava nero pieno.
        /// </summary>
        public const string Automatico = "auto";

        /// <summary>Silhouette in bianco e nero: una soglia di luminanza e una passata di potrace.</summary>
        public const string BiancoENero = "vector";

        /// <summary>Tracciato a colori: i confini fra le tinte, condivisi fra le campiture.</summary>
        public const string Colore = "colore";

        /// <summary>Nessun tracciato: l'immagine si consegna com'e'.</summary>
        public const string Raster = "raster";

        /// <summary>
        /// Riporta quel che arriva da fuori a una delle modalita'.
        ///
        /// Vuoto o sconosciuto vale **automatico**: e' quel che il contratto di coda dichiarava
        /// gia' ("vuoto o sconosciuto vale auto") mentre qui si rispondeva bianco e nero, e la
        /// contraddizione rendeva la scelta automatica irraggiungibile dal caricamento. Fra le due
        /// letture vince quella che guarda l'immagine: non aver detto niente vuol dire non avere
        /// una preferenza, non volere una silhouette.
        /// </summary>
        public static string Normalizza(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return Automatico;
            var m = mode!.Trim();
            if (string.Equals(m, Raster, StringComparison.OrdinalIgnoreCase)) return Raster;
            if (string.Equals(m, BiancoENero, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "bn", StringComparison.OrdinalIgnoreCase)) return BiancoENero;
            if (string.Equals(m, Colore, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "color", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "vector-color", StringComparison.OrdinalIgnoreCase)) return Colore;
            return Automatico;
        }

        /// <summary>
        /// Vero per tutto cio' che produce curve, sia in bianco e nero sia a colori.
        ///
        /// E' la domanda che si fanno le regole di Adobe e la validazione: a loro non interessa
        /// quante tinte ha il file, interessa che sia un vettoriale. L'automatico e' vettoriale:
        /// qualunque delle due strade prenda, curve ne escono.
        /// </summary>
        public static bool EVettoriale(string? mode)
        {
            var m = Normalizza(mode);
            return m == BiancoENero || m == Colore || m == Automatico;
        }

        /// <summary>
        /// Vero quando si e' chiesto **esplicitamente** il colore.
        ///
        /// L'automatico non conta: li' il colore e' possibile ma non deciso, e chi deve sapere
        /// com'e' andata a finire deve guardare l'esito del tracciato, non la richiesta.
        /// </summary>
        public static bool EAColori(string? mode)
        {
            return Normalizza(mode) == Colore;
        }

        /// <summary>
        /// Se il colore e' imposto, imposto il bianco e nero, o da decidere guardando l'immagine.
        /// Null vuol dire "guarda tu", ed e' il caso normale.
        /// </summary>
        public static bool? ColoreImposto(string? mode)
        {
            var m = Normalizza(mode);
            if (m == Colore) return true;
            if (m == BiancoENero) return false;
            return null;
        }
    }
}

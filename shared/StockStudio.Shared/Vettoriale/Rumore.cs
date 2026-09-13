using System;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Toglie il rumore dell'immagine **prima** che si decida quali sono le tinte.
    ///
    /// ## Il difetto che risolve
    /// Un JPEG non ha campiture piatte. Dentro una zona che il disegnatore ha riempito di un colore
    /// solo, i pixel ondeggiano di qualche livello; lungo ogni contorno netto la compressione
    /// lascia un alone che ondeggia di piu'. Finche' si guarda l'immagine non si nota, perche'
    /// l'occhio media. Ma la riduzione a poche tinte non media: sceglie, pixel per pixel, la tinta
    /// piu' vicina. Dove due tinte si contendono una zona, quell'ondeggiamento decide da che parte
    /// cade ogni pixel, e il confine fra le due esce **frastagliato prima ancora** che qualcuno
    /// provi a disegnarlo.
    ///
    /// E' cio' che si vedeva sulle bolle dell'illustrazione delle balene: cerchi perfetti
    /// nell'originale, poligoni bitorzoluti nel tracciato. Nessuna lisciatura a valle poteva
    /// rimediare, perche' a valle il bitorzolo non e' piu' rumore -- e' il confine.
    ///
    /// ## Perche' una mediana e non una sfocatura
    /// Una sfocatura toglie il rumore mediando, e mediando attraverso un bordo netto lo allarga:
    /// la frangia di transizione diventa piu' larga, e la tinta di confine -- che gia' oggi va
    /// scartata perche' descrive una frangia invece di una zona -- diventa piu' grossa. Si toglie
    /// un difetto e se ne alimenta un altro.
    ///
    /// La mediana no. Dove il vicinato e' di un colore solo restituisce quel colore; dove c'e' un
    /// bordo restituisce il colore della parte che ha la maggioranza, cioe' **una delle due**, mai
    /// una loro mescolanza. Un bordo netto resta esattamente dov'e' e largo quanto era. Toglie il
    /// rumore senza toccare la forma, ed e' l'unico filtro di questa famiglia per cui questo valga.
    ///
    /// ## Come, senza far aspettare
    /// La mediana costa: una finestra di raggio due sono venticinque valori da ordinare per ogni
    /// pixel di ogni canale. Qui si usa l'algoritmo di Huang, che tiene un istogramma della
    /// finestra e lo aggiorna spostandosi -- una colonna esce, una entra -- e insieme all'istogramma
    /// tiene la mediana corrente e quanti valori le stanno sotto. La mediana nuova si trova
    /// muovendosi di un passo o due da quella vecchia, invece di ricominciare: il costo per pixel
    /// non dipende piu' da quanto e' larga la finestra.
    /// </summary>
    public static class Rumore
    {
        /// <summary>
        /// La mediana su una finestra quadrata di lato 2r+1, canale per canale.
        /// </summary>
        /// <param name="rgb">Tre byte per pixel, riga per riga. Non viene modificato.</param>
        /// <param name="raggio">Raggio della finestra. Zero o meno restituisce l'originale.</param>
        /// <returns>Un nuovo buffer, o lo stesso quando non c'e' niente da fare.</returns>
        public static byte[] Mediana(byte[] rgb, int larghezza, int altezza, int raggio)
        {
            if (rgb == null) throw new ArgumentNullException("rgb");
            if (raggio < 1 || larghezza < 1 || altezza < 1) return rgb;
            if (rgb.Length < larghezza * altezza * 3) return rgb;
            // Con una finestra piu' larga dell'immagine la mediana e' una tinta unita: non e' una
            // lavorazione, e' la cancellazione del disegno.
            if (2 * raggio + 1 > larghezza || 2 * raggio + 1 > altezza) return rgb;

            var uscita = new byte[rgb.Length];
            for (var canale = 0; canale < 3; canale++)
                UnCanale(rgb, uscita, larghezza, altezza, raggio, canale);
            return uscita;
        }

        private static void UnCanale(byte[] dentro, byte[] fuori, int larghezza, int altezza,
                                     int raggio, int canale)
        {
            var lato = 2 * raggio + 1;
            var quanti = lato * lato;
            // La mediana e' il valore in posizione quanti/2 contando da zero: con una finestra di
            // lato dispari i valori sono in numero dispari e quella posizione e' proprio il mezzo.
            var meta = quanti / 2;
            var istogramma = new int[256];

            for (var y = 0; y < altezza; y++)
            {
                Array.Clear(istogramma, 0, 256);
                for (var dx = -raggio; dx <= raggio; dx++)
                    AggiungiColonna(dentro, istogramma, larghezza, altezza, raggio, canale, dx, y, +1);

                // La mediana di partenza si cerca una volta sola, da capo; per tutta la riga poi si
                // aggiusta di un passo alla volta.
                int mediana = 0, sotto = 0;
                while (sotto + istogramma[mediana] <= meta)
                {
                    sotto += istogramma[mediana];
                    mediana++;
                }
                fuori[(y * larghezza) * 3 + canale] = (byte)mediana;

                for (var x = 1; x < larghezza; x++)
                {
                    sotto += Colonna(dentro, istogramma, larghezza, altezza, raggio, canale,
                                     x - raggio - 1, y, -1, mediana);
                    sotto += Colonna(dentro, istogramma, larghezza, altezza, raggio, canale,
                                     x + raggio, y, +1, mediana);

                    if (sotto > meta)
                    {
                        do { mediana--; sotto -= istogramma[mediana]; } while (sotto > meta);
                    }
                    else
                    {
                        while (sotto + istogramma[mediana] <= meta)
                        {
                            sotto += istogramma[mediana];
                            mediana++;
                        }
                    }

                    fuori[(y * larghezza + x) * 3 + canale] = (byte)mediana;
                }
            }
        }

        private static void AggiungiColonna(byte[] dentro, int[] istogramma, int larghezza, int altezza,
                                            int raggio, int canale, int x, int y, int segno)
        {
            Colonna(dentro, istogramma, larghezza, altezza, raggio, canale, x, y, segno, -1);
        }

        /// <summary>
        /// Aggiunge o toglie dall'istogramma la colonna di pixel sopra e sotto (x, y).
        ///
        /// Fuori dall'immagine si ripete il pixel di bordo: e' cio' che tiene la finestra sempre
        /// piena degli stessi <c>(2r+1)^2</c> valori, e quindi la mediana sempre nella stessa
        /// posizione. Contando meno valori ai bordi bisognerebbe ricalcolarla lì, e il bordo
        /// dell'immagine e' proprio dove conviene non avere casi speciali.
        /// </summary>
        /// <returns>
        /// Di quanto cambia il conteggio dei valori sotto <paramref name="mediana"/>, cosi' chi
        /// chiama non deve riscorrere l'istogramma. Con mediana negativa il conteggio non serve.
        /// </returns>
        private static int Colonna(byte[] dentro, int[] istogramma, int larghezza, int altezza,
                                   int raggio, int canale, int x, int y, int segno, int mediana)
        {
            var cx = x < 0 ? 0 : x >= larghezza ? larghezza - 1 : x;
            var delta = 0;
            for (var dy = -raggio; dy <= raggio; dy++)
            {
                var cy = y + dy;
                if (cy < 0) cy = 0; else if (cy >= altezza) cy = altezza - 1;
                var v = dentro[(cy * larghezza + cx) * 3 + canale];
                istogramma[v] += segno;
                if (mediana >= 0 && v < mediana) delta += segno;
            }
            return delta;
        }
    }
}

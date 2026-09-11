using System;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Il canale alfa, letto prima di buttarlo via.
    ///
    /// ## Il difetto che risolve
    /// Un logo ritagliato arriva come PNG con il fondo trasparente. Caricato direttamente come RGB,
    /// l'alfa sparisce e sotto resta quel che il file aveva scritto nei tre canali: quasi sempre
    /// **nero**, perche' dove l'alfa e' zero il colore non conta e i codificatori ci mettono zero.
    /// Il ritaglio diventa cosi' una campitura nera grande quanto la tavola -- misurato su un logo
    /// vero: il nero occupava il 46% dell'immagine ed era la prima voce della tavolozza, quindi si
    /// prendeva una tinta, veniva tracciato, e il vettoriale usciva col fondo nero. Lo stesso
    /// accadeva al JPEG di consegna.
    ///
    /// ## Le due cose che servono, e sono diverse
    /// 1. **Quali pixel sono disegno** (<see cref="Opachi"/>): serve alla tavolozza e alle
    ///    maschere, per non contare e non tracciare quel che non c'e'.
    /// 2. **Che colore hanno** (<see cref="SuBianco"/>): serve perche' il resto della lavorazione
    ///    ragiona in RGB. Si compone su **bianco**, non su nero: il bianco e' il fondo su cui una
    ///    stock si guarda, e un contorno semitrasparente composto su nero diventerebbe una riga
    ///    scura tutto attorno alla sagoma.
    ///
    /// Sta qui, e non nei due chiamanti, perche' la web app e la Function devono dare lo stesso
    /// file dalla stessa immagine. Lavora su byte grezzi per non tirare una libreria di immagini
    /// dentro un progetto che deve restare compatibile con entrambe.
    /// </summary>
    public static class Trasparenza
    {
        /// <summary>
        /// Sotto quanta opacita' un pixel non e' piu' disegno.
        ///
        /// A meta' scala: sopra, il pixel si vede e va tracciato; sotto, e' il velo di antialiasing
        /// del ritaglio, e tracciarlo allargherebbe la sagoma di un pixel tutto attorno.
        /// </summary>
        public const byte SogliaOpacita = 128;

        /// <summary>
        /// Quali pixel appartengono al disegno. I pixel arrivano come RGBA, quattro byte l'uno.
        /// </summary>
        public static bool[] Opachi(byte[] rgba)
        {
            if (rgba == null) throw new ArgumentNullException("rgba");
            var pixel = rgba.Length / 4;
            var opachi = new bool[pixel];
            for (var i = 0; i < pixel; i++) opachi[i] = rgba[i * 4 + 3] >= SogliaOpacita;
            return opachi;
        }

        /// <summary>
        /// I pixel composti su bianco, come RGB a tre byte l'uno: il trasparente diventa bianco, il
        /// semitrasparente si mescola col bianco in proporzione alla sua opacita'.
        /// </summary>
        public static byte[] SuBianco(byte[] rgba)
        {
            if (rgba == null) throw new ArgumentNullException("rgba");
            var pixel = rgba.Length / 4;
            var rgb = new byte[pixel * 3];
            for (var i = 0; i < pixel; i++)
            {
                var q = i * 4;
                var p = i * 3;
                var a = rgba[q + 3];
                if (a == 255)
                {
                    rgb[p] = rgba[q]; rgb[p + 1] = rgba[q + 1]; rgb[p + 2] = rgba[q + 2];
                    continue;
                }
                var alfa = a / 255.0;
                var complemento = 255 * (1 - alfa);
                rgb[p] = Arrotonda(rgba[q] * alfa + complemento);
                rgb[p + 1] = Arrotonda(rgba[q + 1] * alfa + complemento);
                rgb[p + 2] = Arrotonda(rgba[q + 2] * alfa + complemento);
            }
            return rgb;
        }

        /// <summary>C'e' almeno un pixel trasparente? Se no, non c'e' niente da trattare a parte.</summary>
        public static bool CeTrasparenza(bool[] opachi)
        {
            if (opachi == null) return false;
            for (var i = 0; i < opachi.Length; i++) if (!opachi[i]) return true;
            return false;
        }

        private static byte Arrotonda(double v)
        {
            var n = (int)(v + 0.5);
            return n < 0 ? (byte)0 : n > 255 ? (byte)255 : (byte)n;
        }
    }
}

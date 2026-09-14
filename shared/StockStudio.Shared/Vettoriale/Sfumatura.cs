using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// La sfumatura di una zona di colore: due estremi e la direzione che li unisce.
    /// Coordinate in pixel dell'immagine, con l'origine in alto a sinistra come nell'immagine.
    /// </summary>
    public class Rampa
    {
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public Colore Inizio { get; set; }
        public Colore Fine { get; set; }

        /// <summary>Quanto i due estremi differiscono, in livelli medi per canale.</summary>
        public double Salto { get; set; }
    }

    /// <summary>
    /// Riconosce le zone sfumate e ne stima la rampa.
    ///
    /// ## Perche' esiste
    /// Un vettoriale a tinte piatte non puo' rendere un'ombreggiatura: o la appiattisce, perdendo
    /// il volume della figura, oppure -- dandogli piu' tinte -- la spezza in bande che rigano il
    /// disegno. Misurato: con dodici e sedici tinte il corpo di un orso si rigava in diagonale.
    /// L'unico modo per rendere una sfumatura in un vettoriale e' dichiararla come tale: SVG ha
    /// &lt;linearGradient&gt; apposta.
    ///
    /// ## Come si stima
    /// Per i pixel di una tinta si guarda come varia la luminanza nello spazio: una regressione su
    /// x e y da' il piano che meglio la descrive, e la sua pendenza indica **dove** il colore
    /// cambia. Si proiettano allora i pixel su quella direzione e si prende il colore medio alle
    /// due estremita'. Se i due colori si somigliano, la zona e' piatta e la rampa non serve.
    ///
    /// ## Cosa non prova a fare
    /// Solo rampe **lineari**: quelle radiali, coniche o a piu' fermate non vengono riconosciute e
    /// restano tinte piatte. E' il tipo di ombreggiatura piu' comune nelle illustrazioni, ed e'
    /// l'unico che si possa stimare con onesta' da una regressione: inventare una rampa radiale
    /// dove non c'e' peggiorerebbe il risultato invece di migliorarlo.
    /// </summary>
    public static class Sfumatura
    {
        /// <summary>
        /// Sotto questo salto fra i due estremi la zona e' piatta: la differenza non si vede, e un
        /// gradiente inutile e' solo peso in piu' nel file.
        /// </summary>
        private const double SaltoMinimo = 9.0;

        /// <summary>
        /// Quanti pixel deve avere una zona per meritare una stima. Su poche decine di pixel una
        /// regressione descrive il rumore, non una sfumatura.
        /// </summary>
        private const int PixelMinimi = 400;

        /// <summary>
        /// Quanto dell'errore la rampa deve togliere per meritarsi il posto.
        ///
        /// Nove decimi: la rampa deve fare almeno un decimo meglio della tinta piatta. Non e' una
        /// richiesta severa, ed e' voluto -- una sfumatura vera spiega molto piu' di cosi', mentre
        /// una zona che non e' sfumata non ci arriva nemmeno per caso. Chiedere di piu'
        /// toglierebbe anche le sfumature deboli ma autentiche, che sono proprio quelle per cui
        /// questa passata esiste.
        /// </summary>
        private const double QuotaDaSpiegare = 0.9;

        /// <summary>
        /// Stima la rampa della tinta indicata, o restituisce null se la zona e' piatta, troppo
        /// piccola, o varia in un modo che una retta non descrive.
        /// </summary>
        /// <param name="rgb">I pixel **originali**: la sfumatura sta li', non nella mappa ridotta.</param>
        /// <param name="indici">La tinta assegnata a ogni pixel.</param>
        /// <param name="suBordo">
        /// I pixel di contorno, da saltare. Senza questa esclusione la frangia di bordo -- che e'
        /// una mescolanza col contorno scuro, quindi sistematicamente piu' scura -- viene letta
        /// come un capo della rampa, e una campitura piatta risulta sfumata: misurato, un'acqua
        /// azzurra uniforme dichiarata in sfumatura da grigio ad azzurro.
        /// </param>
        public static Rampa? Stima(byte[] rgb, byte[] indici, bool[]? suBordo, int larghezza, int altezza, int tinta)
        {
            // Un solo passaggio per raccogliere quel che serve alla regressione della luminanza.
            long n = 0;
            double sx = 0, sy = 0, sl = 0, sxx = 0, syy = 0, sxy = 0, sxl = 0, syl = 0;

            // Su immagini grandi si campiona a scacchiera: una pendenza si stima benissimo su un
            // pixel ogni quattro, e cosi' il costo resta trascurabile anche su quattro megapixel.
            var passo = (long)larghezza * altezza > 1500000 ? 2 : 1;
            var bordoNoto = suBordo != null && suBordo.Length == larghezza * altezza;

            // Un pixel serve alla stima solo se e' **interno** alla sua zona: sul perimetro il
            // colore e' gia' contaminato da quello accanto, e usarlo sposta gli estremi della
            // rampa verso la zona vicina -- misurato, una campitura marrone piatta risultava
            // sfumata verso il rosa perche' gli estremi cadevano sul suo stesso bordo.
            bool Interno(int idx, int x, int y)
            {
                if (bordoNoto && suBordo![idx]) return false;
                if (x < 1 || y < 1 || x >= larghezza - 1 || y >= altezza - 1) return false;
                var v = indici[idx];
                return indici[idx - 1] == v && indici[idx + 1] == v
                    && indici[idx - larghezza] == v && indici[idx + larghezza] == v;
            }

            for (var y = 0; y < altezza; y += passo)
            {
                var riga = y * larghezza;
                for (var x = 0; x < larghezza; x += passo)
                {
                    if (indici[riga + x] != tinta) continue;
                    if (!Interno(riga + x, x, y)) continue;
                    var p = (riga + x) * 3;
                    double l = 0.2126 * rgb[p] + 0.7152 * rgb[p + 1] + 0.0722 * rgb[p + 2];
                    n++;
                    sx += x; sy += y; sl += l;
                    sxx += (double)x * x; syy += (double)y * y; sxy += (double)x * y;
                    sxl += x * l; syl += y * l;
                }
            }
            if (n < PixelMinimi / (passo * passo)) return null;

            // Risoluzione del sistema normale a due incognite: pendenza in x e in y.
            var mx = sx / n; var my = sy / n; var ml = sl / n;
            var cxx = sxx - n * mx * mx;
            var cyy = syy - n * my * my;
            var cxy = sxy - n * mx * my;
            var cxl = sxl - n * mx * ml;
            var cyl = syl - n * my * ml;

            var det = cxx * cyy - cxy * cxy;
            if (Math.Abs(det) < 1e-6) return null;

            var bx = (cxl * cyy - cyl * cxy) / det;
            var by = (cyl * cxx - cxl * cxy) / det;

            var norma = Math.Sqrt(bx * bx + by * by);
            if (norma < 1e-9) return null;          // luminanza costante: zona piatta

            var dx = bx / norma;
            var dy = by / norma;

            // Secondo passaggio: si proiettano i pixel sulla direzione trovata e si raccolgono i
            // colori alle due estremita'. Si usano fasce di un decimo e non i singoli estremi,
            // perche' un estremo e' un pixel e un pixel puo' essere rumore.
            double pMin = double.MaxValue, pMax = double.MinValue;
            for (var y = 0; y < altezza; y += passo)
            {
                var riga = y * larghezza;
                for (var x = 0; x < larghezza; x += passo)
                {
                    if (indici[riga + x] != tinta) continue;
                    if (!Interno(riga + x, x, y)) continue;
                    var t = x * dx + y * dy;
                    if (t < pMin) pMin = t;
                    if (t > pMax) pMax = t;
                }
            }
            if (pMax - pMin < 8) return null;      // zona troppo sottile perche' una rampa abbia senso

            var soglia1 = pMin + (pMax - pMin) * 0.12;
            var soglia2 = pMax - (pMax - pMin) * 0.12;
            double r1 = 0, g1 = 0, b1 = 0, r2 = 0, g2 = 0, b2 = 0;
            long n1 = 0, n2 = 0;

            for (var y = 0; y < altezza; y += passo)
            {
                var riga = y * larghezza;
                for (var x = 0; x < larghezza; x += passo)
                {
                    if (indici[riga + x] != tinta) continue;
                    if (!Interno(riga + x, x, y)) continue;
                    var t = x * dx + y * dy;
                    var p = (riga + x) * 3;
                    if (t <= soglia1) { r1 += rgb[p]; g1 += rgb[p + 1]; b1 += rgb[p + 2]; n1++; }
                    else if (t >= soglia2) { r2 += rgb[p]; g2 += rgb[p + 1]; b2 += rgb[p + 2]; n2++; }
                }
            }
            if (n1 == 0 || n2 == 0) return null;

            var c1 = new Colore(Byte(r1 / n1), Byte(g1 / n1), Byte(b1 / n1), (int)n1);
            var c2 = new Colore(Byte(r2 / n2), Byte(g2 / n2), Byte(b2 / n2), (int)n2);

            var salto = (Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B)) / 3.0;
            if (salto < SaltoMinimo) return null;   // piatta: meglio una tinta sola

            // L'ultima prova, e la piu' importante: **la rampa descrive questa zona meglio di una
            // tinta sola?**
            //
            // ## Perche' mancava e cosa costava
            // Fin qui si e' stimata una rampa, non verificata. Una zona puo' benissimo avere i due
            // estremi diversi di dieci livelli senza essere affatto sfumata: basta che sia grande
            // e che il disegno ci passi sopra qualcosa. La rampa allora viene stesa su **tutta** la
            // zona, comprese le parti che erano giuste, e quel che si vede e' una velatura chiara
            // dove il colore era pieno. E' il difetto delle "macchie di colore": sul tronco del
            // castoro il bruno scuro usciva slavato e gessoso, mentre Illustrator -- che sfumature
            // non ne mette affatto -- lo teneva pieno.
            //
            // ## Come si verifica
            // Si misura l'errore che si commette nei due modi: colorando tutta la zona con la sua
            // tinta media, oppure con la rampa. Se la rampa non spiega una fetta consistente di
            // quel che varia, non e' una sfumatura -- e' una zona con dentro dell'altro, e
            // stenderci sopra un gradiente e' una perdita secca.
            //
            // Si guarda **solo il colore**, non la luminanza: la regressione sopra lavora sulla
            // luminanza perche' li' serviva una direzione, ma qui si giudica quel che si vede.
            double errPiatto = 0, errRampa = 0;
            long quanti = 0;
            var mediaR = (c1.R + (double)c2.R) / 2;
            var mediaG = (c1.G + (double)c2.G) / 2;
            var mediaB = (c1.B + (double)c2.B) / 2;
            var ampiezza = pMax - pMin;

            for (var y = 0; y < altezza; y += passo)
            {
                var riga = y * larghezza;
                for (var x = 0; x < larghezza; x += passo)
                {
                    if (indici[riga + x] != tinta) continue;
                    if (!Interno(riga + x, x, y)) continue;

                    var u = (x * dx + y * dy - pMin) / ampiezza;
                    if (u < 0) u = 0; else if (u > 1) u = 1;
                    var p = (riga + x) * 3;

                    errPiatto += Math.Abs(rgb[p] - mediaR) + Math.Abs(rgb[p + 1] - mediaG)
                               + Math.Abs(rgb[p + 2] - mediaB);
                    errRampa += Math.Abs(rgb[p] - (c1.R + (c2.R - c1.R) * u))
                              + Math.Abs(rgb[p + 1] - (c1.G + (c2.G - c1.G) * u))
                              + Math.Abs(rgb[p + 2] - (c1.B + (c2.B - c1.B) * u));
                    quanti++;
                }
            }
            if (quanti == 0 || errPiatto <= 0) return null;
            if (errRampa > errPiatto * QuotaDaSpiegare) return null;

            return new Rampa
            {
                X0 = mx + dx * (pMin - (mx * dx + my * dy)),
                Y0 = my + dy * (pMin - (mx * dx + my * dy)),
                X1 = mx + dx * (pMax - (mx * dx + my * dy)),
                Y1 = my + dy * (pMax - (mx * dx + my * dy)),
                Inizio = c1,
                Fine = c2,
                Salto = salto,
            };
        }

        private static byte Byte(double v)
        {
            var i = (int)Math.Round(v);
            return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }

        /// <summary>Le rampe di tutte le tinte, nell'ordine della tavolozza. Null dove e' piatta.</summary>
        public static Rampa?[] StimaTutte(byte[] rgb, Tavolozza.Esito tavolozza, int larghezza, int altezza)
        {
            var fuori = new Rampa?[tavolozza.Colori.Length];
            for (var i = 0; i < tavolozza.Colori.Length; i++)
                fuori[i] = Stima(rgb, tavolozza.Indici, tavolozza.SuBordo, larghezza, altezza, i);
            return fuori;
        }
    }
}

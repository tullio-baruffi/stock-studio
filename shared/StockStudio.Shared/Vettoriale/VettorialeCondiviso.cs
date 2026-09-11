using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Scrive SVG ed EPS a partire dai confini condivisi di <see cref="Contorni"/>.
    ///
    /// ## Cosa cambia rispetto alla composizione dei tracciati di potrace
    /// Li' si rimettevano insieme dei pezzi gia' fatti, ognuno con la sua trasformazione e le sue
    /// misure, e il lavoro era ricucirli. Qui il modello e' gia' completo e in **pixel
    /// dell'immagine**: una zona e' la sequenza dei suoi archi, e un arco e' una sequenza di
    /// cubiche. Non c'e' niente da convertire per l'SVG, che usa lo stesso sistema di coordinate;
    /// per l'EPS basta ribaltare la verticale e passare ai punti tipografici.
    ///
    /// Ne segue anche che due tinte affacciate scrivono **gli stessi numeri** lungo il confine che
    /// dividono, perche' li leggono dallo stesso arco: i bordi combaciano al bit, non entro una
    /// tolleranza.
    /// </summary>
    public static class VettorialeCondiviso
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Una tinta con la sua eventuale sfumatura.</summary>
        public class Tinta
        {
            public Colore Colore;
            /// <summary>La sfumatura di questa zona, o null quando e' a tinta piatta.</summary>
            public Rampa? Rampa;
        }

        /// <summary>
        /// L'SVG. Ogni tinta e' un gruppo con un nome che dice il colore, cosi' in Illustrator si
        /// seleziona una campitura intera con un clic -- che e' la ragione per cui si vettorializza
        /// a colori invece di esportare un raster.
        /// </summary>
        public static string? ComponiSvg(Contorni.Esito contorni, IList<Tinta> tinte,
                                         int larghezza, int altezza)
        {
            if (contorni == null || tinte == null || contorni.Zone.Length == 0) return null;

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" standalone=\"no\"?>\n");
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\"\n");
            sb.Append(" width=\"").Append(larghezza).Append("\" height=\"").Append(altezza)
              .Append("\" viewBox=\"0 0 ").Append(larghezza).Append(' ').Append(altezza).Append("\"\n");
            sb.Append(" preserveAspectRatio=\"xMidYMid meet\">\n");
            sb.Append("<metadata>Stock Vector Studio - tracciato a colori a confini condivisi</metadata>\n");

            var defs = new StringBuilder();
            for (var i = 0; i < tinte.Count && i < contorni.Zone.Length; i++)
            {
                var r = tinte[i].Rampa;
                if (r == null || contorni.Zone[i].Count == 0) continue;
                // Le coordinate sono gia' quelle dell'immagine: con gradientUnits="userSpaceOnUse"
                // il gradiente vive nello stesso spazio dei tracciati, e non serve convertirlo.
                defs.Append("<linearGradient id=\"sfumatura").Append(i)
                    .Append("\" gradientUnits=\"userSpaceOnUse\"")
                    .Append(" x1=\"").Append(N(r.X0)).Append("\" y1=\"").Append(N(r.Y0))
                    .Append("\" x2=\"").Append(N(r.X1)).Append("\" y2=\"").Append(N(r.Y1)).Append("\">\n")
                    .Append("<stop offset=\"0\" stop-color=\"").Append(r.Inizio.Esadecimale).Append("\"/>\n")
                    .Append("<stop offset=\"1\" stop-color=\"").Append(r.Fine.Esadecimale).Append("\"/>\n")
                    .Append("</linearGradient>\n");
            }
            if (defs.Length > 0) sb.Append("<defs>\n").Append(defs).Append("</defs>\n");

            var scritte = 0;
            for (var i = 0; i < tinte.Count && i < contorni.Zone.Length; i++)
            {
                var anelli = contorni.Zone[i];
                if (anelli.Count == 0) continue;

                var d = Percorso(contorni, anelli, false);
                if (d.Length == 0) continue;

                scritte++;
                var conRampa = tinte[i].Rampa != null;
                var nome = scritte.ToString("00", Inv) + "-" + (conRampa ? "sfumatura-" : "colore-")
                         + tinte[i].Colore.Esadecimale.TrimStart('#');
                var riempimento = conRampa ? "url(#sfumatura" + i + ")" : tinte[i].Colore.Esadecimale;

                sb.Append("<g id=\"").Append(nome).Append("\" data-name=\"").Append(nome)
                  .Append("\" fill=\"").Append(riempimento)
                  // I buchi si girano al contrario dei contorni esterni, e questa regola li vuota:
                  // e' cio' che rende un anello un anello invece di un disco.
                  .Append("\" fill-rule=\"nonzero\" stroke=\"none\">\n");
                sb.Append("<path d=\"").Append(d).Append("\"/>\n");
                sb.Append("</g>\n");
            }

            sb.Append("</svg>\n");
            return scritte == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// L'EPS, che Adobe Stock vuole al posto dell'SVG.
        ///
        /// PostScript conta la verticale dal basso e misura in punti, non in pixel: si ribalta la y
        /// e si scala di 72/96, cosi' la tavola in punti vale esattamente i pixel dell'immagine di
        /// partenza. Fatto questo, le coordinate si scrivono tali e quali a quelle dell'SVG.
        /// </summary>
        public static string? ComponiEps(Contorni.Esito contorni, IList<Tinta> tinte,
                                         int larghezza, int altezza)
        {
            if (contorni == null || tinte == null || contorni.Zone.Length == 0) return null;

            const double PuntiPerPixel = 72.0 / 96.0;
            var lp = larghezza * PuntiPerPixel;
            var ap = altezza * PuntiPerPixel;

            var conSfumature = false;
            for (var i = 0; i < tinte.Count && i < contorni.Zone.Length; i++)
                if (tinte[i].Rampa != null && contorni.Zone[i].Count > 0) conSfumature = true;

            var sb = new StringBuilder();
            sb.Append("%!PS-Adobe-3.0 EPSF-3.0\n");
            sb.Append("%%Creator: Stock Vector Studio (tracciato a colori a confini condivisi)\n");
            // Le sfumature sono un'ombreggiatura di livello 3: dichiararlo evita che un lettore
            // vecchio provi a interpretarle e disegni qualcosa di sbagliato senza avvisare.
            sb.Append(conSfumature ? "%%LanguageLevel: 3\n" : "%%LanguageLevel: 2\n");
            sb.Append("%%BoundingBox: 0 0 ").Append(Math.Ceiling(lp).ToString("0", Inv))
              .Append(' ').Append(Math.Ceiling(ap).ToString("0", Inv)).Append('\n');
            sb.Append("%%HiResBoundingBox: 0 0 ").Append(lp.ToString("0.000000", Inv))
              .Append(' ').Append(ap.ToString("0.000000", Inv)).Append('\n');
            sb.Append("%%Pages: 1\n%%EndComments\n%%Page: 1 1\n");
            sb.Append("gsave\n");
            sb.Append(PuntiPerPixel.ToString("0.000000", Inv)).Append(' ')
              .Append(PuntiPerPixel.ToString("0.000000", Inv)).Append(" scale\n");
            sb.Append("0 ").Append(altezza.ToString(Inv)).Append(" translate\n");
            sb.Append("1 -1 scale\n");

            var scritto = false;
            for (var i = 0; i < tinte.Count && i < contorni.Zone.Length; i++)
            {
                var anelli = contorni.Zone[i];
                if (anelli.Count == 0) continue;
                var corpo = Percorso(contorni, anelli, true);
                if (corpo.Length == 0) continue;
                scritto = true;

                var r = tinte[i].Rampa;
                if (r == null)
                {
                    sb.Append(Tre(tinte[i].Colore)).Append(" setrgbcolor\n");
                    sb.Append(corpo);
                    sb.Append("fill\n");
                    continue;
                }

                // Con una sfumatura il tracciato non si riempie: si usa come **ritaglio**, e dentro
                // quel ritaglio si stende un'ombreggiatura assiale. E' il modo con cui PostScript
                // esprime un gradiente, e l'unico che Illustrator riapra come tale.
                sb.Append("gsave\n");
                sb.Append(corpo);
                sb.Append("clip newpath\n");
                sb.Append("<< /ShadingType 2 /ColorSpace /DeviceRGB\n");
                sb.Append("   /Coords [").Append(N(r.X0)).Append(' ').Append(N(r.Y0)).Append(' ')
                  .Append(N(r.X1)).Append(' ').Append(N(r.Y1)).Append("]\n");
                // Estensione ai due capi: fuori dalla rampa il colore continua invece di sparire,
                // altrimenti le parti del ritaglio oltre gli estremi resterebbero vuote.
                sb.Append("   /Extend [true true]\n");
                sb.Append("   /Function << /FunctionType 2 /Domain [0 1] /N 1\n");
                sb.Append("      /C0 [").Append(Tre(r.Inizio)).Append("]\n");
                sb.Append("      /C1 [").Append(Tre(r.Fine)).Append("] >>\n");
                sb.Append(">> shfill\n");
                sb.Append("grestore\n");
            }

            sb.Append("grestore\n%%EOF\n");
            return scritto ? sb.ToString() : null;
        }

        /// <summary>
        /// Gli anelli di una tinta, scritti come un solo percorso.
        ///
        /// Ogni anello e' una sequenza di archi, e ogni arco si legge in avanti o all'indietro
        /// secondo come lo attraversa questa zona. La geometria non viene ricalcolata: e' la stessa
        /// che leggera' la zona di fronte.
        /// </summary>
        private static string Percorso(Contorni.Esito contorni, List<Contorni.Anello> anelli,
                                       bool postScript)
        {
            var sb = new StringBuilder();
            foreach (var anello in anelli)
            {
                if (anello.Passi.Count == 0) continue;

                var primo = contorni.Archi[anello.Passi[0].Arco];
                var partenza = anello.Passi[0].Inverso ? primo.Fine : primo.Inizio;

                if (postScript)
                    sb.Append("newpath\n").Append(N(partenza.X)).Append(' ')
                      .Append(N(partenza.Y)).Append(" moveto\n");
                else
                    sb.Append('M').Append(N(partenza.X)).Append(' ').Append(N(partenza.Y));

                foreach (var passo in anello.Passi)
                {
                    var arco = contorni.Archi[passo.Arco];
                    var n = arco.Cubiche.Count;
                    for (var k = 0; k < n; k++)
                    {
                        Punto c1, c2, fine;
                        if (!passo.Inverso)
                        {
                            var c = arco.Cubiche[k];
                            c1 = c[0]; c2 = c[1]; fine = c[2];
                        }
                        else
                        {
                            // All'indietro: le cubiche si leggono a rovescio, i due controlli si
                            // scambiano, e si arriva dove cominciava quella precedente.
                            var c = arco.Cubiche[n - 1 - k];
                            c1 = c[1]; c2 = c[0];
                            fine = n - 1 - k == 0 ? arco.Inizio : arco.Cubiche[n - 2 - k][2];
                        }

                        if (postScript)
                            sb.Append(N(c1.X)).Append(' ').Append(N(c1.Y)).Append(' ')
                              .Append(N(c2.X)).Append(' ').Append(N(c2.Y)).Append(' ')
                              .Append(N(fine.X)).Append(' ').Append(N(fine.Y)).Append(" curveto\n");
                        else
                            sb.Append('C').Append(N(c1.X)).Append(' ').Append(N(c1.Y)).Append(' ')
                              .Append(N(c2.X)).Append(' ').Append(N(c2.Y)).Append(' ')
                              .Append(N(fine.X)).Append(' ').Append(N(fine.Y));
                    }
                }

                sb.Append(postScript ? "closepath\n" : "Z");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Un decimale: un decimo di pixel su un lato da quattromila e' un quarantamillesimo del
        /// disegno, molto sotto il visibile anche ingrandendo dieci volte. Scriverne di piu'
        /// gonfierebbe il file senza che si veda niente -- misurato, il secondo decimale costa un
        /// sesto del peso.
        ///
        /// L'arrotondamento non riapre le sovrapposizioni: i due lati di un confine leggono gli
        /// **stessi** numeri dallo stesso arco, quindi li arrotondano allo stesso modo.
        /// </summary>
        private static string N(double v)
        {
            return v.ToString("0.#", Inv);
        }

        private static string Tre(Colore c)
        {
            return (c.R / 255.0).ToString("0.000", Inv) + " " +
                   (c.G / 255.0).ToString("0.000", Inv) + " " +
                   (c.B / 255.0).ToString("0.000", Inv);
        }
    }
}

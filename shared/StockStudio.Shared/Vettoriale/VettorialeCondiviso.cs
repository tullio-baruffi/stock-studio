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

                // Da dove parte il pezzo che si sta per scrivere: serve per sapere se e' dritto,
                // perche' una cubica e' una retta solo rispetto ai **suoi due estremi**, e il primo
                // dei due non sta dentro la cubica -- e' dove e' arrivata quella prima.
                var corrente = partenza;

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

                        if (EDritta(corrente, c1, c2, fine))
                        {
                            if (postScript)
                                sb.Append(N(fine.X)).Append(' ').Append(N(fine.Y)).Append(" lineto\n");
                            else
                                sb.Append('L').Append(N(fine.X)).Append(' ').Append(N(fine.Y));
                        }
                        else if (postScript)
                            sb.Append(N(c1.X)).Append(' ').Append(N(c1.Y)).Append(' ')
                              .Append(N(c2.X)).Append(' ').Append(N(c2.Y)).Append(' ')
                              .Append(N(fine.X)).Append(' ').Append(N(fine.Y)).Append(" curveto\n");
                        else
                            sb.Append('C').Append(N(c1.X)).Append(' ').Append(N(c1.Y)).Append(' ')
                              .Append(N(c2.X)).Append(' ').Append(N(c2.Y)).Append(' ')
                              .Append(N(fine.X)).Append(' ').Append(N(fine.Y));

                        corrente = fine;
                    }
                }

                sb.Append(postScript ? "closepath\n" : "Z");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Se questa cubica e' in realta' un segmento dritto, e conviene quindi scriverla come tale.
        ///
        /// ## Perche'
        /// Perche' una retta scritta come cubica costa **sei numeri invece di due**, e di rette ce
        /// ne sono tante: un disegno vettoriale e' pieno di bordi dritti, e il tracciato ne produce
        /// altri suoi -- ogni arco di un solo passo viene costruito proprio come una retta (vedi
        /// Contorni.Retta), con i due controlli messi a un terzo e a due terzi della corda.
        ///
        /// Misurato sull'illustrazione dei castori, Illustrator usa 670 segmenti dritti su 1584
        /// pezzi totali; noi ne usavamo **zero**, perche' scrivevamo tutto come cubica. Non e' una
        /// differenza di qualita' del disegno -- la forma e' identica -- ma di peso del file e di
        /// pulizia di quel che si apre in un programma di disegno: chi ci mette mano si aspetta di
        /// trovare un segmento dove il bordo e' dritto, non una curva con due maniglie da spostare.
        ///
        /// ## Perche' non cambia la forma
        /// Questa non e' una semplificazione: non si raddrizza niente. Si riconosce che la curva
        /// **gia' passa** per la retta -- i due controlli stanno sul segmento, entro un ventesimo
        /// di pixel -- e la si scrive nel modo corto. Chi disegna il file ottiene gli stessi pixel.
        ///
        /// La tolleranza e' volutamente molto sotto il mezzo pixel che i punti del contorno gia' si
        /// portano dietro: sotto quella misura non c'e' informazione sulla forma, c'e' la
        /// quantizzazione del reticolo. Vedi <see cref="ScartoDiRettitudine"/>.
        ///
        /// ## Le due prove
        /// Non basta che i controlli stiano **sulla retta**: devono anche stare all'incirca
        /// **dentro** il segmento. Due controlli allineati ma molto oltre gli estremi descrivono
        /// una curva che esce lontano, torna indietro e rientra: passa per la stessa retta, ma
        /// scriverla come un segmento cancellerebbe un tratto di percorso che c'era.
        ///
        /// Il margine e' pero' largo -- mezza lunghezza di corda oltre ciascun estremo -- perche'
        /// un rientro breve lungo la stessa retta non cambia un riempimento: un'escursione su una
        /// retta ha area nulla, e queste campiture si riempiono, non si contornano. Il caso si
        /// presenta davvero agli angoli della tavola, dove l'adattamento accorcia le tangenti e
        /// produce cubiche come "da 116 a 120 con un controllo a 114,7": tutta sulla retta, con un
        /// arretramento di un terzo di corda. Rifiutarle vorrebbe dire scrivere come curve i
        /// quattro angoli di ogni immagine.
        /// </summary>
        private static bool EDritta(Punto da, Punto c1, Punto c2, Punto a)
        {
            var dx = a.X - da.X;
            var dy = a.Y - da.Y;
            var l2 = dx * dx + dy * dy;
            // Una cubica che finisce dove comincia e' un cappio: dritta non e', e dividere per la
            // sua lunghezza darebbe infinito.
            if (l2 < 1e-12) return false;

            return SulSegmento(da, c1, dx, dy, l2) && SulSegmento(da, c2, dx, dy, l2);
        }

        /// <summary>Se un punto di controllo sta sul segmento, e non solo sulla sua retta.</summary>
        private static bool SulSegmento(Punto da, Punto c, double dx, double dy, double l2)
        {
            var px = c.X - da.X;
            var py = c.Y - da.Y;

            // Quanto e' lontano dalla retta: il prodotto vettoriale diviso la lunghezza.
            var fuori = Math.Abs(px * dy - py * dx) / Math.Sqrt(l2);
            if (fuori > ScartoDiRettitudine) return false;

            // Dove cade lungo il segmento. Il margine e' largo apposta: vedi EDritta.
            var t = (px * dx + py * dy) / l2;
            return t >= -0.5 && t <= 1.5;
        }

        /// <summary>
        /// Di quanto un punto di controllo puo' scostarsi dalla corda e contare ancora come dritto.
        ///
        /// ## Perche' non un valore quasi nullo
        /// Perche' con un valore quasi nullo si riconoscono solo le rette **costruite come tali**
        /// -- gli archi di un passo solo -- e non quelle che il disegno ha davvero. La lisciatura
        /// di Taubin sposta ogni vertice di una frazione di pixel, quindi un bordo dritto esce
        /// dal tracciato leggermente incurvato: dritto all'occhio, curvo all'aritmetica.
        ///
        /// ## Perche' proprio un sesto di pixel
        /// Perche' e' molto sotto il mezzo pixel che i punti gia' si portano dietro. I contorni si
        /// misurano sugli spigoli interi del reticolo: ogni punto e' gia' arrotondato a mezzo
        /// pixel, e sotto quella misura non c'e' informazione sulla forma, c'e' la quantizzazione.
        ///
        /// Misurato sull'illustrazione della balena, a parita' di disegno:
        ///     0,05 -> 332 KB,  1818 segmenti dritti,  0,02% dei pixel toccati
        ///     0,15 -> 260 KB,  4940 segmenti dritti,  0,12% dei pixel toccati
        ///     0,25 -> 228 KB,  6316 segmenti dritti,  0,25% dei pixel toccati
        ///     0,40 -> 201 KB,  7508 segmenti dritti,  0,45% dei pixel toccati
        /// I pixel toccati sono tutti sul filo di un bordo, e a schermo le quattro versioni non si
        /// distinguono. Si sceglie la piu' prudente fra quelle che danno un guadagno vero: un
        /// quinto del peso in meno per un pixel su ottocento, tutti di antialiasing.
        /// </summary>
        private const double ScartoDiRettitudine = 0.15;

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

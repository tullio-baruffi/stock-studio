using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>Il rettangolo che contiene una figura, nelle coordinate del disegno.</summary>
    public struct Riquadro
    {
        public double X0 { get; private set; }
        public double Y0 { get; private set; }
        public double X1 { get; private set; }
        public double Y1 { get; private set; }
        public bool Valido { get; private set; }

        public Riquadro(double x0, double y0, double x1, double y1, bool valido)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; Valido = valido;
        }

        public static readonly Riquadro Vuoto = new Riquadro(0, 0, 0, 0, false);

        public double Larghezza { get { return X1 - X0; } }
        public double Altezza { get { return Y1 - Y0; } }
        public double Area { get { return Math.Max(0, Larghezza) * Math.Max(0, Altezza); } }

        public Riquadro Unione(Riquadro a)
        {
            if (!a.Valido) return this;
            if (!Valido) return a;
            return new Riquadro(Math.Min(X0, a.X0), Math.Min(Y0, a.Y0),
                                Math.Max(X1, a.X1), Math.Max(Y1, a.Y1), true);
        }

        /// <summary>Vero se i due riquadri si toccano, o distano meno del margine dato.</summary>
        public bool Vicino(Riquadro a, double margine)
        {
            return X0 - margine <= a.X1 && a.X0 - margine <= X1
                && Y0 - margine <= a.Y1 && a.Y0 - margine <= Y1;
        }

        /// <summary>
        /// Il riquadro di un tracciato SVG, letto scorrendo i comandi.
        ///
        /// potrace usa soltanto M/m (spostati), c/C (curva), l/L (linea) e z (chiudi) -- misurato
        /// sui file che produce -- ma qui si accettano anche h/v per non rompersi se un domani ne
        /// aggiungesse. Delle curve si prendono anche i punti di controllo: il riquadro esce un
        /// filo piu' largo della curva vera, e per decidere se due figure si toccano e' un errore
        /// dalla parte giusta.
        /// </summary>
        public static Riquadro Di(string d)
        {
            double x = 0, y = 0;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            var numeri = new List<double>(8);
            var comando = '\0';
            var i = 0;

            Action<double, double> punto = (px, py) =>
            {
                if (px < minX) minX = px;
                if (py < minY) minY = py;
                if (px > maxX) maxX = px;
                if (py > maxY) maxY = py;
            };

            Action chiudi = () =>
            {
                if (numeri.Count == 0) return;
                var relativo = char.IsLower(comando);
                switch (char.ToLowerInvariant(comando))
                {
                    case 'm':
                    case 'l':
                        for (var k = 0; k + 1 < numeri.Count; k += 2)
                        {
                            x = relativo ? x + numeri[k] : numeri[k];
                            y = relativo ? y + numeri[k + 1] : numeri[k + 1];
                            punto(x, y);
                        }
                        break;
                    case 'c':
                        for (var k = 0; k + 5 < numeri.Count; k += 6)
                        {
                            var bx = relativo ? x : 0;
                            var by = relativo ? y : 0;
                            punto(bx + numeri[k], by + numeri[k + 1]);
                            punto(bx + numeri[k + 2], by + numeri[k + 3]);
                            x = bx + numeri[k + 4];
                            y = by + numeri[k + 5];
                            punto(x, y);
                        }
                        break;
                    case 'h':
                        foreach (var n in numeri) { x = relativo ? x + n : n; punto(x, y); }
                        break;
                    case 'v':
                        foreach (var n in numeri) { y = relativo ? y + n : n; punto(x, y); }
                        break;
                }
                numeri.Clear();
            };

            while (i < d.Length)
            {
                var c = d[i];
                if (char.IsLetter(c))
                {
                    chiudi();
                    comando = c;
                    i++;
                    continue;
                }
                if (c == ' ' || c == ',' || c == '\n' || c == '\r' || c == '\t') { i++; continue; }

                var inizio = i;
                if (d[i] == '-' || d[i] == '+') i++;
                while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.')) i++;
                if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
                {
                    i++;
                    if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
                    while (i < d.Length && char.IsDigit(d[i])) i++;
                }
                if (i == inizio) { i++; continue; }

                double n2;
                if (double.TryParse(d.Substring(inizio, i - inizio), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out n2))
                    numeri.Add(n2);
            }
            chiudi();

            if (maxX < minX) return Vuoto;
            return new Riquadro(minX, minY, maxX, maxY, true);
        }
    }

    /// <summary>
    /// La trasformazione del gruppo esterno di potrace: translate(tx,ty) scale(sx,sy).
    ///
    /// Serve solo a riportare i riquadri nelle coordinate del disegno, perche' "in alto" voglia
    /// dire in alto: la scala verticale di potrace e' negativa, e senza applicarla i nomi
    /// uscirebbero capovolti.
    /// </summary>
    public struct Trasformazione
    {
        public double Tx { get; private set; }
        public double Ty { get; private set; }
        public double Sx { get; private set; }
        public double Sy { get; private set; }

        private static readonly Regex Sposta =
            new Regex(@"translate\(\s*([-\d.eE+]+)[\s,]+([-\d.eE+]+)\s*\)", RegexOptions.Compiled);
        private static readonly Regex Scala =
            new Regex(@"scale\(\s*([-\d.eE+]+)(?:[\s,]+([-\d.eE+]+))?\s*\)", RegexOptions.Compiled);

        public Trasformazione(double tx, double ty, double sx, double sy)
        {
            Tx = tx; Ty = ty; Sx = sx; Sy = sy;
        }

        public static Trasformazione Leggi(string transform)
        {
            double tx = 0, ty = 0, sx = 1, sy = 1;
            if (!string.IsNullOrEmpty(transform))
            {
                var t = Sposta.Match(transform);
                if (t.Success)
                {
                    double.TryParse(t.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tx);
                    double.TryParse(t.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ty);
                }
                var s = Scala.Match(transform);
                if (s.Success)
                {
                    double.TryParse(s.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out sx);
                    double v;
                    sy = s.Groups[2].Success &&
                         double.TryParse(s.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                         ? v : sx;
                }
            }
            return new Trasformazione(tx, ty, sx, sy);
        }

        public Riquadro Applica(Riquadro r)
        {
            if (!r.Valido) return r;
            double ax = Tx + r.X0 * Sx, bx = Tx + r.X1 * Sx;
            double ay = Ty + r.Y0 * Sy, by = Ty + r.Y1 * Sy;
            return new Riquadro(Math.Min(ax, bx), Math.Min(ay, by),
                                Math.Max(ax, bx), Math.Max(ay, by), true);
        }
    }
}

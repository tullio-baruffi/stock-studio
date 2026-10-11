using System;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Lo spazio in cui si misura quanto due colori sono lontani.
    ///
    /// ## Il difetto che risolve
    /// Fin qui ogni distanza fra colori era la distanza euclidea fra le terne RGB: `dr*dr + dg*dg
    /// + db*db`. E' la misura piu' ovvia e non e' la misura giusta, perche' in RGB spostamenti
    /// uguali **non si vedono uguali**. Il verde occupa una fetta di numeri molto piu' larga di
    /// quanto l'occhio gli riconosca, e i blu scuri e i marroni una molto piu' stretta. Ne segue
    /// che il taglio della tavolozza spende tinte a distinguere verdi che nessuno distingue, e ne
    /// risparmia sui marroni, dove invece si vedono le bande.
    ///
    /// E' esattamente il caso patologico descritto in <see cref="Tavolozza"/>: due marroni a
    /// distanza 459 che l'occhio legge come lo stesso colore, mentre la stessa distanza fra due
    /// verdi separa due tinte legittime. Nessuna soglia unica su RGB puo' prendere entrambi i
    /// casi, e infatti a valle sono nate euristiche di fusione per rimediare.
    ///
    /// ## La misura giusta
    /// Oklab (Björn Ottosson, 2020) e' costruito perche' la distanza euclidea al suo interno
    /// corrisponda alla differenza percepita. E' lo spazio che usano i due soli progetti open
    /// source con la nostra stessa architettura a confini condivisi -- Vecto e Trazor -- entrambi
    /// con k-means in Oklab.
    ///
    /// ## Perche' le distanze restano confrontabili con quelle di prima
    /// Tutte le soglie del motore sono tarate in **livelli per canale**: la soglia delle gemelle
    /// vale `soglia * soglia * 3`, cioe' la distanza quadrata fra due grigi che differiscono di
    /// `soglia` livelli su ciascun canale. Se cambiando spazio cambiasse anche la scala, ogni
    /// numero misurato negli anni andrebbe ritarato da capo.
    ///
    /// Qui le coordinate Oklab vengono percio' moltiplicate per <see cref="SpazioColore.Scala"/>
    /// = 255*sqrt(3). Con quel fattore il nero e il bianco distano 441,67 in entrambi gli spazi,
    /// che e' proprio `sqrt(255*255*3)`: lungo l'asse dei grigi la nuova misura vale quanto la
    /// vecchia, e le soglie conservano il significato per cui erano state scelte.
    ///
    /// ## Il limite di quella corrispondenza, misurato
    /// La scala coincide **sull'asse dei grigi e basta**. Nella direzione della tinta no: RGB
    /// esagera le differenze di colore saturo, e le stesse coppie che in RGB stavano sopra la
    /// soglia, in Oklab ci finiscono sotto. Misurato sul leone: con la stessa soglia di 10,5 la
    /// fusione delle gemelle passa da 24 tinte a 21 in RGB e da 24 a 13 in Oklab. Non e' un
    /// difetto della conversione, e' la non uniformita' di RGB che si manifesta -- ma vuol dire
    /// che una soglia tarata in RGB **non** si trasporta cosi' com'e', e che un confronto fra le
    /// due misure a soglia uguale confronta due aggressivita' diverse, non due spazi.
    ///
    /// Vedi <see cref="ParametriTracciato.MetricaPercettiva"/> per come e' andata a finire la
    /// prova, ritarando la soglia per rendere il confronto onesto.
    /// </summary>
    public abstract class MetricaColore
    {
        /// <summary>La misura di sempre: distanza euclidea sulle terne RGB.</summary>
        public static readonly MetricaColore Rgb = new MetricaRgb();

        /// <summary>La misura percettiva: distanza euclidea in Oklab riportato alla scala RGB.</summary>
        public static readonly MetricaColore Oklab = new MetricaOklab();

        /// <summary>Come si chiama, per poterlo scrivere nel registro del banco.</summary>
        public abstract string Nome { get; }

        /// <summary>
        /// Porta un colore nello spazio in cui si misura. I tre valori in ingresso sono livelli
        /// RGB da 0 a 255, anche non interi: il taglio mediano e Lloyd lavorano su medie.
        /// </summary>
        public abstract void Porta(double r, double g, double b,
                                   out double x, out double y, out double z);

        /// <summary>
        /// Riporta a RGB un punto dello spazio di misura. Serve a Lloyd: il centro di una nuvola
        /// di colori va calcolato **nello spazio in cui si misura**, altrimenti la media non e' il
        /// punto che minimizza le distanze che si stanno usando.
        /// </summary>
        public abstract void Riporta(double x, double y, double z,
                                     out double r, out double g, out double b);

        /// <summary>Quanto distano due colori, al quadrato.</summary>
        public double Distanza2(double r1, double g1, double b1, double r2, double g2, double b2)
        {
            double x1, y1, z1, x2, y2, z2;
            Porta(r1, g1, b1, out x1, out y1, out z1);
            Porta(r2, g2, b2, out x2, out y2, out z2);
            var dx = x1 - x2;
            var dy = y1 - y2;
            var dz = z1 - z2;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// La misura richiesta. Il valore predefinito resta <see cref="Rgb"/>: finche' il banco non
        /// dice il contrario, la produzione non cambia comportamento.
        /// </summary>
        public static MetricaColore Scegli(bool percettiva)
        {
            return percettiva ? Oklab : Rgb;
        }

        private sealed class MetricaRgb : MetricaColore
        {
            public override string Nome { get { return "rgb"; } }

            // L'identita', scritta per esteso: cosi' chi passa di qui vede che con questa metrica
            // il motore fa **esattamente** i conti di prima, senza arrotondamenti intermedi.
            public override void Porta(double r, double g, double b,
                                       out double x, out double y, out double z)
            {
                x = r; y = g; z = b;
            }

            public override void Riporta(double x, double y, double z,
                                         out double r, out double g, out double b)
            {
                r = x; g = y; b = z;
            }
        }

        private sealed class MetricaOklab : MetricaColore
        {
            public override string Nome { get { return "oklab"; } }

            public override void Porta(double r, double g, double b,
                                       out double x, out double y, out double z)
            {
                SpazioColore.VersoOklab(r, g, b, out x, out y, out z);
            }

            public override void Riporta(double x, double y, double z,
                                         out double r, out double g, out double b)
            {
                SpazioColore.DaOklab(x, y, z, out r, out g, out b);
            }
        }
    }

    /// <summary>
    /// La conversione fra sRGB e Oklab, secondo la formulazione di Björn Ottosson.
    ///
    /// Le coordinate Oklab escono **moltiplicate per <see cref="Scala"/>**: vedi
    /// <see cref="MetricaColore"/> per il motivo: serve a non dover ritarare le soglie del motore.
    /// </summary>
    public static class SpazioColore
    {
        /// <summary>
        /// Il fattore che rende le distanze Oklab confrontabili con quelle RGB.
        ///
        /// 255*sqrt(3) e' la distanza RGB fra il nero e il bianco. Moltiplicando per questo le
        /// coordinate Oklab -- dove il nero e il bianco distano 1 -- i due estremi distano lo
        /// stesso numero nei due spazi, e ogni soglia gia' misurata continua a voler dire quel
        /// che voleva dire.
        /// </summary>
        public const double Scala = 441.6729559300637;

        /// <summary>
        /// Da livello sRGB a valore lineare. E' una tabella perche' la si consulta milioni di
        /// volte e la formula ha una potenza dentro: i livelli sono 256, tanto vale saperli tutti.
        /// </summary>
        private static readonly double[] Lineare = CostruisciLineare();

        private static double[] CostruisciLineare()
        {
            var t = new double[256];
            for (var i = 0; i < 256; i++)
            {
                var c = i / 255.0;
                t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return t;
        }

        /// <summary>
        /// Da livello sRGB a valore lineare, accettando anche livelli non interi.
        ///
        /// Il taglio mediano e Lloyd producono medie, non livelli: interpolare fra le due caselle
        /// vicine costa una moltiplicazione e evita di arrotondare prima di misurare, che su una
        /// tavolozza da otto tinte si vedrebbe.
        /// </summary>
        private static double VersoLineare(double livello)
        {
            if (livello <= 0) return Lineare[0];
            if (livello >= 255) return Lineare[255];
            var i = (int)livello;
            var f = livello - i;
            // i vale al massimo 254 qui, quindi i+1 e' sempre dentro la tabella.
            return Lineare[i] + (Lineare[i + 1] - Lineare[i]) * f;
        }

        /// <summary>
        /// La radice cubica. Scritta a mano perche' la libreria condivisa e' netstandard2.0, dove
        /// <c>Math.Cbrt</c> non esiste: c'e' solo da .NET Core 2.1 in su, e questa libreria la
        /// usano anche le Function su net6.0-windows attraverso lo stesso binario.
        /// </summary>
        private static double RadiceCubica(double v)
        {
            if (v >= 0) return Math.Pow(v, 1.0 / 3.0);
            return -Math.Pow(-v, 1.0 / 3.0);
        }

        /// <summary>Da livelli sRGB (0..255) a Oklab moltiplicato per <see cref="Scala"/>.</summary>
        public static void VersoOklab(double r, double g, double b,
                                      out double lOut, out double aOut, out double bOut)
        {
            var rl = VersoLineare(r);
            var gl = VersoLineare(g);
            var bl = VersoLineare(b);

            var l = 0.4122214708 * rl + 0.5363325363 * gl + 0.0514459929 * bl;
            var m = 0.2119034982 * rl + 0.6806995451 * gl + 0.1073969566 * bl;
            var s = 0.0883024619 * rl + 0.2817188376 * gl + 0.6299787005 * bl;

            var lr = RadiceCubica(l);
            var mr = RadiceCubica(m);
            var sr = RadiceCubica(s);

            lOut = (0.2104542553 * lr + 0.7936177850 * mr - 0.0040720468 * sr) * Scala;
            aOut = (1.9779984951 * lr - 2.4285922050 * mr + 0.4505937099 * sr) * Scala;
            bOut = (0.0259040371 * lr + 0.7827717662 * mr - 0.8086757660 * sr) * Scala;
        }

        /// <summary>
        /// Da Oklab moltiplicato per <see cref="Scala"/> a livelli sRGB (0..255).
        ///
        /// Il risultato viene riportato dentro l'intervallo: il centro di una nuvola di colori
        /// puo' cadere fuori dal cubo sRGB, e li' non c'e' nessun colore da mostrare.
        /// </summary>
        public static void DaOklab(double lIn, double aIn, double bIn,
                                   out double r, out double g, out double b)
        {
            var L = lIn / Scala;
            var A = aIn / Scala;
            var B = bIn / Scala;

            var lr = L + 0.3963377774 * A + 0.2158037573 * B;
            var mr = L - 0.1055613458 * A - 0.0638541728 * B;
            var sr = L - 0.0894841775 * A - 1.2914855480 * B;

            var l = lr * lr * lr;
            var m = mr * mr * mr;
            var s = sr * sr * sr;

            var rl = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            var gl = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            var bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

            r = DaLineare(rl);
            g = DaLineare(gl);
            b = DaLineare(bl);
        }

        /// <summary>Da valore lineare a livello sRGB, riportato dentro 0..255.</summary>
        private static double DaLineare(double v)
        {
            if (v <= 0) return 0;
            if (v >= 1) return 255;
            var c = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
            var livello = c * 255.0;
            return livello < 0 ? 0 : livello > 255 ? 255 : livello;
        }
    }
}

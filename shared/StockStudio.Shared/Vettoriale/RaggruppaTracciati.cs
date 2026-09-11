using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace StockStudio.Shared.Vettoriale
{
    /// <summary>
    /// Divide i tracciati di un SVG in gruppi, uno per figura riconoscibile.
    ///
    /// ## Il problema
    /// potrace emette tutti i tracciati dentro un solo &lt;g&gt;: aprendo il file in Illustrator si
    /// trova un blocco unico e indistinto, e per toccare una parte sola bisogna selezionarla a mano
    /// pezzo per pezzo. Su una silhouette da qualche centinaio di tracciati non e' lavoro, e'
    /// pazienza.
    ///
    /// ## Cosa si puo' sapere davvero
    /// Qui non c'e' nessun modello che guardi l'immagine, quindi **non si puo' sapere che una
    /// figura e' un cane**: scriverlo sarebbe inventarlo. Si puo' pero' sapere una cosa vera e
    /// quasi altrettanto utile: quali tracciati appartengono alla stessa figura. Due tracciati che
    /// si toccano o si sovrappongono fanno parte della stessa cosa -- il tronco e la chioma di un
    /// albero, il corpo e la coda di un animale -- mentre due tracciati lontani sono cose diverse.
    /// E' una proprieta' geometrica: si calcola senza indovinare, e produce esattamente i gruppi
    /// che servono per lavorare.
    ///
    /// I nomi dicono quindi dove sta la figura e quanto e' grande, non cosa raffigura. Chi apre il
    /// file legge "02-in-alto-a-destra" e la riconosce a colpo d'occhio; leggerebbe "02-farfalla"
    /// soltanto se qualcuno l'avesse davvero riconosciuta, e nessuno l'ha fatto.
    /// </summary>
    public static class RaggruppaTracciati
    {
        /// <summary>
        /// Oltre questo numero di gruppi la divisione smette di aiutare e comincia a ingombrare:
        /// i piu' piccoli finiscono insieme in un gruppo solo, ordinato in fondo.
        /// </summary>
        private const int GruppiMax = 40;

        /// <summary>
        /// Quanto possono distare due tracciati per dirsi ancora la stessa figura, in frazione
        /// della diagonale del disegno. Non zero: il tracciato di un occhio non tocca quello della
        /// testa, ma nessuno direbbe che sono due figure. Non troppo: con un valore alto tutto si
        /// fonde in un blocco unico e il raggruppamento non serve piu' a niente.
        /// </summary>
        private const double VicinanzaRelativa = 0.015;

        /// <summary>
        /// Sotto questa frazione dell'area del disegno una figura non merita un gruppo suo: e' un
        /// granello. Senza questa regola una silhouette con trenta puntini sparsi produce trenta
        /// gruppi, che e' tecnicamente corretto e praticamente inservibile -- misurato su una scena
        /// di prova: 21 gruppi, di cui 19 di puntini. Finiscono tutti insieme in "dettagli-minori",
        /// tranne quelli abbastanza vicini a una figura vera, che sono gia' stati assorbiti da
        /// quella nel passaggio precedente.
        /// </summary>
        private const double AreaMinimaRelativa = 0.005;

        private static readonly Regex ApreGruppo =
            new Regex("<g\\s+transform=\"(?<tr>[^\"]*)\"[^>]*>", RegexOptions.Compiled);

        private static readonly Regex Tracciato =
            new Regex("<path[^>]*\\bd=\"(?<d>[^\"]*)\"[^>]*?/?>", RegexOptions.Compiled);

        /// <summary>
        /// Restituisce l'SVG con i tracciati divisi in gruppi, oppure null se non c'e' niente da
        /// fare: un tracciato solo, un file che non ha la forma prodotta da potrace, o un disegno
        /// in cui tutto si tocca e un gruppo unico sarebbe identico all'originale.
        ///
        /// Null e non un'eccezione: chi chiama tiene il file com'e', che e' valido.
        /// </summary>
        public static string? Dividi(string svg)
        {
            if (string.IsNullOrEmpty(svg)) return null;

            var apertura = ApreGruppo.Match(svg);
            if (!apertura.Success) return null;

            var chiusura = svg.LastIndexOf("</g>", StringComparison.Ordinal);
            if (chiusura <= apertura.Index + apertura.Length) return null;

            var inizioCorpo = apertura.Index + apertura.Length;
            var corpo = svg.Substring(inizioCorpo, chiusura - inizioCorpo);

            var trasformazione = Trasformazione.Leggi(apertura.Groups["tr"].Value);
            var figure = new List<Figura>();
            foreach (Match m in Tracciato.Matches(corpo))
            {
                var d = m.Groups["d"].Value;
                if (d.Length == 0) continue;
                var r = trasformazione.Applica(Riquadro.Di(d));
                if (r.Valido) figure.Add(new Figura(m.Value, r));
            }
            if (figure.Count < 2) return null;

            var gruppi = Unisci(figure);
            if (gruppi.Count < 2) return null;

            return Ricompone(svg, inizioCorpo, chiusura, gruppi);
        }

        /// <summary>
        /// Mette insieme le figure che si toccano, con l'unione per insiemi disgiunti.
        ///
        /// Il confronto e' fra riquadri e non fra curve: due riquadri che si sovrappongono non
        /// provano che le curve si tocchino davvero. E' un'approssimazione voluta -- costa un
        /// confronto fra rettangoli invece di intersezioni fra migliaia di curve, e sbaglia sempre
        /// dalla parte giusta, cioe' unendo un po' piu' del necessario invece di spezzare una
        /// figura in pezzi.
        /// </summary>
        private static List<Gruppo> Unisci(List<Figura> figure)
        {
            var tutto = figure[0].Riquadro;
            for (var i = 1; i < figure.Count; i++) tutto = tutto.Unione(figure[i].Riquadro);

            var margine = Math.Sqrt(tutto.Larghezza * tutto.Larghezza + tutto.Altezza * tutto.Altezza)
                          * VicinanzaRelativa;

            var padre = new int[figure.Count];
            for (var i = 0; i < padre.Length; i++) padre[i] = i;

            // Compressione di cammino: senza, su un disegno con migliaia di tracciati in fila la
            // ricerca della radice degenera in una catena.
            int Radice(int i)
            {
                while (padre[i] != i) { padre[i] = padre[padre[i]]; i = padre[i]; }
                return i;
            }

            for (var i = 0; i < figure.Count; i++)
                for (var j = i + 1; j < figure.Count; j++)
                    if (figure[i].Riquadro.Vicino(figure[j].Riquadro, margine))
                    {
                        int a = Radice(i), b = Radice(j);
                        if (a != b) padre[a] = b;
                    }

            var per = new Dictionary<int, Gruppo>();
            for (var i = 0; i < figure.Count; i++)
            {
                var r = Radice(i);
                Gruppo g;
                if (!per.TryGetValue(r, out g)) { g = new Gruppo(); per[r] = g; }
                g.Aggiungi(figure[i]);
            }

            // Le figure grandi per prime: chi apre il file trova in cima quel che conta, e il
            // numero nel nome tiene l'ordine anche dove i gruppi vengono riordinati per nome.
            var ordinati = per.Values.OrderByDescending(g => g.Riquadro.Area).ToList();

            // I granelli non diventano gruppi: diventano un gruppo solo, in fondo. Si separano
            // per area e non per numero di tracciati, perche' quel che rende una figura degna di
            // un gruppo e' quanto occupa, non di quanti pezzi e' fatta.
            var soglia = tutto.Area * AreaMinimaRelativa;
            var vere = ordinati.Where(g => g.Riquadro.Area >= soglia).ToList();
            var granelli = ordinati.Where(g => g.Riquadro.Area < soglia).ToList();

            // Se fosse tutto sotto soglia vorrebbe dire che la soglia non ha senso su questo
            // disegno -- tanti pezzi tutti piccoli, nessuno dominante -- e allora si torna a
            // trattarli come figure, che e' meglio di un gruppo unico chiamato "dettagli".
            if (vere.Count == 0) { vere = ordinati; granelli.Clear(); }

            if (granelli.Count > 0)
            {
                var minori = new Gruppo();
                foreach (var g in granelli)
                    foreach (var f in g.Figure) minori.Aggiungi(f);
                minori.Minuti = true;
                vere.Add(minori);
            }
            ordinati = vere;

            if (ordinati.Count > GruppiMax)
            {
                var tenuti = ordinati.Take(GruppiMax - 1).ToList();
                var resto = new Gruppo();
                foreach (var g in ordinati.Skip(GruppiMax - 1))
                    foreach (var f in g.Figure) resto.Aggiungi(f);
                resto.Minuti = true;
                tenuti.Add(resto);
                ordinati = tenuti;
            }

            Nomina(ordinati, tutto);
            return ordinati;
        }

        private static void Nomina(List<Gruppo> gruppi, Riquadro tutto)
        {
            for (var i = 0; i < gruppi.Count; i++)
            {
                var g = gruppi[i];
                string etichetta;
                if (g.Minuti) etichetta = "dettagli-minori-" + g.Figure.Count;
                else if (i == 0 && gruppi.Count > 1 && g.Riquadro.Area > gruppi[1].Riquadro.Area * 1.6)
                    etichetta = "soggetto-principale";
                else etichetta = Posizione(g.Riquadro, tutto);

                g.Nome = (i + 1).ToString("00", CultureInfo.InvariantCulture) + "-" + etichetta;
            }
        }

        /// <summary>Dove sta la figura, detto come lo direbbe chi guarda il foglio.</summary>
        private static string Posizione(Riquadro r, Riquadro tutto)
        {
            var cx = (r.X0 + r.X1) / 2;
            var cy = (r.Y0 + r.Y1) / 2;
            var fx = tutto.Larghezza > 0 ? (cx - tutto.X0) / tutto.Larghezza : 0.5;
            var fy = tutto.Altezza > 0 ? (cy - tutto.Y0) / tutto.Altezza : 0.5;

            var verticale = fy < 0.33 ? "in-alto" : fy > 0.67 ? "in-basso" : "al-centro";
            string? orizzontale = fx < 0.33 ? "a-sinistra" : fx > 0.67 ? "a-destra" : null;

            if (orizzontale == null) return verticale;
            return verticale + "-" + orizzontale;
        }

        private static string Ricompone(string svg, int inizioCorpo, int chiusura, List<Gruppo> gruppi)
        {
            var sb = new StringBuilder();
            sb.Append(svg, 0, inizioCorpo).Append('\n');
            foreach (var g in gruppi)
            {
                // L'id e' quello che Illustrator usa come nome del gruppo; data-name lo ripete per
                // gli strumenti che leggono quello. Nessuno dei due cambia il disegno.
                sb.Append("<g id=\"").Append(g.Nome).Append("\" data-name=\"").Append(g.Nome).Append("\">\n");
                foreach (var f in g.Figure) sb.Append(f.Xml).Append('\n');
                sb.Append("</g>\n");
            }
            sb.Append(svg, chiusura, svg.Length - chiusura);
            return sb.ToString();
        }

        private sealed class Figura
        {
            public Figura(string xml, Riquadro riquadro) { Xml = xml; Riquadro = riquadro; }
            public string Xml { get; private set; }
            public Riquadro Riquadro { get; private set; }
        }

        private sealed class Gruppo
        {
            public List<Figura> Figure { get; private set; }
            public Riquadro Riquadro { get; private set; }
            public string Nome { get; set; }
            public bool Minuti { get; set; }

            public Gruppo()
            {
                Figure = new List<Figura>();
                Riquadro = Riquadro.Vuoto;
                Nome = "";
            }

            public void Aggiungi(Figura f)
            {
                Figure.Add(f);
                Riquadro = Riquadro.Valido ? Riquadro.Unione(f.Riquadro) : f.Riquadro;
            }
        }
    }
}

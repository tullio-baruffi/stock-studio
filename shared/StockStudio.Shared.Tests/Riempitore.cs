using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Riempie i contorni prodotti da <see cref="Contorni"/> e conta quante volte ogni pixel viene
/// coperto.
///
/// Serve a provare la tassellatura sul **risultato**, non sul modello che l'ha generato: le
/// invarianti combinatorie si possono verificare guardando gli archi, ma se poi la scrittura del
/// percorso sbagliasse un verso o saltasse una curva il file uscirebbe rotto lo stesso. Qui si
/// riempie davvero, con la stessa regola di riempimento dell'SVG.
///
/// Le cubiche si spezzano in segmentini e si riempie a scansione con la regola **nonzero**, che e'
/// quella dichiarata nell'SVG: un pixel sta dentro quando i contorni che lo circondano, contati con
/// il loro verso, non si annullano.
/// </summary>
internal static class Riempitore
{
    /// <summary>In quanti pezzi si spezza ogni cubica. Venti bastano: il campione e' un pixel.</summary>
    private const int Pezzi = 20;

    /// <summary>
    /// Quante volte ogni pixel e' coperto, contando tutte le zone. Il campione si prende al centro
    /// del pixel, cosi' un confine che passa esattamente sul bordo non viene contato due volte.
    /// </summary>
    public static int[] Coperture(Contorni.Esito contorni, int larghezza, int altezza)
    {
        var conta = new int[larghezza * altezza];
        foreach (var anelli in contorni.Zone)
        {
            if (anelli.Count == 0) continue;
            var dentro = Riempi(contorni, anelli, larghezza, altezza);
            for (var i = 0; i < conta.Length; i++) if (dentro[i]) conta[i]++;
        }
        return conta;
    }

    /// <summary>Quali pixel appartengono a una zona.</summary>
    public static bool[] Riempi(Contorni.Esito contorni, List<Contorni.Anello> anelli,
                                int larghezza, int altezza)
    {
        // Tutti i segmenti di tutti gli anelli della zona, gia' appiattiti.
        var segmenti = new List<(double X0, double Y0, double X1, double Y1)>();
        foreach (var anello in anelli)
        {
            var punti = Spezzata(contorni, anello);
            for (var i = 0; i + 1 < punti.Count; i++)
                segmenti.Add((punti[i].X, punti[i].Y, punti[i + 1].X, punti[i + 1].Y));
            // La chiusura: l'ultimo punto torna al primo, e senza questo tratto il conteggio degli
            // attraversamenti sarebbe dispari e il riempimento colerebbe fuori.
            if (punti.Count > 1)
                segmenti.Add((punti[^1].X, punti[^1].Y, punti[0].X, punti[0].Y));
        }

        var dentro = new bool[larghezza * altezza];
        for (var y = 0; y < altezza; y++)
        {
            var yc = y + 0.5;
            // Per ogni segmento attraversato dalla riga: dove la taglia e in che verso.
            var incroci = new List<(double X, int Verso)>();
            foreach (var s in segmenti)
            {
                if (Math.Abs(s.Y1 - s.Y0) < 1e-12) continue;              // orizzontale: non taglia
                var basso = Math.Min(s.Y0, s.Y1);
                var alto = Math.Max(s.Y0, s.Y1);
                if (yc < basso || yc >= alto) continue;
                var t = (yc - s.Y0) / (s.Y1 - s.Y0);
                incroci.Add((s.X0 + t * (s.X1 - s.X0), s.Y1 > s.Y0 ? 1 : -1));
            }
            if (incroci.Count == 0) continue;
            incroci.Sort((a, b) => a.X.CompareTo(b.X));

            var giro = 0;
            for (var k = 0; k + 1 <= incroci.Count - 1; k++)
            {
                giro += incroci[k].Verso;
                if (giro == 0) continue;                                   // fuori: regola nonzero
                var da = (int)Math.Ceiling(incroci[k].X - 0.5);
                var a = (int)Math.Ceiling(incroci[k + 1].X - 0.5) - 1;
                if (da < 0) da = 0;
                if (a > larghezza - 1) a = larghezza - 1;
                for (var x = da; x <= a; x++) dentro[y * larghezza + x] = true;
            }
        }
        return dentro;
    }

    /// <summary>L'anello come spezzata, seguendo gli archi nel verso in cui li attraversa.</summary>
    public static List<Punto> Spezzata(Contorni.Esito contorni, Contorni.Anello anello)
    {
        var punti = new List<Punto>();
        if (anello.Passi.Count == 0) return punti;

        var primo = contorni.Archi[anello.Passi[0].Arco];
        punti.Add(anello.Passi[0].Inverso ? primo.Fine : primo.Inizio);

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
                    var c = arco.Cubiche[n - 1 - k];
                    c1 = c[1]; c2 = c[0];
                    fine = n - 1 - k == 0 ? arco.Inizio : arco.Cubiche[n - 2 - k][2];
                }
                var da = punti[^1];
                for (var j = 1; j <= Pezzi; j++)
                {
                    var t = (double)j / Pezzi;
                    var mt = 1 - t;
                    var b0 = mt * mt * mt;
                    var b1 = 3 * t * mt * mt;
                    var b2 = 3 * t * t * mt;
                    var b3 = t * t * t;
                    punti.Add(new Punto(da.X * b0 + c1.X * b1 + c2.X * b2 + fine.X * b3,
                                        da.Y * b0 + c1.Y * b1 + c2.Y * b2 + fine.Y * b3));
                }
            }
        }
        return punti;
    }
}

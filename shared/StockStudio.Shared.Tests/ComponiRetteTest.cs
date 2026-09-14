using System.Linq;
using System.Text.RegularExpressions;
using StockStudio.Shared.Vettoriale;
using Xunit;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Che un bordo dritto venga scritto come un segmento e non come una curva.
///
/// Non cambia il disegno -- la forma e' la stessa -- ma cambia il file: una retta scritta come
/// cubica costa sei numeri invece di due, e chi apre il vettoriale in un programma di disegno si
/// aspetta di trovare un segmento dove il bordo e' dritto, non una curva con due maniglie.
/// </summary>
public class ComponiRetteTest
{
    [Fact]
    public void IlBordoDellaTavolaSiScriveConSegmenti()
    {
        // Il contorno della tavola e' l'unica cosa **dritta per costruzione** che passa di qui: e'
        // il rettangolo dell'immagine, e i suoi punti stanno esattamente sugli spigoli del
        // reticolo. Se nemmeno quello uscisse come segmenti, il riconoscimento non funzionerebbe.
        //
        // Un rettangolo disegnato **dentro** l'immagine non andrebbe bene per questa prova, ed e'
        // un errore che questo test ha gia' fatto: passando per la riduzione a tinte il suo bordo
        // oscilla di mezzo pixel -- i pixel di frangia vengono assegnati ora a una tinta ora
        // all'altra -- e quelle curve sono curve davvero. Misurato: i controlli si scostano fino a
        // sei decimi di pixel dalla corda, quattro volte la tolleranza.
        var svg = TracciaRettangolo();

        var primo = Regex.Match(svg, @"\sd=""([^""]*)""").Groups[1].Value;
        var anelloEsterno = primo.Split('Z')[0];

        Assert.True(!anelloEsterno.Contains("C") && anelloEsterno.Count(ch => ch == 'L') >= 4,
            $"il bordo della tavola dovrebbe essere fatto di soli segmenti, invece e': {anelloEsterno}");
    }

    [Fact]
    public void LEpsUsaLinetoDoveLSvgUsaL()
    {
        // Le due strade scrivono la stessa geometria in due linguaggi: se una riconoscesse le
        // rette e l'altra no, lo stesso disegno peserebbe due pesi diversi e, peggio, il cliente
        // che apre l'EPS troverebbe curve dove nell'SVG ci sono segmenti.
        var eps = TracciaRettangolo(eps: true);
        Assert.Contains("lineto", eps);
    }

    [Fact]
    public void UnCerchioRestaFattoDiCurve()
    {
        // Il controesempio: se si riconoscessero dritte anche le curve, un cerchio diventerebbe un
        // poligono. E' la prova che la tolleranza e' stretta davvero. Si guarda **il cerchio**, non
        // tutto il file: il contorno della tavola e' dritto per costruzione e, contato insieme,
        // farebbe sembrare pieno di segmenti anche un disegno fatto di sole curve.
        const int w = 120, h = 120;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) { rgb[i * 3] = 255; rgb[i * 3 + 1] = 255; rgb[i * 3 + 2] = 255; }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                double dx = x - 60, dy = y - 60;
                if (dx * dx + dy * dy > 45 * 45) continue;
                var p = (y * w + x) * 3;
                rgb[p] = 20; rgb[p + 1] = 40; rgb[p + 2] = 180;
            }

        // Il cerchio e' l'ultimo percorso: il primo e' il fondo, che porta con se' il bordo
        // della tavola e i suoi segmenti dritti.
        var svg = Componi(rgb, w, h);
        var percorsi = Regex.Matches(svg, @"\sd=""([^""]*)""");
        var cerchio = percorsi[percorsi.Count - 1].Groups[1].Value;
        var rette = cerchio.Count(ch => ch == 'L');
        var curve = cerchio.Count(ch => ch == 'C');

        Assert.True(curve > rette,
            $"un cerchio dovrebbe essere fatto di curve: {curve} curve, {rette} rette");
    }

    /// <summary>Un rettangolo pieno su fondo bianco, tracciato e composto in SVG (o EPS).</summary>
    private static string TracciaRettangolo(bool eps = false)
    {
        const int w = 120, h = 90;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) { rgb[i * 3] = 255; rgb[i * 3 + 1] = 255; rgb[i * 3 + 2] = 255; }
        for (var y = 20; y < 70; y++)
            for (var x = 25; x < 95; x++)
            {
                var p = (y * w + x) * 3;
                rgb[p] = 30; rgb[p + 1] = 90; rgb[p + 2] = 200;
            }
        return Componi(rgb, w, h, eps);
    }

    private static string Componi(byte[] rgb, int w, int h, bool eps = false)
    {
        // Lisciatura spenta: qui si prova **lo scrittore**, non il lisciatore. Con la lisciatura
        // accesa i lati dritti escono incurvati di una frazione di pixel, e il test misurerebbe
        // quanto Taubin li piega invece di quanto bene si riconosce una retta.
        var p = new ParametriTracciato { NumeroColori = 4, Granelli = 0, RaggioLisciatura = 0,
                                         LisciaturaAutomatica = false, RiduzioneRumore = 0,
                                         GiriLisciatura = 0 };
        var t = Tavolozza.Riduci(rgb, w, h, p.NumeroColori, p.SogliaUnione);
        var c = Contorni.Estrai(t.Indici, t.Opaco, w, h, t.Colori.Length, p);
        var tinte = t.Colori
            .Select(x => new VettorialeCondiviso.Tinta { Colore = x, Rampa = null })
            .ToList();
        var uscita = eps
            ? VettorialeCondiviso.ComponiEps(c, tinte, w, h)
            : VettorialeCondiviso.ComponiSvg(c, tinte, w, h);
        Assert.NotNull(uscita);
        return uscita!;
    }
}

using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Le promesse della mediana: che tolga il rumore e che **non** sposti i bordi.
///
/// La seconda conta quanto la prima. Una sfocatura toglierebbe il rumore altrettanto bene, e
/// sarebbe la scelta sbagliata proprio perche' allargherebbe la frangia di ogni contorno: il
/// motivo per cui qui c'e' una mediana e' tutto in <see cref="UnBordoNettoRestaDovEra"/>.
/// </summary>
public class RumoreTest
{
    /// <summary>Un'immagine piatta del colore dato.</summary>
    private static byte[] Piatta(int w, int h, byte r, byte g, byte b)
    {
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) { rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; }
        return rgb;
    }

    private static (byte R, byte G, byte B) Pixel(byte[] rgb, int w, int x, int y)
    {
        var i = (y * w + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    [Fact]
    public void IGranelliIsolatiSpariscono()
    {
        const int w = 20, h = 20;
        var rgb = Piatta(w, h, 200, 200, 200);
        // Un pixel fuori posto in mezzo a una campitura: e' il rumore che la compressione lascia.
        rgb[(10 * w + 10) * 3] = 0;
        rgb[(10 * w + 10) * 3 + 1] = 0;
        rgb[(10 * w + 10) * 3 + 2] = 0;

        var pulita = Rumore.Mediana(rgb, w, h, 1);

        Assert.Equal((byte)200, Pixel(pulita, w, 10, 10).R);
    }

    /// <summary>
    /// Il motivo per cui e' una mediana: un bordo netto deve restare netto e **dov'era**.
    ///
    /// Una media lo avrebbe spalmato su tre pixel, e la tinta intermedia sarebbe poi diventata una
    /// campitura vera nel tracciato -- il difetto che questa passata dovrebbe togliere, non creare.
    /// </summary>
    [Fact]
    public void UnBordoNettoRestaDovEra()
    {
        const int w = 20, h = 20;
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = (byte)(x < 10 ? 0 : 255);
                var i = (y * w + x) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
            }

        var pulita = Rumore.Mediana(rgb, w, h, 2);

        for (var y = 0; y < h; y++)
        {
            Assert.Equal((byte)0, Pixel(pulita, w, 9, y).R);
            Assert.Equal((byte)255, Pixel(pulita, w, 10, y).R);
        }
    }

    [Fact]
    public void RaggioZeroNonToccaNiente()
    {
        var rgb = Piatta(8, 8, 1, 2, 3);
        Assert.Same(rgb, Rumore.Mediana(rgb, 8, 8, 0));
    }

    /// <summary>
    /// Una finestra piu' larga dell'immagine non e' una lavorazione: sarebbe la cancellazione del
    /// disegno, perche' ogni pixel diventerebbe la mediana di tutti.
    /// </summary>
    [Fact]
    public void UnaFinestraPiuLargaDellImmagineNonSiApplica()
    {
        var rgb = Piatta(5, 5, 10, 20, 30);
        Assert.Same(rgb, Rumore.Mediana(rgb, 5, 5, 3));
    }

    /// <summary>
    /// Ai bordi la finestra si riempie ripetendo il pixel di bordo: senza quello la mediana
    /// cadrebbe in una posizione diversa lungo la cornice, e la cornice si tingerebbe.
    /// </summary>
    [Fact]
    public void LaCorniceNonCambiaColore()
    {
        const int w = 15, h = 15;
        var rgb = Piatta(w, h, 77, 88, 99);

        var pulita = Rumore.Mediana(rgb, w, h, 2);

        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1), (7, 0), (0, 7) })
            Assert.Equal((77, 88, 99), (Pixel(pulita, w, x, y).R, Pixel(pulita, w, x, y).G, Pixel(pulita, w, x, y).B));
    }

    /// <summary>I tre canali si lavorano separatamente, e nessuno deve finire in un altro.</summary>
    [Fact]
    public void ICanaliNonSiMescolano()
    {
        const int w = 12, h = 12;
        var rgb = Piatta(w, h, 10, 120, 240);

        var pulita = Rumore.Mediana(rgb, w, h, 2);

        var p = Pixel(pulita, w, 6, 6);
        Assert.Equal(((byte)10, (byte)120, (byte)240), (p.R, p.G, p.B));
    }
}

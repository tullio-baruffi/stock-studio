using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Tests;

/// <summary>
/// La promessa di <see cref="Disegno"/>: riconoscere un disegno a tinte piatte da uno sfumato, e
/// non lisciare il primo.
///
/// Nasce da un difetto misurato sulle immagini vere del portfolio: una taratura scelta su
/// un'illustrazione ombreggiata, applicata a un line art, gli smussava le punte e gli stringeva i
/// vuoti fra i tratti.
/// </summary>
public class DisegnoTest
{
    /// <summary>Due tinte nette, come un line art: nessuna sfumatura da nessuna parte.</summary>
    private static byte[] TintePiatte(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var scuro = ((x / 9) + (y / 9)) % 2 == 0;
                rgb[i] = (byte)(scuro ? 30 : 245);
                rgb[i + 1] = (byte)(scuro ? 58 : 247);
                rgb[i + 2] = (byte)(scuro ? 95 : 251);
            }
        return rgb;
    }

    /// <summary>Una rampa continua: ogni colonna un valore diverso, come un'ombreggiatura.</summary>
    private static byte[] Sfumato(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var v = (byte)(255 * x / System.Math.Max(1, w - 1));
                rgb[i] = v;
                rgb[i + 1] = (byte)(255 - v);
                rgb[i + 2] = 128;
            }
        return rgb;
    }

    [Fact]
    public void UnDisegnoATintePiatteSiRiconosce()
    {
        const int w = 90, h = 90;
        var scarto = Disegno.Scarto(TintePiatte(w, h), w, h, new bool[0]);

        Assert.True(scarto < Disegno.ScartoTintePiatte,
            $"tinte piatte ma scarto {scarto:0.0}, sopra la soglia {Disegno.ScartoTintePiatte}");
        Assert.True(Disegno.ATintePiatte(TintePiatte(w, h), w, h, new bool[0]));
    }

    [Fact]
    public void UnaSfumaturaNonPassaPerTintePiatte()
    {
        const int w = 200, h = 60;
        var scarto = Disegno.Scarto(Sfumato(w, h), w, h, new bool[0]);

        Assert.True(scarto >= Disegno.ScartoTintePiatte,
            $"sfumatura ma scarto {scarto:0.0}, sotto la soglia {Disegno.ScartoTintePiatte}");
        Assert.False(Disegno.ATintePiatte(Sfumato(w, h), w, h, new bool[0]));
    }

    /// <summary>Sulle tinte piatte non si liscia: e' tutto il punto.</summary>
    [Fact]
    public void SulleTintePiatteNonSiLiscia()
    {
        const int w = 90, h = 90;
        var p = ParametriTracciato.Predefiniti;

        Assert.Equal(0, Disegno.LisciaturaPer(TintePiatte(w, h), w, h, new bool[0], p));
    }

    [Fact]
    public void SulloSfumatoSiLisciaQuantoChiesto()
    {
        const int w = 200, h = 60;
        var p = ParametriTracciato.Predefiniti;

        Assert.Equal(p.RaggioLisciatura, Disegno.LisciaturaPer(Sfumato(w, h), w, h, new bool[0], p));
    }

    /// <summary>
    /// Chi sceglie comanda: se la lisciatura e' stata imposta, il disegno non la scavalca. E'
    /// l'unico modo perche' il cursore nell'interfaccia voglia dire qualcosa.
    /// </summary>
    [Fact]
    public void LaSceltaEsplicitaVinceSulDisegno()
    {
        const int w = 90, h = 90;
        var imposta = new ParametriTracciato { RaggioLisciatura = 3, LisciaturaAutomatica = false };

        Assert.Equal(3, Disegno.LisciaturaPer(TintePiatte(w, h), w, h, new bool[0], imposta));
    }

    /// <summary>
    /// La misura descrive il **disegno**, non la taratura: chiedere piu' o meno tinte al tracciato
    /// non deve cambiare il genere a cui l'immagine appartiene.
    /// </summary>
    [Fact]
    public void LaMisuraNonDipendeDallaTaratura()
    {
        const int w = 200, h = 60;
        var pochi = new ParametriTracciato { NumeroColori = 4 };
        var molti = new ParametriTracciato { NumeroColori = 64 };
        var img = Sfumato(w, h);

        Assert.Equal(Disegno.LisciaturaPer(img, w, h, new bool[0], pochi),
                     Disegno.LisciaturaPer(img, w, h, new bool[0], molti));
    }

    [Fact]
    public void UnImmagineVuotaNonRompeNiente()
    {
        Assert.Equal(0, Disegno.Scarto(new byte[0], 0, 0, new bool[0]));
        Assert.Equal(0, Disegno.Scarto(null!, 10, 10, new bool[0]));
    }
}

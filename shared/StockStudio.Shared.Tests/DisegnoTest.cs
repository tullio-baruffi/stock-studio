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

    // ---- La taratura consigliata --------------------------------------------------------------

    [Fact]
    public void SulleTintePiatteIlConsiglioToglieLaLisciatura()
    {
        const int w = 90, h = 90;
        var c = Disegno.Consiglia(TintePiatte(w, h), w, h, new bool[0]);

        Assert.Equal(0, c.RaggioLisciatura);
        // La scelta e' stata fatta guardando: non deve poi essere riconsiderata a valle.
        Assert.False(c.LisciaturaAutomatica);
    }

    [Fact]
    public void SulloSfumatoIlConsiglioLasciaLaLisciatura()
    {
        const int w = 200, h = 60;
        Assert.Equal(1, Disegno.Consiglia(Sfumato(w, h), w, h, new bool[0]).RaggioLisciatura);
    }

    /// <summary>
    /// Su un'illustrazione sfumata il consiglio non tocca granelli e rumore: li' le macchioline
    /// sono frammenti d'ombra da togliere, e abbassare le soglie peggiorerebbe il disegno --
    /// misurato, i contorni della balena salivano da 404 a 590.
    /// </summary>
    [Fact]
    public void SulloSfumatoIlConsiglioNonToccaGranelliERumore()
    {
        const int w = 200, h = 60;
        var p = ParametriTracciato.Predefiniti;
        var c = Disegno.Consiglia(Sfumato(w, h), w, h, new bool[0], p);

        Assert.Equal(p.Granelli, c.Granelli);
        Assert.Equal(p.RiduzioneRumore, c.RiduzioneRumore);
    }

    /// <summary>
    /// E in nessun caso il consiglio **alza** quel che toglie: puo' solo essere piu' prudente del
    /// predefinito, mai piu' aggressivo. E' la garanzia che accettarlo non possa far sparire
    /// dettagli che la taratura di serie avrebbe tenuto.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IlConsiglioNonEMaiPiuAggressivoDelPredefinito(bool piatte)
    {
        const int w = 200, h = 90;
        var img = piatte ? TintePiatte(w, h) : Sfumato(w, h);
        var p = ParametriTracciato.Predefiniti;
        var c = Disegno.Consiglia(img, w, h, new bool[0], p);

        Assert.True(c.Granelli <= p.Granelli, $"granelli {c.Granelli} > predefinito {p.Granelli}");
        Assert.True(c.RiduzioneRumore <= p.RiduzioneRumore,
            $"rumore {c.RiduzioneRumore} > predefinito {p.RiduzioneRumore}");
        Assert.True(c.RaggioLisciatura <= p.RaggioLisciatura);
        Assert.True(c.Tolleranza <= p.Tolleranza + 1e-9,
            $"fedelta' {c.Tolleranza:0.00} > predefinito {p.Tolleranza:0.00}");
    }

    /// <summary>
    /// Il consiglio si esprime alla grandezza convenzionale, come tutto il resto: se parlasse in
    /// pixel veri, <see cref="ParametriTracciato.PerImmagine"/> riapplicherebbe la scala e i
    /// numeri uscirebbero al quadrato della grandezza.
    /// </summary>
    [Fact]
    public void IlConsiglioEAllaGrandezzaConvenzionale()
    {
        const int w = 200, h = 90;
        var c = Disegno.Consiglia(TintePiatte(w, h), w, h, new bool[0]);
        var effettivi = c.PerImmagine(w, h);

        // Immagine molto piu' piccola del riferimento: i granelli effettivi devono scendere.
        Assert.True(effettivi.Granelli <= c.Granelli,
            $"effettivi {effettivi.Granelli} > dichiarati {c.Granelli} su un'immagine piccola");
    }

    /// <summary>
    /// La fedelta' si abbassa quando il disegno ha strutture sottili.
    ///
    /// E' la correzione che mancava: la fedelta' dice di quanto la curva puo' allontanarsi dal
    /// bordo, e se quel margine vale quanto l'anello di una bollicina la curva puo' attraversarlo
    /// tutto. Misurato sulla balena, dove l'anello e' spesso tre pixel e la media venti: a 2,5 px
    /// le bolle escono poligonali, a 1,3 tonde.
    /// </summary>
    [Fact]
    public void LeStruttureSottiliAbbassanoLaFedelta()
    {
        // Una tavola vicina a quelle vere. Su un'immagine minuscola non si misura niente di utile:
        // la riscalatura verso la grandezza convenzionale si ferma al suo limite e gonfia ogni
        // misura di tre volte, schiacciando entrambi i casi contro il predefinito.
        const int w = 1500, h = 600;
        var p = ParametriTracciato.Predefiniti;

        var conFilo = Disegno.Consiglia(ConFiloSottile(w, h), w, h, new bool[0], p);
        var senza = Disegno.Consiglia(SoloCampitureLarghe(w, h), w, h, new bool[0], p);

        Assert.True(conFilo.Tolleranza < senza.Tolleranza,
            $"con un filo sottile la fedelta' e' {conFilo.Tolleranza:0.00}, " +
            $"senza {senza.Tolleranza:0.00}: doveva essere piu' stretta");
    }

    /// <summary>
    /// Ma non si abbassa per un granello: una macchiolina minuscola non e' una struttura, e se
    /// contasse basterebbe un puntino di rumore per far esplodere il numero di nodi di ogni file.
    /// </summary>
    [Fact]
    public void UnGranelloNonAbbassaLaFedelta()
    {
        const int w = 1500, h = 600;
        var p = ParametriTracciato.Predefiniti;
        var img = SoloCampitureLarghe(w, h);
        // Un puntino di tre pixel per lato, ben sotto la quota minima.
        for (var y = 5; y < 8; y++)
            for (var x = 5; x < 8; x++)
            {
                var i = (y * w + x) * 3;
                img[i] = 250; img[i + 1] = 40; img[i + 2] = 40;
            }

        var conGranello = Disegno.Consiglia(img, w, h, new bool[0], p);
        var senza = Disegno.Consiglia(SoloCampitureLarghe(w, h), w, h, new bool[0], p);

        Assert.Equal(senza.Tolleranza, conGranello.Tolleranza, 3);
    }

    /// <summary>Un filo sottile su una campitura larga: e' il caso dell'anello di una bolla.</summary>
    private static byte[] ConFiloSottile(int w, int h)
    {
        var rgb = SoloCampitureLarghe(w, h);
        // Una banda alta pochi pixel ma lunga tutta l'immagine: area piccola, perimetro grande.
        for (var y = h / 2 - 1; y < h / 2 + 2; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                rgb[i] = 20; rgb[i + 1] = 20; rgb[i + 2] = 20;
            }
        return rgb;
    }

    /// <summary>Due sole campiture, entrambe larghe: niente di sottile da conservare.</summary>
    private static byte[] SoloCampitureLarghe(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var dentro = x > w / 4 && x < 3 * w / 4 && y > h / 4 && y < 3 * h / 4;
                rgb[i] = (byte)(dentro ? 40 : 240);
                rgb[i + 1] = (byte)(dentro ? 70 : 242);
                rgb[i + 2] = (byte)(dentro ? 120 : 246);
            }
        return rgb;
    }

    [Fact]
    public void UnaMisuraVuotaLasciaILPredefinito()
    {
        var p = ParametriTracciato.Predefiniti;
        var c = Disegno.Consiglia(new Disegno.Misure(), p);

        Assert.Equal(p.Granelli, c.Granelli);
        Assert.Equal(p.NumeroColori, c.NumeroColori);
    }
}


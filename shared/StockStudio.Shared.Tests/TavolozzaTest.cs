using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Le promesse della tavolozza che si possono verificare: che il pulviscolo sparisca, che le
/// campiture vere restino, e che il trasparente non venga scambiato per un colore.
///
/// Ognuno di questi test corrisponde a un difetto trovato misurando, e serve a non ritrovarlo.
/// </summary>
public class TavolozzaTest
{
    /// <summary>Un fondo pieno con qualche macchia dentro, di dimensione scelta.</summary>
    private static Tavolozza.Esito Mappa(int w, int h, params (int X, int Y, int Lato, byte Tinta)[] macchie)
    {
        var indici = new byte[w * h];
        foreach (var m in macchie)
            for (var y = m.Y; y < m.Y + m.Lato && y < h; y++)
                for (var x = m.X; x < m.X + m.Lato && x < w; x++)
                    indici[y * w + x] = m.Tinta;

        var opaco = new bool[w * h];
        for (var i = 0; i < opaco.Length; i++) opaco[i] = true;

        return new Tavolozza.Esito
        {
            Colori = new[] { new Colore(0, 0, 0, 0), new Colore(255, 0, 0, 0), new Colore(0, 255, 0, 0) },
            Indici = indici,
            Opaco = opaco,
            SuBordo = new bool[w * h],
        };
    }

    private static int Quanti(Tavolozza.Esito e, byte tinta)
    {
        return e.Indici.Count(i => i == tinta);
    }

    // ---- Pulviscolo --------------------------------------------------------------------------

    /// <summary>
    /// Una macchia sotto la soglia sparisce dentro la campitura che la circonda. E' quel che toglie
    /// i contorni chiusi inutili e i nodi che spezzano i bordi -- misurato, su un'illustrazione vera
    /// erano 1.276 macchie sotto i 64 pixel.
    /// </summary>
    [Fact]
    public void UnGranelloSparisceNelFondo()
    {
        var e = Mappa(40, 40, (10, 10, 3, 1));   // 9 pixel: sotto la soglia
        Assert.Equal(9, Quanti(e, 1));

        Tavolozza.TogliIGranelli(e, 40, 40, 64);

        Assert.Equal(0, Quanti(e, 1));
        Assert.Equal(1600, Quanti(e, 0));
    }

    /// <summary>Una campitura vera non si tocca, per quanto piccola sia la soglia.</summary>
    [Fact]
    public void UnaCampituraVeraResta()
    {
        var e = Mappa(40, 40, (10, 10, 12, 1));  // 144 pixel: sopra la soglia
        Tavolozza.TogliIGranelli(e, 40, 40, 64);
        Assert.Equal(144, Quanti(e, 1));
    }

    /// <summary>
    /// Il granello passa alla tinta con cui **confina di piu'**, non alla prima che capita: e' la
    /// scelta che sposta meno il disegno.
    /// </summary>
    [Fact]
    public void IlGranelloVaAllaTintaConCuiConfinaDiPiu()
    {
        const int w = 40, h = 40;
        var e = Mappa(w, h);
        // Meta' destra di tinta 2, e un granello di tinta 1 tutto dentro quella meta'.
        for (var y = 0; y < h; y++)
            for (var x = w / 2; x < w; x++) e.Indici[y * w + x] = 2;
        for (var y = 20; y < 23; y++)
            for (var x = 30; x < 33; x++) e.Indici[y * w + x] = 1;

        Tavolozza.TogliIGranelli(e, w, h, 64);

        Assert.Equal(0, Quanti(e, 1));
        Assert.Equal(h * w / 2, Quanti(e, 2));   // il granello e' finito nel 2, non nello 0
    }

    /// <summary>
    /// Una macchia isolata nel trasparente non ha con chi fondersi e resta dov'e': li' non e'
    /// pulviscolo, e' l'unico disegno che c'e'.
    /// </summary>
    [Fact]
    public void UnaMacchiaIsolataNelTrasparenteNonSiTocca()
    {
        const int w = 40, h = 40;
        var e = Mappa(w, h, (10, 10, 3, 1));
        for (var i = 0; i < e.Opaco.Length; i++) e.Opaco[i] = e.Indici[i] == 1;

        Tavolozza.TogliIGranelli(e, w, h, 64);

        Assert.Equal(9, Quanti(e, 1));
    }

    /// <summary>La soglia scala con l'immagine, ma resta dentro limiti sensati.</summary>
    [Theory]
    [InlineData(100, 100, 4)]        // francobollo: il minimo
    [InlineData(3840, 2160, 60)]     // otto megapixel: il massimo
    public void LaSogliaDeiGranelliScalaEResta(int w, int h, int atteso)
    {
        Assert.Equal(atteso, Tavolozza.SogliaGranelli(w, h));
    }

    // ---- Colori o bianco e nero --------------------------------------------------------------

    /// <summary>
    /// I pixel trasparenti non contano nel decidere se un'immagine ha colori. Contandoli, un logo
    /// ritagliato -- dove il trasparente e' la maggioranza -- sarebbe stato lavorato come
    /// silhouette e avrebbe perso tutte le tinte.
    /// </summary>
    [Fact]
    public void HaColoriIgnoraIlTrasparente()
    {
        const int n = 10000;
        var rgb = new byte[n * 3];
        var opaco = new bool[n];
        // L'1% dei pixel e' disegno, ed e' tutto rosso pieno; il resto e' trasparente, e sotto il
        // trasparente i PNG scrivono nero. La soglia di HaColori sta al 2%: contando anche i neri
        // l'immagine sembra grigia, guardando il solo disegno e' tutta colorata.
        for (var i = 0; i < n / 100; i++)
        {
            rgb[i * 3] = 220; rgb[i * 3 + 1] = 20; rgb[i * 3 + 2] = 20;
            opaco[i] = true;
        }

        Assert.False(Tavolozza.HaColori(rgb));                  // contando i neri: sembra grigia
        Assert.True(Tavolozza.HaColori(rgb, opachi: opaco));    // guardando il disegno: e' a colori
    }

    // ---- Lisciatura --------------------------------------------------------------------------

    /// <summary>
    /// La lisciatura raddrizza i bordi ma non deve inventare tinte ne' perdere pixel: resta una
    /// partizione, e gli indici restano dentro la tavolozza.
    /// </summary>
    [Fact]
    public void LaLisciaturaRestaUnaPartizione()
    {
        const int w = 50, h = 50;
        var e = Mappa(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (x < y) e.Indici[y * w + x] = 1;

        Tavolozza.LisciaPerTracciato(e, w, h);

        Assert.All(e.Indici, i => Assert.InRange(i, 0, (byte)(e.Colori.Length - 1)));
        Assert.Equal(w * h, e.Colori.Sum(c => c.Pixel));
    }
}

/// <summary>
/// Il canale alfa, che va letto prima di buttarlo via: e' il difetto che faceva uscire un logo
/// ritagliato con il fondo nero, sia nel vettoriale sia nel JPEG di consegna.
/// </summary>
public class TrasparenzaTest
{
    [Fact]
    public void IlTrasparenteDiventaBiancoENonNero()
    {
        // Un pixel completamente trasparente, con sotto scritto nero: e' il caso vero dei PNG.
        var rgba = new byte[] { 0, 0, 0, 0 };
        var rgb = Trasparenza.SuBianco(rgba);
        Assert.Equal(new byte[] { 255, 255, 255 }, rgb);
    }

    [Fact]
    public void IlPienoRestaComEra()
    {
        var rgba = new byte[] { 10, 120, 240, 255 };
        Assert.Equal(new byte[] { 10, 120, 240 }, Trasparenza.SuBianco(rgba));
    }

    [Fact]
    public void IlSemitrasparenteSiMescolaColBianco()
    {
        // Nero a meta' opacita' su bianco: grigio di mezzo.
        var rgb = Trasparenza.SuBianco(new byte[] { 0, 0, 0, 128 });
        Assert.All(rgb, v => Assert.InRange(v, 126, 129));
    }

    [Fact]
    public void SottoMetaOpacitaNonEPiuDisegno()
    {
        var opachi = Trasparenza.Opachi(new byte[] { 0, 0, 0, 127, 0, 0, 0, 128 });
        Assert.False(opachi[0]);
        Assert.True(opachi[1]);
    }

    [Fact]
    public void RiconosceSeCEDavveroDellaTrasparenza()
    {
        Assert.False(Trasparenza.CeTrasparenza(new[] { true, true }));
        Assert.True(Trasparenza.CeTrasparenza(new[] { true, false }));
    }
}

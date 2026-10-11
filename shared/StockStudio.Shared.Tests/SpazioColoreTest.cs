using System;
using StockStudio.Shared.Vettoriale;
using Xunit;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Che la misura percettiva sia davvero Oklab, e che sia innestata senza toccare quel che si
/// consegna oggi.
///
/// Sono due cose distinte e vanno provate separatamente: la formula puo' essere giusta e il
/// collegamento sbagliato -- per esempio scalando male, e allora ogni soglia del motore
/// cambierebbe significato in silenzio.
/// </summary>
public class SpazioColoreTest
{
    /// <summary>
    /// I valori di riferimento di Ottosson. Sono il controllo che la formula sia trascritta bene:
    /// una cifra sbagliata in una delle diciotto costanti darebbe comunque numeri plausibili, e
    /// senza un riferimento esterno non si distinguerebbero da quelli giusti.
    /// </summary>
    [Theory]
    [InlineData(255, 255, 255, 1.0000, 0.0000, 0.0000)]
    [InlineData(0, 0, 0, 0.0000, 0.0000, 0.0000)]
    [InlineData(255, 0, 0, 0.6279, 0.2249, 0.1258)]
    [InlineData(0, 255, 0, 0.8664, -0.2339, 0.1795)]
    [InlineData(0, 0, 255, 0.4520, -0.0324, -0.3115)]
    public void LaFormulaDaIValoriDiRiferimento(int r, int g, int b,
                                                double attesoL, double attesoA, double attesoB)
    {
        double l, a, bb;
        SpazioColore.VersoOklab(r, g, b, out l, out a, out bb);

        // Le coordinate escono moltiplicate per la scala: per confrontarle con i valori pubblicati
        // si torna indietro.
        //
        // Il confronto e' a tolleranza e non a cifre decimali: i valori di riferimento sono dati a
        // quattro cifre, e un arrotondamento a tre manderebbe i due numeri da parti opposte quando
        // il riferimento cade su un confine -- 0,1795 diventa 0,180 e 0,17949848 diventa 0,179,
        // che e' un test che fallisce sulla formattazione invece che sulla formula.
        Vicini(attesoL, l / SpazioColore.Scala, "L");
        Vicini(attesoA, a / SpazioColore.Scala, "a");
        Vicini(attesoB, bb / SpazioColore.Scala, "b");
    }

    /// <summary>Entro mezza unita' dell'ultima cifra pubblicata, cioe' 5 decimillesimi.</summary>
    private static void Vicini(double atteso, double ottenuto, string quale)
    {
        Assert.True(Math.Abs(ottenuto - atteso) < 5e-4,
                    $"{quale}: atteso {atteso:0.######}, ottenuto {ottenuto:0.######}");
    }

    /// <summary>
    /// Andare e tornare deve riportare al colore di partenza. Serve perche' Lloyd calcola il
    /// centro in Oklab e poi lo riporta a RGB: se il ritorno fosse storto, ogni giro sposterebbe
    /// le tinte di un po', e la tavolozza andrebbe alla deriva invece di assestarsi.
    /// </summary>
    [Fact]
    public void AndareETornareRiportaAlloStessoColore()
    {
        for (var r = 0; r <= 255; r += 17)
            for (var g = 0; g <= 255; g += 17)
                for (var b = 0; b <= 255; b += 17)
                {
                    double x, y, z, r2, g2, b2;
                    SpazioColore.VersoOklab(r, g, b, out x, out y, out z);
                    SpazioColore.DaOklab(x, y, z, out r2, out g2, out b2);

                    Assert.True(Math.Abs(r2 - r) < 0.5 && Math.Abs(g2 - g) < 0.5 && Math.Abs(b2 - b) < 0.5,
                                $"({r},{g},{b}) e' tornato ({r2:0.##},{g2:0.##},{b2:0.##})");
                }
    }

    /// <summary>
    /// Il nero e il bianco devono distare lo stesso numero nei due spazi.
    ///
    /// E' il punto su cui regge tutta l'operazione: ogni soglia del motore e' tarata in livelli
    /// per canale -- la soglia delle gemelle vale `soglia*soglia*3` -- e se la nuova misura avesse
    /// un'altra scala quei numeri vorrebbero dire un'altra cosa, senza che niente lo segnali.
    /// </summary>
    [Fact]
    public void LeDueMisureHannoLaStessaScalaSuiGrigi()
    {
        var inRgb = MetricaColore.Rgb.Distanza2(0, 0, 0, 255, 255, 255);
        var inOklab = MetricaColore.Oklab.Distanza2(0, 0, 0, 255, 255, 255);

        Assert.Equal(255.0 * 255.0 * 3.0, inRgb, 6);
        Assert.Equal(Math.Sqrt(inRgb), Math.Sqrt(inOklab), 3);
    }

    /// <summary>
    /// La metrica RGB deve essere **esattamente** il conto di prima, non una sua approssimazione:
    /// e' quella che gira in produzione, e passa attraverso il nuovo codice.
    /// </summary>
    [Fact]
    public void LaMetricaRgbEIlContoDiPrima()
    {
        var caso = new[]
        {
            new[] { 12, 200, 7, 250, 3, 199 },
            new[] { 0, 0, 0, 255, 255, 255 },
            new[] { 128, 128, 128, 128, 128, 128 },
            new[] { 31, 63, 95, 97, 65, 33 },
        };

        foreach (var c in caso)
        {
            double dr = c[0] - c[3], dg = c[1] - c[4], db = c[2] - c[5];
            var atteso = dr * dr + dg * dg + db * db;
            Assert.Equal(atteso, MetricaColore.Rgb.Distanza2(c[0], c[1], c[2], c[3], c[4], c[5]));
        }
    }

    /// <summary>
    /// Il caso che motiva l'esperimento, preso dai commenti di <see cref="Tavolozza"/>: due
    /// marroni che in RGB distano quanto due verdi, ma che l'occhio legge in modo molto diverso.
    ///
    /// In RGB le due coppie distano per costruzione lo stesso; in Oklab no. Il test non giudica
    /// quale resa sia migliore -- quello lo dice il banco -- ma fissa che le due misure
    /// **ordinano diversamente**, che e' l'unica ragione per cui vale la pena provarla.
    /// </summary>
    [Fact]
    public void OklabSeparaCioCheRgbConfonde()
    {
        // Due marroni scuri e due verdi, scelti perche' in RGB le due coppie distino uguale.
        int[] marroneA = { 92, 64, 40 }, marroneB = { 112, 84, 60 };
        int[] verdeA = { 60, 150, 60 }, verdeB = { 80, 170, 80 };

        var marroniRgb = MetricaColore.Rgb.Distanza2(marroneA[0], marroneA[1], marroneA[2],
                                                     marroneB[0], marroneB[1], marroneB[2]);
        var verdiRgb = MetricaColore.Rgb.Distanza2(verdeA[0], verdeA[1], verdeA[2],
                                                   verdeB[0], verdeB[1], verdeB[2]);
        Assert.Equal(marroniRgb, verdiRgb, 6);

        var marroniOklab = MetricaColore.Oklab.Distanza2(marroneA[0], marroneA[1], marroneA[2],
                                                         marroneB[0], marroneB[1], marroneB[2]);
        var verdiOklab = MetricaColore.Oklab.Distanza2(verdeA[0], verdeA[1], verdeA[2],
                                                       verdeB[0], verdeB[1], verdeB[2]);

        // Lo stesso salto di venti livelli si vede di piu' sui marroni scuri che sui verdi chiari:
        // e' la non uniformita' che RGB non sa esprimere.
        Assert.True(marroniOklab > verdiOklab,
                    $"marroni {marroniOklab:0.#} contro verdi {verdiOklab:0.#}");
    }

    /// <summary>
    /// Con la metrica di serie la tavolozza deve venire **identica** a prima, tinta per tinta.
    /// E' la prova che il flag e' spento davvero e che la produzione non si e' mossa.
    /// </summary>
    [Fact]
    public void SenzaFlagLaTavolozzaNonCambia()
    {
        const int w = 160, h = 120;
        var rgb = Prova(w, h);

        var senza = Tavolozza.Riduci(rgb, w, h, 8);
        var conRgb = Tavolozza.Riduci(rgb, w, h, 8, metrica: MetricaColore.Rgb);

        Assert.Equal(senza.Colori.Length, conRgb.Colori.Length);
        for (var i = 0; i < senza.Colori.Length; i++)
        {
            Assert.Equal(senza.Colori[i].R, conRgb.Colori[i].R);
            Assert.Equal(senza.Colori[i].G, conRgb.Colori[i].G);
            Assert.Equal(senza.Colori[i].B, conRgb.Colori[i].B);
        }
        Assert.Equal(senza.Indici, conRgb.Indici);
    }

    /// <summary>
    /// Con la metrica percettiva il motore deve comunque produrre una tavolozza sensata: tante
    /// tinte quante ne servono, tutte dentro l'intervallo, e ogni pixel assegnato.
    ///
    /// Non si pretende che sia uguale -- se lo fosse, l'esperimento non servirebbe a niente.
    /// </summary>
    [Fact]
    public void ConIlFlagIlMotoreRestaSano()
    {
        const int w = 160, h = 120;
        var rgb = Prova(w, h);

        var t = Tavolozza.Riduci(rgb, w, h, 8, metrica: MetricaColore.Oklab);

        Assert.InRange(t.Colori.Length, 2, 8);
        Assert.Equal(w * h, t.Indici.Length);
        foreach (var i in t.Indici) Assert.True(i < t.Colori.Length);
    }

    /// <summary>
    /// Il flag deve sopravvivere a <see cref="ParametriTracciato.Convalidato"/> e a
    /// <see cref="ParametriTracciato.PerImmagine"/>, che ricostruiscono l'oggetto elencando le
    /// proprieta' una per una: una dimenticanza li' spegnerebbe l'esperimento in silenzio, e il
    /// banco misurerebbe due volte la stessa cosa credendo di confrontarne due.
    /// </summary>
    [Fact]
    public void IlFlagSopravviveAllaConvalida()
    {
        var p = new ParametriTracciato { MetricaPercettiva = true };

        Assert.True(p.Convalidato().MetricaPercettiva);
        Assert.True(p.PerImmagine(6000, 4000).MetricaPercettiva);
        Assert.Same(MetricaColore.Oklab, p.PerImmagine(6000, 4000).Metrica);

        var q = new ParametriTracciato();
        Assert.False(q.Convalidato().MetricaPercettiva);
        Assert.Same(MetricaColore.Rgb, q.PerImmagine(6000, 4000).Metrica);
    }

    /// <summary>Un'immagine con qualche campitura e una sfumatura, abbastanza varia da quantizzare.</summary>
    private static byte[] Prova(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = (y * w + x) * 3;
                if (x < w / 3)
                {
                    rgb[p] = 92; rgb[p + 1] = 64; rgb[p + 2] = 40;
                }
                else if (x < 2 * w / 3)
                {
                    rgb[p] = (byte)(60 + x % 40); rgb[p + 1] = 150; rgb[p + 2] = 60;
                }
                else
                {
                    rgb[p] = (byte)(200 - y / 2); rgb[p + 1] = (byte)(180 - y / 3); rgb[p + 2] = 90;
                }
            }
        return rgb;
    }
}

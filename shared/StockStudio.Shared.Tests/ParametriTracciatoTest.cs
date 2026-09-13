using System.Globalization;
using Newtonsoft.Json;
using StockStudio.Shared.Contracts;
using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Le promesse della taratura: che i numeri assurdi non passino, e che le misure in pixel seguano
/// la grandezza dell'immagine.
///
/// La seconda e' il difetto che ha reso necessaria questa classe: la stessa illustrazione
/// consegnata a tremila pixel e a seimila dava due disegni diversi -- pulito il primo, pieno di
/// granelli il secondo -- perche' un raggio di due pixel non vuol dire la stessa cosa sui due.
/// </summary>
public class ParametriTracciatoTest
{
    /// <summary>
    /// Le promesse della taratura: che i numeri assurdi non passino, e che le misure in pixel
    /// seguano la grandezza dell'immagine.
    /// </summary>
    /// <summary>
    /// I numeri sopravvivono al viaggio nella coda, **anche su una macchina italiana**.
    ///
    /// E' l'altra meta' della stessa insidia che ha tolto i parametri dai campi sciolti del modulo
    /// di caricamento: fra la web application e la Function c'e' un messaggio serializzato, e se
    /// quella serializzazione seguisse la cultura locale una tolleranza di 2,7 partirebbe come
    /// "2,7" e verrebbe riletta come ventisette. Qui si prova con la cultura impostata a italiano,
    /// che e' quella su cui gira davvero.
    /// </summary>
    [Fact]
    public void INumeriSopravvivonoAlViaggioNellaCoda()
    {
        var prima = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");

            var messaggio = new VectorizeQueueMessage
            {
                BlobName = "prova.jpg",
                Tracciato = new ParametriTracciato { Tolleranza = 2.7, Morbidezza = 3.5, SogliaUnione = 0.6 },
            };

            var riletto = JsonConvert.DeserializeObject<VectorizeQueueMessage>(messaggio.ToString())!;
            var p = riletto.ParametriDiTracciato();

            Assert.Equal(2.7, p.Tolleranza, 6);
            Assert.Equal(3.5, p.Morbidezza, 6);
            Assert.Equal(0.6, p.SogliaUnione, 6);
        }
        finally
        {
            CultureInfo.CurrentCulture = prima;
        }
    }

    /// <summary>
    /// Un messaggio vecchio, che porta solo le due voci storiche, deve continuare a voler dire quel
    /// che voleva dire: ce ne sono in coda nel momento in cui si rilascia.
    /// </summary>
    [Fact]
    public void UnMessaggioVecchioContinuaAValere()
    {
        var vecchio = "{\"BlobName\":\"prova.jpg\",\"Mode\":\"colore\",\"Colori\":12,\"Unione\":900.0}";

        var p = JsonConvert.DeserializeObject<VectorizeQueueMessage>(vecchio)!.ParametriDiTracciato();

        Assert.Equal(12, p.NumeroColori);
        Assert.Equal(900.0, p.SogliaUnione, 6);
        // Il resto resta la taratura di serie: il messaggio non ne sapeva niente.
        Assert.Equal(ParametriTracciato.Predefiniti.Tolleranza, p.Tolleranza, 6);
        Assert.Equal(ParametriTracciato.Predefiniti.Granelli, p.Granelli);
    }

    /// <summary>
    /// Le due voci storiche vincono su quelle nuove: un messaggio che le porta entrambe e' stato
    /// scritto da chi conosceva le prime, e quelle sono la sua volonta'.
    /// </summary>
    [Fact]
    public void LeVociStoricheVincono()
    {
        var messaggio = new VectorizeQueueMessage
        {
            Colori = 8,
            Tracciato = new ParametriTracciato { NumeroColori = 40, Tolleranza = 1.1 },
        };

        var p = messaggio.ParametriDiTracciato();

        Assert.Equal(8, p.NumeroColori);
        Assert.Equal(1.1, p.Tolleranza, 6);
    }

    [Fact]
    public void ZeroGranelliVuolDireNonToglierneNessuno()
    {
        // Zero deve restare zero attraverso convalida e riscalatura: se diventasse un numero
        // qualunque, il cursore al minimo toglierebbe piu' granelli di quello a dieci -- il comando
        // andrebbe al contrario, che e' peggio di un comando che manca.
        Assert.Equal(0, new ParametriTracciato { Granelli = 0 }.Convalidato().Granelli);
        Assert.Equal(0, new ParametriTracciato { Granelli = 0 }.PerImmagine(6000, 6000).Granelli);
    }

    /// <summary>Alzando il numero si tolgono piu' granelli, a qualunque grandezza.</summary>
    [Theory]
    [InlineData(1500)]
    [InlineData(3000)]
    [InlineData(9000)]
    public void PiuGranelliChiestiPiuGranelliTolti(int lato)
    {
        var scala = new[] { 0, 10, 60, 150, 400 }
            .Select(g => new ParametriTracciato { Granelli = g }.PerImmagine(lato, lato).Granelli)
            .ToArray();

        for (var i = 1; i < scala.Length; i++)
            Assert.True(scala[i] > scala[i - 1],
                $"a {lato}px la scala dei granelli non sale: {string.Join(" ", scala)}");
    }

    [Fact]
    public void AllaGrandezzaDiRiferimentoINumeriRestanoQuelli()
    {
        var p = ParametriTracciato.Predefiniti;
        var s = p.PerImmagine(ParametriTracciato.LatoDiRiferimento, ParametriTracciato.LatoDiRiferimento / 2);

        Assert.Equal(p.RiduzioneRumore, s.RiduzioneRumore);
        Assert.Equal(p.Granelli, s.Granelli);
        Assert.Equal(p.Tolleranza, s.Tolleranza, 6);
    }

    /// <summary>
    /// Raddoppiando i pixel raddoppiano le lunghezze e **quadruplica** l'area: un granello che si
    /// vede uguale occupa quattro volte i pixel.
    /// </summary>
    [Fact]
    public void RaddoppiandoIPixelLeLunghezzeRaddoppianoELAreaQuadruplica()
    {
        var p = ParametriTracciato.Predefiniti;
        var s = p.PerImmagine(ParametriTracciato.LatoDiRiferimento * 2, 100);

        Assert.Equal(p.RiduzioneRumore * 2, s.RiduzioneRumore);
        Assert.Equal(p.Granelli * 4, s.Granelli);
        Assert.Equal(p.Tolleranza * 2, s.Tolleranza, 6);
        Assert.Equal(p.Morbidezza * 2, s.Morbidezza, 6);
    }

    /// <summary>
    /// La lisciatura della mappa non si scala, ed e' l'unico raggio che non lo fa: toglie la
    /// scalinata del reticolo, che e' alta un pixel qualunque sia la grandezza dell'immagine.
    /// </summary>
    [Fact]
    public void LaLisciaturaDellaMappaNonSegueLaGrandezza()
    {
        var p = ParametriTracciato.Predefiniti;

        Assert.Equal(p.RaggioLisciatura, p.PerImmagine(500, 500).RaggioLisciatura);
        Assert.Equal(p.RaggioLisciatura, p.PerImmagine(12000, 12000).RaggioLisciatura);
    }

    /// <summary>Nemmeno quel che non e' una lunghezza: tinte, giri, gradi.</summary>
    [Fact]
    public void QuelCheNonEUnaLunghezzaNonSiScala()
    {
        var p = ParametriTracciato.Predefiniti;
        var s = p.PerImmagine(ParametriTracciato.LatoDiRiferimento * 3, 100);

        Assert.Equal(p.NumeroColori, s.NumeroColori);
        Assert.Equal(p.GiriLisciatura, s.GiriLisciatura);
        Assert.Equal(p.AngoloSpigolo, s.AngoloSpigolo);
        Assert.Equal(p.SogliaUnione, s.SogliaUnione);
    }

    /// <summary>
    /// Una passata spenta resta spenta: scalare zero non deve accenderla, perche' zero qui vuol dire
    /// «non farla» e non «falla piano».
    /// </summary>
    [Fact]
    public void QuelCheEraSpentoRestaSpento()
    {
        var p = new ParametriTracciato { RiduzioneRumore = 0, Granelli = 0 };
        var s = p.PerImmagine(9000, 9000);

        Assert.Equal(0, s.RiduzioneRumore);
        Assert.Equal(0, s.Granelli);
    }

    /// <summary>
    /// E una passata accesa non si spegne per arrotondamento: su un'immagine piccola il raggio
    /// scalerebbe sotto l'unita', e troncarlo a zero sarebbe spegnere una passata che era chiesta.
    /// </summary>
    [Fact]
    public void QuelCheEraAccesoNonSiSpegnePerArrotondamento()
    {
        var p = new ParametriTracciato { RiduzioneRumore = 1, Granelli = 1 };
        var s = p.PerImmagine(200, 150);

        Assert.True(s.RiduzioneRumore >= 1);
        Assert.True(s.Granelli >= 1);
    }

    /// <summary>
    /// Riportare alla grandezza dell'immagine non deve toccare l'originale: la taratura
    /// configurata vive in un oggetto solo, condiviso da tutte le richieste, e modificarlo qui
    /// vorrebbe dire che la seconda immagine lavorata riceve i numeri scalati per la prima.
    /// </summary>
    [Fact]
    public void RiportareAllaGrandezzaNonToccaLOriginale()
    {
        var p = new ParametriTracciato { Granelli = 150, Tolleranza = 2.7, RiduzioneRumore = 2 };

        p.PerImmagine(12000, 12000);
        p.PerImmagine(400, 400);

        Assert.Equal(150, p.Granelli);
        Assert.Equal(2.7, p.Tolleranza, 6);
        Assert.Equal(2, p.RiduzioneRumore);
    }

    [Theory]
    [InlineData(-5, 2)]
    [InlineData(0, 2)]
    [InlineData(1000, 64)]
    public void LeTinteRestanoDentroILimiti(int chieste, int attese)
    {
        Assert.Equal(attese, new ParametriTracciato { NumeroColori = chieste }.Convalidato().NumeroColori);
    }

    [Fact]
    public void UnaTolleranzaAssurdaNonPassa()
    {
        Assert.Equal(0.1, new ParametriTracciato { Tolleranza = -3 }.Convalidato().Tolleranza, 6);
        Assert.Equal(12, new ParametriTracciato { Tolleranza = 999 }.Convalidato().Tolleranza, 6);
        Assert.Equal(0.1, new ParametriTracciato { Tolleranza = double.NaN }.Convalidato().Tolleranza, 6);
    }

    /// <summary>
    /// L'angolo si dichiara in gradi perche' e' cosi' che si ragiona guardando un disegno; quel che
    /// il motore confronta e' il coseno, e il verso conta: piu' gradi vuol dire coseno piu' piccolo.
    /// </summary>
    [Fact]
    public void LAngoloDiventaUnCoseno()
    {
        Assert.Equal(0.0, new ParametriTracciato { AngoloSpigolo = 90 }.CosenoSpigolo, 6);
        Assert.True(new ParametriTracciato { AngoloSpigolo = 120 }.CosenoSpigolo
                  < new ParametriTracciato { AngoloSpigolo = 60 }.CosenoSpigolo);
    }

    /// <summary>
    /// Una grandezza assurda -- zero, o un lato negativo -- non deve far esplodere il conto: si
    /// lavora quel che c'e' con la taratura com'e'.
    /// </summary>
    [Fact]
    public void UnaGrandezzaAssurdaNonRompeLaTaratura()
    {
        var s = ParametriTracciato.Predefiniti.PerImmagine(0, 0);

        Assert.True(s.Tolleranza > 0);
        Assert.True(s.NumeroColori >= 2);
    }
}

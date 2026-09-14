using StockStudio.Shared.Vettoriale;
using Xunit;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Che una campitura piatta resti piatta.
///
/// La stima delle sfumature serviva a rendere un'ombreggiatura con poche tinte, e faceva anche
/// l'opposto: stendeva una rampa su zone che sfumate non erano, e quel che si vedeva era una
/// velatura chiara dove il colore era pieno -- le "macchie di colore" sul tronco del castoro.
/// </summary>
public class SfumaturaTest
{
    [Fact]
    public void UnaCampituraPiattaNonDiventaSfumata()
    {
        // Il caso che rompeva: una zona grande, di colore uniforme, con dentro **dell'altro** --
        // qui delle righe scure, come le venature di un tronco. I due estremi della zona possono
        // risultare diversi per via di come cadono le righe, e la vecchia stima ci vedeva una
        // sfumatura. Il colore di fondo pero' non varia: una rampa non lo descrive meglio di una
        // tinta sola, e infatti non deve uscirne nessuna.
        const int w = 200, h = 200;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = 120; rgb[i * 3 + 1] = 80; rgb[i * 3 + 2] = 50;
        }
        for (var x = 20; x < w; x += 37)
            for (var y = 0; y < h; y++)
                for (var k = 0; k < 4 && x + k < w; k++)
                {
                    var p = ((y * w) + x + k) * 3;
                    rgb[p] = 60; rgb[p + 1] = 35; rgb[p + 2] = 20;
                }

        var t = Tavolozza.Riduci(rgb, w, h, 4);
        var rampe = Sfumatura.StimaTutte(rgb, t, w, h);

        foreach (var r in rampe)
            Assert.Null(r);
    }

    [Fact]
    public void UnaSfumaturaVeraRestaSfumata()
    {
        // Il controesempio, senza il quale il test sopra si supererebbe spegnendo la passata: una
        // zona il cui colore cambia davvero da un capo all'altro deve continuare a uscire come
        // rampa, altrimenti si e' risolto un difetto cancellando una funzione.
        const int w = 200, h = 200;
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = (y * w + x) * 3;
                var v = (byte)(40 + 170 * x / w);
                rgb[p] = v; rgb[p + 1] = (byte)(v * 0.7); rgb[p + 2] = (byte)(v * 0.5);
            }

        var t = Tavolozza.Riduci(rgb, w, h, 3);
        var rampe = Sfumatura.StimaTutte(rgb, t, w, h);

        var quante = 0;
        foreach (var r in rampe) if (r != null) quante++;
        Assert.True(quante > 0, "una sfumatura vera deve continuare a uscire come rampa");
    }

    [Fact]
    public void IGranelliNonMangianoPiuDiQuantoFacciaIllustrator()
    {
        // Il valore era 150, scelto per far quadrare il **numero di contorni** con quello di
        // Illustrator -- bersaglio sbagliato, perche' Illustrator arriva a quel numero tenendo
        // dettagli che noi buttavamo. Il comando equivalente di Adobe (minArea) vale 25 px
        // quadrati sui pixel veri; il nostro e' riferito a tremila pixel di lato.
        //
        // Il test fissa l'ordine di grandezza, non il numero preciso: su un'immagine grande quanto
        // il riferimento non si deve essere piu' di tre volte sopra Adobe.
        var p = ParametriTracciato.Predefiniti.PerImmagine(3000, 2000);
        Assert.InRange(p.Granelli, 8, 75);
    }
}

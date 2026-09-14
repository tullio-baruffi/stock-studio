using System;
using StockStudio.Shared.Vettoriale;
using Xunit;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Che la tavolozza consegni le tinte che le si chiedono, e che il pulviscolo non mangi i tratti.
///
/// Sono i due difetti che facevano divergere il tracciato da quello di Illustrator sulle
/// illustrazioni vere, e nessuno dei due dava errore: davano un disegno consegnato peggio.
/// </summary>
public class TavolozzaTinteTest
{
    [Fact]
    public void ChiedereTinteInPiuNeDaDiPiu()
    {
        // E' la proprieta' che mancava: la soglia di fusione era fissa, quindi le tinte chieste in
        // piu' nascevano vicine fra loro e venivano subito rimesse insieme. Il cursore delle tinte
        // era un comando che non comandava.
        var img = Sfumatura(240, 160);

        var poche = Tavolozza.Riduci(img, 240, 160, 8).Colori.Length;
        var tante = Tavolozza.Riduci(img, 240, 160, 40).Colori.Length;

        Assert.True(tante > poche,
            $"chiedendone 40 ne escono {tante}, chiedendone 8 ne escono {poche}");
    }

    [Fact]
    public void LaSogliaDelleGemelleScendeSoloQuandoSeNeChiedonoTante()
    {
        // Alzarla e' la direzione che ha gia' fatto danni -- a diciotto gli orsi perdevano il
        // volume -- quindi sotto la soglia di riferimento non deve cambiare niente.
        var aOtto = Tavolozza.SogliaGemelle(8);
        var aSedici = Tavolozza.SogliaGemelle(16);
        var aQuaranta = Tavolozza.SogliaGemelle(40);
        var aSessantaquattro = Tavolozza.SogliaGemelle(64);

        Assert.Equal(aOtto, aSedici);
        Assert.True(aQuaranta < aSedici, "sopra il riferimento la soglia deve stringersi");
        Assert.True(aSessantaquattro < aQuaranta, "e continuare a stringersi");
        Assert.True(aSessantaquattro >= 6, "ma non fino a tenere il rumore di quantizzazione");
    }

    [Fact]
    public void LaSogliaNonSuperaMaiQuellaDiSerie()
    {
        // Il tetto e' l'unica garanzia che questa legge non possa ripetere la regressione degli
        // orsi: qualunque numero di tinte, la soglia puo' solo scendere.
        for (var n = 2; n <= 64; n++)
            Assert.True(Tavolozza.SogliaGemelle(n) <= 12.0, $"con {n} tinte");
    }

    [Fact]
    public void IlGranelloVaAllaVicinaPiuSimileNonSoloAllaPiuEstesa()
    {
        // Il difetto del contorno a tratteggio, ridotto all'osso: una linea scura su fondo chiaro
        // con dentro una macchiolina di uno scuro appena diverso. La macchiolina confina quasi
        // tutta con la linea scura, ma il fondo chiaro e' enormemente piu' esteso.
        //
        // Con il criterio vecchio -- "va a chi confina di piu'" -- bastava che il chiaro toccasse
        // la macchia piu' della linea perche' se la prendesse, e nel contorno si apriva un buco.
        const int w = 40, h = 40;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = 245; rgb[i * 3 + 1] = 245; rgb[i * 3 + 2] = 245;
        }

        // Una linea orizzontale scura, spessa cinque pixel, per tutta la larghezza.
        for (var y = 18; y < 23; y++)
            for (var x = 0; x < w; x++)
            {
                var p = (y * w + x) * 3;
                rgb[p] = 30; rgb[p + 1] = 30; rgb[p + 2] = 34;
            }

        // Dentro la linea, un tratto di uno scuro leggermente diverso: e' il pezzo che la
        // riduzione a tinte stacca su un disegno dipinto a mano.
        for (var y = 19; y < 22; y++)
            for (var x = 16; x < 22; x++)
            {
                var p = (y * w + x) * 3;
                rgb[p] = 64; rgb[p + 1] = 62; rgb[p + 2] = 70;
            }

        var t = Tavolozza.Riduci(rgb, w, h, 6);
        Tavolozza.TogliIGranelli(t, w, h, 60);

        // Il centro del tratto deve essere rimasto scuro: che sia lo scuro della linea o quello
        // della macchia non importa -- importa che non sia diventato il fondo chiaro.
        var centro = t.Colori[t.Indici[20 * w + 19]];
        Assert.True(centro.R < 120,
            $"il pezzo di contorno e' diventato chiaro (R={centro.R}): il tratto si spezza");
    }

    [Fact]
    public void IlPulviscoloVeroSiToglieLoStesso()
    {
        // Il controesempio del precedente: una macchiolina isolata in mezzo a una campitura, di un
        // colore qualunque, deve sparire. Se il nuovo criterio la salvasse, si sarebbe scambiata
        // la rottura dei contorni con il ritorno del pulviscolo.
        const int w = 40, h = 40;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = 200; rgb[i * 3 + 1] = 120; rgb[i * 3 + 2] = 60;
        }
        for (var y = 19; y < 22; y++)
            for (var x = 19; x < 22; x++)
            {
                var p = (y * w + x) * 3;
                rgb[p] = 40; rgb[p + 1] = 40; rgb[p + 2] = 40;
            }

        var t = Tavolozza.Riduci(rgb, w, h, 4);
        var prima = t.Indici[20 * w + 20];
        Tavolozza.TogliIGranelli(t, w, h, 60);

        Assert.NotEqual(prima, t.Indici[20 * w + 20]);
    }

    [Fact]
    public void LaStoriaRaccontaDoveSiPerdonoLeTinte()
    {
        // Senza questa, "ne ho chieste quaranta e ne sono uscite undici" resta un'osservazione e
        // non diventa una diagnosi: i passaggi che accorciano la tavolozza sono quattro.
        var t = Tavolozza.Riduci(Sfumatura(200, 120), 200, 120, 24);
        var s = t.Storia;

        Assert.Equal(24, s.Chieste);
        Assert.True(s.DalTaglio > 0);
        Assert.True(s.DopoIDimenticati >= s.DalTaglio, "i dimenticati possono solo aggiungere");
        Assert.True(s.DopoLeGemelle <= s.DopoIDimenticati, "le fusioni possono solo togliere");
        Assert.True(s.DopoLeSpezzate <= s.DopoLeGemelle);
        Assert.True(s.DopoLeFrange <= s.DopoLeSpezzate);
        Assert.Equal(t.Colori.Length, s.DopoLeFrange);
        Assert.True(s.Gemelle > 0 && s.Gemelle <= 12);
    }

    /// <summary>Una sfumatura larga: tante tinte vicine fra loro, che e' il caso difficile.</summary>
    private static byte[] Sfumatura(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = (y * w + x) * 3;
                rgb[p] = (byte)(60 + 140 * x / w);
                rgb[p + 1] = (byte)(40 + 120 * x / w);
                rgb[p + 2] = (byte)(30 + 90 * y / h);
            }
        return rgb;
    }
}

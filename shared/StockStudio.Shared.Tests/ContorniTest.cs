using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Le invarianti del tracciato a confini condivisi.
///
/// Sono le promesse su cui poggia tutto il motore, e sono verificabili: che le tinte tassellino il
/// disegno, che ogni confine sia disegnato una volta sola, che il trasparente resti fuori. Finche'
/// erano solo misurate a mano un refactoring poteva romperle in silenzio.
/// </summary>
public class ContorniTest
{
    /// <summary>Una mappa di prova: due bande verticali, la sinistra tinta 0 e la destra tinta 1.</summary>
    private static byte[] DueBande(int w, int h)
    {
        var m = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                m[y * w + x] = (byte)(x < w / 2 ? 0 : 1);
        return m;
    }

    /// <summary>Una diagonale: e' il caso che produce la scalinata da raddrizzare.</summary>
    private static byte[] Diagonale(int w, int h)
    {
        var m = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                m[y * w + x] = (byte)(x * h < y * w ? 0 : 1);
        return m;
    }

    /// <summary>Un disco di tinta 1 dentro un fondo di tinta 0: serve a provare i buchi.</summary>
    private static byte[] Disco(int w, int h, int raggio)
    {
        var m = new byte[w * h];
        double cx = w / 2.0, cy = h / 2.0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var dx = x + 0.5 - cx; var dy = y + 0.5 - cy;
                m[y * w + x] = (byte)(dx * dx + dy * dy <= raggio * raggio ? 1 : 0);
            }
        return m;
    }

    /// <summary>Tre tinte a fasce: crea nodi veri, dove tre zone si incontrano sul bordo.</summary>
    private static byte[] TreFasce(int w, int h)
    {
        var m = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                m[y * w + x] = (byte)(y < h / 3 ? 0 : y < 2 * h / 3 ? 1 : 2);
        return m;
    }

    // ---- La promessa principale: le tinte tassellano ------------------------------------------

    [Theory]
    [InlineData("bande")]
    [InlineData("diagonale")]
    [InlineData("disco")]
    [InlineData("fasce")]
    public void OgniPixelEUnaTintaSola(string quale)
    {
        const int w = 60, h = 40;
        var (mappa, quante) = Mappa(quale, w, h);

        var contorni = Contorni.Estrai(mappa, null, w, h, quante);
        var coperture = Riempitore.Coperture(contorni, w, h);

        var sovrapposti = coperture.Count(c => c > 1);
        var scoperti = coperture.Count(c => c == 0);

        Assert.Equal(0, sovrapposti);
        Assert.Equal(0, scoperti);
    }

    /// <summary>
    /// La forma strutturale della stessa promessa: ogni confine viene percorso **una volta per
    /// verso**, e mai due volte nello stesso.
    ///
    /// E' il controllo che avrebbe preso subito l'errore vero commesso scrivendo questo motore --
    /// un solo segno di "gia' percorso" per spigolo invece di uno per verso -- che lasciava le
    /// zone col contorno a pezzi.
    /// </summary>
    [Theory]
    [InlineData("bande")]
    [InlineData("diagonale")]
    [InlineData("disco")]
    [InlineData("fasce")]
    public void OgniArcoEPercorsoUnaVoltaPerVerso(string quale)
    {
        const int w = 60, h = 40;
        var (mappa, quante) = Mappa(quale, w, h);

        var contorni = Contorni.Estrai(mappa, null, w, h, quante);
        var avanti = new int[contorni.Archi.Count];
        var indietro = new int[contorni.Archi.Count];

        foreach (var anelli in contorni.Zone)
            foreach (var anello in anelli)
                foreach (var passo in anello.Passi)
                    if (passo.Inverso) indietro[passo.Arco]++; else avanti[passo.Arco]++;

        for (var i = 0; i < contorni.Archi.Count; i++)
        {
            Assert.True(avanti[i] <= 1, $"arco {i} percorso {avanti[i]} volte in avanti");
            Assert.True(indietro[i] <= 1, $"arco {i} percorso {indietro[i]} volte all'indietro");
            // Un arco che nessuno percorre e' geometria scritta e mai usata.
            Assert.True(avanti[i] + indietro[i] >= 1, $"arco {i} non e' nel contorno di nessuna zona");
        }
    }

    /// <summary>
    /// Un confine interno appartiene a due zone, e le due lo percorrono in versi opposti: e' questo
    /// che fa combaciare le campiture invece di sovrapporle.
    ///
    /// Sul bordo della tavola no: di la' non c'e' nessuna zona, quindi quel tratto ha un verso solo.
    /// </summary>
    [Fact]
    public void UnConfineInternoEPercorsoDaEntrambiILati()
    {
        const int w = 40, h = 30;
        var contorni = Contorni.Estrai(DueBande(w, h), null, w, h, 2);

        var versi = new Dictionary<int, (int Avanti, int Indietro)>();
        foreach (var anelli in contorni.Zone)
            foreach (var anello in anelli)
                foreach (var passo in anello.Passi)
                {
                    versi.TryGetValue(passo.Arco, out var v);
                    versi[passo.Arco] = passo.Inverso ? (v.Avanti, v.Indietro + 1)
                                                      : (v.Avanti + 1, v.Indietro);
                }

        // Il confine fra le due bande e' verticale, lungo tutta l'altezza, e non tocca nessuna
        // terza zona: dev'esserci almeno un arco percorso in tutti e due i versi.
        Assert.Contains(versi, kv => kv.Value.Avanti == 1 && kv.Value.Indietro == 1);
    }

    // ---- Fedelta' ----------------------------------------------------------------------------

    /// <summary>
    /// Riempiendo i contorni si deve riottenere la mappa di partenza. La lisciatura sposta i bordi
    /// di meno di un pixel, quindi qualche pixel di confine puo' cambiare tinta; il grosso no.
    /// </summary>
    [Theory]
    [InlineData("bande")]
    [InlineData("diagonale")]
    [InlineData("disco")]
    public void RicostruisceLaMappaDiPartenza(string quale)
    {
        const int w = 60, h = 40;
        var (mappa, quante) = Mappa(quale, w, h);

        var contorni = Contorni.Estrai(mappa, null, w, h, quante);
        var ricostruita = new int[w * h];
        for (var i = 0; i < ricostruita.Length; i++) ricostruita[i] = -1;
        for (var z = 0; z < quante; z++)
        {
            if (contorni.Zone[z].Count == 0) continue;
            var dentro = Riempitore.Riempi(contorni, contorni.Zone[z], w, h);
            for (var i = 0; i < dentro.Length; i++) if (dentro[i]) ricostruita[i] = z;
        }

        var uguali = 0;
        for (var i = 0; i < mappa.Length; i++) if (ricostruita[i] == mappa[i]) uguali++;
        var fedelta = (double)uguali / mappa.Length;
        Assert.True(fedelta > 0.97, $"fedelta' {fedelta:P2}, troppo bassa");
    }

    // ---- Buchi -------------------------------------------------------------------------------

    /// <summary>
    /// Il fondo attorno a un disco deve restare vuoto **sotto** il disco: il suo contorno ha un
    /// buco, e i due anelli girano in versi opposti perche' la regola nonzero lo svuoti.
    /// </summary>
    [Fact]
    public void IlFondoNonCopreCioCheContiene()
    {
        const int w = 60, h = 60;
        var contorni = Contorni.Estrai(Disco(w, h, 15), null, w, h, 2);

        var fondo = Riempitore.Riempi(contorni, contorni.Zone[0], w, h);
        var centro = (h / 2) * w + (w / 2);
        Assert.False(fondo[centro], "il fondo copre anche il disco: il buco non e' stato vuotato");
        Assert.True(fondo[0], "il fondo non copre l'angolo della tavola");
    }

    // ---- Trasparenza -------------------------------------------------------------------------

    /// <summary>
    /// Dove l'immagine e' trasparente non si traccia niente: nessuna zona deve coprire quei pixel.
    /// E' il difetto che faceva uscire un logo ritagliato su fondo nero.
    /// </summary>
    [Fact]
    public void IlTrasparenteRestaVuoto()
    {
        const int w = 60, h = 60;
        var mappa = Disco(w, h, 18);
        // Opaco solo dentro il disco: fuori non c'e' disegno.
        var opaco = new bool[w * h];
        for (var i = 0; i < mappa.Length; i++) opaco[i] = mappa[i] == 1;

        var contorni = Contorni.Estrai(mappa, opaco, w, h, 2);
        var coperture = Riempitore.Coperture(contorni, w, h);

        Assert.Equal(0, coperture[0]);                       // angolo della tavola: trasparente
        Assert.True(coperture[(h / 2) * w + (w / 2)] >= 1);  // centro del disco: disegnato

        // E niente si sovrappone nemmeno qui.
        Assert.Equal(0, coperture.Count(c => c > 1));
    }

    // ---- Casi limite -------------------------------------------------------------------------

    [Fact]
    public void UnaTintaSolaNonProduceContorniInterni()
    {
        const int w = 20, h = 20;
        var contorni = Contorni.Estrai(new byte[w * h], null, w, h, 1);
        // Solo il bordo della tavola.
        Assert.Single(contorni.Zone[0]);
        var coperture = Riempitore.Coperture(contorni, w, h);
        Assert.Equal(0, coperture.Count(c => c != 1));
    }

    [Fact]
    public void UnImmagineVuotaNonRompe()
    {
        var contorni = Contorni.Estrai(new byte[0], null, 0, 0, 2);
        Assert.Empty(contorni.Archi);
        Assert.Equal(2, contorni.Zone.Length);
    }

    /// <summary>
    /// Le curve non devono uscire dalla tavola: il bordo dell'immagine e' tenuto fermo apposta,
    /// perche' una lisciatura che lo muovesse lascerebbe i lati ondulati.
    /// </summary>
    [Fact]
    public void IlBordoDellaTavolaRestaDritto()
    {
        const int w = 40, h = 30;
        var contorni = Contorni.Estrai(Diagonale(w, h), null, w, h, 2);

        foreach (var arco in contorni.Archi)
        {
            Verifica(arco.Inizio);
            Verifica(arco.Fine);
            foreach (var c in arco.Cubiche) { Verifica(c[0]); Verifica(c[1]); Verifica(c[2]); }
        }

        void Verifica(Punto p)
        {
            Assert.InRange(p.X, -0.001, w + 0.001);
            Assert.InRange(p.Y, -0.001, h + 0.001);
        }
    }

    /// <summary>
    /// L'angolo della tavola non si taglia, **nemmeno con la tolleranza larga**.
    ///
    /// E' un difetto vero, e insidioso perche' si vedeva solo da un lato: la ricerca degli spigoli
    /// guardava la direzione fra punti distanti tre passi, e quindi non poteva esaminare i primi e
    /// gli ultimi tre punti di un arco. Sull'angolo della tavola il contorno gira di novanta gradi a
    /// un passo dall'estremo: nessuno lo vedeva, la curva tagliava dritta, e il pixel di spigolo
    /// restava fuori da ogni campitura -- un puntino di fondo nell'angolo del disegno.
    ///
    /// Prima non si notava perche' l'adattamento partiva sempre da quattro tratti, e uno dei nodi
    /// capitava li' vicino per caso. Appena si e' smesso di sprecare quei nodi, il buco e' uscito.
    ///
    /// Si prova anche con la tolleranza al massimo perche' e' li' che la curva ha piu' licenza di
    /// allontanarsi: se l'angolo regge con quella, regge con tutte.
    /// </summary>
    [Theory]
    [InlineData(0.6)]
    [InlineData(2.7)]
    [InlineData(12.0)]
    public void LAngoloDellaTavolaNonSiTaglia(double tolleranza)
    {
        const int w = 60, h = 40;
        var contorni = Contorni.Estrai(Diagonale(w, h), null, w, h, 2,
                                       new ParametriTracciato { Tolleranza = tolleranza });

        var coperture = Riempitore.Coperture(contorni, w, h);

        Assert.Equal(0, coperture.Count(c => c == 0));
        Assert.Equal(0, coperture.Count(c => c > 1));
    }

    /// <summary>
    /// Una tolleranza piu' larga deve costare **meno** nodi, non di piu': e' tutto quello che quel
    /// numero promette, ed e' il motivo per cui esiste.
    /// </summary>
    [Fact]
    public void UnaTolleranzaPiuLargaCostaMenoNodi()
    {
        const int w = 120, h = 120;
        var stretta = Nodi(0.6);
        var larga = Nodi(4.0);

        Assert.True(larga < stretta, $"tolleranza 4,0 ha dato {larga} nodi contro i {stretta} di 0,6");

        int Nodi(double t)
        {
            var c = Contorni.Estrai(Disco(w, h, 40), null, w, h, 2,
                                    new ParametriTracciato { Tolleranza = t });
            return c.Archi.Sum(a => a.Cubiche.Count);
        }
    }

    /// <summary>
    /// Un arco dolce si descrive con **poche** curve.
    ///
    /// L'adattamento partiva da quattro tratti sempre, e da li' i nodi potevano solo crescere: un
    /// cerchio, che di curve ne vuole quattro in tutto, ne riceveva quattro per ciascuno dei tratti
    /// in cui era stato spezzato. Il tetto qui e' largo -- non si sta misurando la taratura, si sta
    /// verificando che il minimo sia il minimo e non un numero fisso.
    /// </summary>
    [Fact]
    public void UnCerchioNonCostaDecineDiCurve()
    {
        const int w = 200, h = 200;
        var contorni = Contorni.Estrai(Disco(w, h, 70), null, w, h, 2,
                                       new ParametriTracciato { Tolleranza = 2.7 });

        // Il contorno del disco: l'arco che non corre sul bordo della tavola.
        var disco = contorni.Archi
            .Where(a => a.Inizio.X > 2 && a.Inizio.Y > 2 && a.Inizio.X < w - 2 && a.Inizio.Y < h - 2)
            .Sum(a => a.Cubiche.Count);

        Assert.InRange(disco, 1, 16);
    }

    /// <summary>
    /// Le cubiche devono davvero descrivere la forma.
    ///
    /// Un difetto vero e' passato inosservato proprio qui: un anello chiuso finiva in una sola
    /// cubica di lunghezza zero -- la figura spariva dal disegno -- e nessun controllo se ne
    /// accorgeva, perche' le zone restavano intatte e si guardavano quelle. Le zone dicono quali
    /// confini esistono; sono le cubiche a essere consegnate al cliente.
    /// </summary>
    [Fact]
    public void LeCubicheDescrivonoLaForma()
    {
        const int w = 200, h = 200, raggio = 60;
        var contorni = Contorni.Estrai(Disco(w, h, raggio), null, w, h, 2);

        // Il contorno del disco: l'arco che non corre sul bordo della tavola.
        var disco = contorni.Archi
            .Where(a => a.Inizio.X > 2 && a.Inizio.Y > 2 && a.Inizio.X < w - 2 && a.Inizio.Y < h - 2)
            .ToList();
        Assert.NotEmpty(disco);

        var lunghezza = 0.0;
        var punti = new List<Punto>();
        foreach (var arco in disco)
        {
            var p = arco.Inizio;
            foreach (var c in arco.Cubiche)
            {
                for (var i = 1; i <= 8; i++)
                {
                    var t = i / 8.0; var mt = 1 - t;
                    punti.Add(new Punto(
                        mt*mt*mt*p.X + 3*mt*mt*t*c[0].X + 3*mt*t*t*c[1].X + t*t*t*c[2].X,
                        mt*mt*mt*p.Y + 3*mt*mt*t*c[0].Y + 3*mt*t*t*c[1].Y + t*t*t*c[2].Y));
                }
                lunghezza += Math.Sqrt((c[2].X - p.X) * (c[2].X - p.X) + (c[2].Y - p.Y) * (c[2].Y - p.Y));
                p = c[2];
            }
        }

        // Il giro deve misurare quanto la circonferenza, non zero.
        var atteso = 2 * Math.PI * raggio;
        Assert.InRange(lunghezza, atteso * 0.85, atteso * 1.15);

        // E ogni suo punto deve stare sul cerchio, non altrove.
        foreach (var q in punti)
        {
            var r = Math.Sqrt((q.X - w / 2.0) * (q.X - w / 2.0) + (q.Y - h / 2.0) * (q.Y - h / 2.0));
            Assert.InRange(r, raggio - 2.0, raggio + 2.0);
        }
    }

    private static (byte[] Mappa, int Quante) Mappa(string quale, int w, int h)
    {
        return quale switch
        {
            "bande" => (DueBande(w, h), 2),
            "diagonale" => (Diagonale(w, h), 2),
            "disco" => (Disco(w, h, 12), 2),
            "fasce" => (TreFasce(w, h), 3),
            _ => throw new ArgumentException(quale),
        };
    }
}

using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Le promesse della modalita', e in particolare quella nuova.
///
/// Questi test nascono da un difetto costoso e silenzioso: chi caricava senza toccare niente
/// otteneva una **silhouette in bianco e nero**, perche' la pagina partiva su quella modalita' e
/// la normalizzazione mandava a "bianco e nero" tutto cio' che non riconosceva -- mentre il
/// contratto di coda dichiarava che vuoto valesse "automatico". Le due letture divergevano, e il
/// risultato non era un errore ma un disegno a tinta unita consegnato al cliente.
/// </summary>
public class ModalitaTest
{
    /// <summary>
    /// Non aver detto niente vuol dire "guarda tu", non "fammi una sagoma".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("qualcosa-che-non-esiste")]
    public void ChiNonDiceNienteOttieneLAutomatico(string? mode)
    {
        Assert.Equal(Modalita.Automatico, Modalita.Normalizza(mode));
    }

    [Theory]
    [InlineData("vector", Modalita.BiancoENero)]
    [InlineData("VECTOR", Modalita.BiancoENero)]
    [InlineData("bn", Modalita.BiancoENero)]
    [InlineData("colore", Modalita.Colore)]
    [InlineData("color", Modalita.Colore)]
    [InlineData("vector-color", Modalita.Colore)]
    [InlineData("raster", Modalita.Raster)]
    [InlineData("auto", Modalita.Automatico)]
    public void LeModalitaDichiarateSiRiconoscono(string mode, string atteso)
    {
        Assert.Equal(atteso, Modalita.Normalizza(mode));
    }

    /// <summary>
    /// L'automatico produce curve, qualunque delle due strade prenda: per le regole di Adobe e per
    /// la validazione e' un vettoriale come gli altri.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("vector")]
    [InlineData("colore")]
    [InlineData(null)]
    public void LAutomaticoEUnVettoriale(string? mode)
    {
        Assert.True(Modalita.EVettoriale(mode));
    }

    [Fact]
    public void LImmagineNonEUnVettoriale()
    {
        Assert.False(Modalita.EVettoriale("raster"));
    }

    /// <summary>
    /// "A colori" vuol dire **chiesto** a colori. L'automatico non lo e': li' il colore e'
    /// possibile ma non deciso, e chi vuole sapere com'e' finita deve guardare l'esito.
    /// </summary>
    [Fact]
    public void SoloIlColoreChiestoContaComeColore()
    {
        Assert.True(Modalita.EAColori("colore"));
        Assert.False(Modalita.EAColori("auto"));
        Assert.False(Modalita.EAColori("vector"));
        Assert.False(Modalita.EAColori(null));
    }

    /// <summary>
    /// Quel che il motore legge davvero: imposto, imposto al contrario, oppure "guarda tu".
    /// Null e' la risposta che lascia decidere all'immagine, ed e' il caso normale.
    /// </summary>
    [Fact]
    public void SoloLeImposizioniScavalcanoLImmagine()
    {
        Assert.True(Modalita.ColoreImposto("colore"));
        Assert.False(Modalita.ColoreImposto("vector"));
        Assert.Null(Modalita.ColoreImposto("auto"));
        Assert.Null(Modalita.ColoreImposto(null));
        Assert.Null(Modalita.ColoreImposto(""));
    }
}

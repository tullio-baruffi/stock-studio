using StockStudio.Shared.Pipeline;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Le promesse dello stato di pipeline.
///
/// Lo stato decide quali pulsanti compaiono, quindi sbagliarlo non e' un difetto estetico: e' la
/// differenza fra un file che parte una volta e uno che parte due.
/// </summary>
public class StatoPipelineTest
{
    private static string Stato(string library, bool invia, bool inviato, string? testo = null)
        => StatoPipeline.Di(library, invia, inviato, testo).Stato;

    [Fact]
    public void AppenaRevisionatoEDaRevisionare()
    {
        Assert.Equal(StatoPipeline.InRevisione, Stato("ImagesToClassify", false, false));
    }

    [Fact]
    public void RevisionatoEPronto()
    {
        Assert.Equal(StatoPipeline.Pronto, Stato("ImagesToSend", false, false));
    }

    [Fact]
    public void ChiestoMaNonPartitoEInAttesa()
    {
        Assert.Equal(StatoPipeline.InAttesa, Stato("ImagesToSend", true, false));
    }

    /// <summary>
    /// Il caso per cui questo stato esiste.
    ///
    /// Chi accoda -- la Logic App di sorveglianza o la pubblicazione immediata -- scrive "Inviato"
    /// **prima** di mettere il messaggio in coda, per impedire che il giro successivo riprenda lo
    /// stesso file. Fra quel momento e lo spostamento in ImagesSent il file resta nella libreria di
    /// partenza con tutti e due i contrassegni alzati.
    ///
    /// Finche' questo stato non c'era, quel caso ricadeva su "pronto": l'applicazione rimostrava
    /// "Invia ai marketplace" su un file che stava gia' salendo.
    /// </summary>
    [Fact]
    public void PresoInCaricoMaNonAncoraSpostatoEInConsegna()
    {
        Assert.Equal(StatoPipeline.InConsegna, Stato("ImagesToSend", true, true));
    }

    /// <summary>
    /// A significare "preso in carico" e' "Inviato" da solo.
    ///
    /// Chi vuole annullare un invio gia' partito e' tentato di togliere la spunta a "Invia"
    /// direttamente in SharePoint. Quel gesto non ferma niente -- il messaggio e' gia' in coda -- ma
    /// finche' lo stato chiedeva tutti e due i contrassegni, il file tornava a mostrarsi "pronto"
    /// con i pulsanti riattivati, che poi fallivano sempre.
    /// </summary>
    [Fact]
    public void ResteInConsegnaAncheSeQualcunoAbbassaIlContrassegnoDiInvio()
    {
        Assert.Equal(StatoPipeline.InConsegna, Stato("ImagesToSend", false, true));
        Assert.False(StatoPipeline.SiPuoInviare(Stato("ImagesToSend", false, true)));
        Assert.False(StatoPipeline.SiPuoForzare(Stato("ImagesToSend", false, true)));
    }

    /// <summary>Le quattro combinazioni della libreria d'invio, per intero e senza buchi.</summary>
    [Theory]
    [InlineData(false, false, StatoPipeline.Pronto)]
    [InlineData(true, false, StatoPipeline.InAttesa)]
    [InlineData(true, true, StatoPipeline.InConsegna)]
    [InlineData(false, true, StatoPipeline.InConsegna)]
    public void OgniCombinazioneDeiContrassegniHaUnoStato(bool invia, bool inviato, string atteso)
    {
        Assert.Equal(atteso, Stato("ImagesToSend", invia, inviato));
    }

    [Fact]
    public void NellaLibreriaDeiPubblicatiEPubblicato()
    {
        Assert.Equal(StatoPipeline.Pubblicato, Stato("ImagesSent", true, true, "COMPLETATO: caricato su Freepik"));
    }

    [Fact]
    public void UnErroreBatteQualunqueAltroSegnale()
    {
        Assert.Equal(StatoPipeline.Errore, Stato("ImagesToSend", true, true, "ERRORE: 500 - qualcosa"));
        Assert.Equal(StatoPipeline.Errore, Stato("ImagesSent", true, true, "ERRORE: 500 - qualcosa"));
    }

    /// <summary>
    /// Si invia da fermo o dopo un fallimento, mai da uno stato di passaggio: sono proprio quelli
    /// in cui premere due volte fa partire due copie.
    /// </summary>
    [Fact]
    public void SiInviaSoloDaFermoODopoUnErrore()
    {
        Assert.True(StatoPipeline.SiPuoInviare(StatoPipeline.Pronto));
        Assert.True(StatoPipeline.SiPuoInviare(StatoPipeline.Errore));

        Assert.False(StatoPipeline.SiPuoInviare(StatoPipeline.InAttesa));
        Assert.False(StatoPipeline.SiPuoInviare(StatoPipeline.InConsegna));
        Assert.False(StatoPipeline.SiPuoInviare(StatoPipeline.Pubblicato));
        Assert.False(StatoPipeline.SiPuoInviare(StatoPipeline.InRevisione));
    }

    /// <summary>
    /// Forzare si puo' anche da "in attesa" -- e' il caso per cui il pulsante esiste -- ma mai da
    /// "in pubblicazione", dove il messaggio e' gia' in coda e un secondo lo duplicherebbe.
    /// </summary>
    [Fact]
    public void SiForzaAncheDallAttesaMaMaiDaUnaConsegnaInCorso()
    {
        Assert.True(StatoPipeline.SiPuoForzare(StatoPipeline.InAttesa));
        Assert.True(StatoPipeline.SiPuoForzare(StatoPipeline.Pronto));
        Assert.True(StatoPipeline.SiPuoForzare(StatoPipeline.Errore));

        Assert.False(StatoPipeline.SiPuoForzare(StatoPipeline.InConsegna));
        Assert.False(StatoPipeline.SiPuoForzare(StatoPipeline.Pubblicato));
        Assert.False(StatoPipeline.SiPuoForzare(StatoPipeline.InRevisione));
    }

    /// <summary>
    /// I nomi delle librerie arrivano dalla querystring e non hanno una forma garantita.
    /// </summary>
    [Fact]
    public void IlNomeDellaLibreriaNonDistingueMaiuscoleEMinuscole()
    {
        Assert.Equal(StatoPipeline.InConsegna, Stato("imagestosend", true, true));
        Assert.Equal(StatoPipeline.Pubblicato, Stato("IMAGESSENT", true, true));
    }
}

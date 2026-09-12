using StockStudio.Api.Services.Integration;
using StockStudio.Api.Services.Scoring;

namespace StockStudio.Api.Services;

/// <summary>
/// Dove si trova un'immagine lungo la pipeline, detto in un posto solo.
///
/// Lo stato non e' scritto da nessuna parte: vive sparso fra la libreria in cui il file si trova,
/// due caselle di spunta e una colonna di testo libero. Dedurlo nel frontend significherebbe
/// riscrivere quelle regole in ogni schermata che le mostra, e vederle divergere alla prima
/// modifica. Qui si legge una volta e si spedisce gia' risolto.
/// </summary>
public static class StatoPipeline
{
    public const string InRevisione = "revisione";
    public const string Pronto = "pronto";
    public const string InAttesa = "in-attesa";
    public const string Pubblicato = "pubblicato";
    public const string Errore = "errore";

    /// <summary>
    /// Lo stato, e la frase da mostrare a chi guarda.
    ///
    /// L'ordine dei controlli e' la regola: un errore conta piu' di tutto, perche' e' la sola cosa
    /// su cui si debba intervenire; poi viene l'attesa, perche' un invio gia' chiesto non si chiede
    /// due volte; e solo alla fine la libreria in cui il file sta.
    /// </summary>
    public static (string Stato, string Etichetta, string Spiega) Di(
        string library, bool invia, bool inviato, string? stato)
    {
        var testo = stato ?? string.Empty;

        if (testo.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase))
            return (Errore, "Non riuscito",
                    "L'ultimo tentativo di pubblicazione e' fallito. Il file resta qui e si puo' ritentare.");

        // Chiesto ma non ancora partito: e' lo stato che prima non si vedeva, e per cui si finiva a
        // premere "Invia" una seconda volta credendo che il primo non avesse funzionato.
        if (invia && !inviato)
            return (InAttesa, "In attesa di invio",
                    "L'invio e' stato chiesto. La pipeline prende in carico il file entro pochi minuti, oppure si puo' forzare subito.");

        if (string.Equals(library, "ImagesSent", StringComparison.OrdinalIgnoreCase))
            return (Pubblicato, "Pubblicato",
                    testo.StartsWith("COMPLETATO", StringComparison.OrdinalIgnoreCase) && testo.Contains("PARZIALE")
                        ? "Consegnato solo ad alcune destinazioni: il dettaglio e' nello stato."
                        : "Consegnato ai marketplace.");

        if (string.Equals(library, "ImagesToSend", StringComparison.OrdinalIgnoreCase))
            return (Pronto, "Pronto per l'invio",
                    "Rivisto e pronto: manca solo di mandarlo ai marketplace.");

        return (InRevisione, "Da revisionare",
                "Metadati generati, in attesa di revisione.");
    }
}

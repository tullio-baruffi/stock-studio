namespace StockStudio.Shared.Pipeline;

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
    public const string InConsegna = "in-consegna";
    public const string Pubblicato = "pubblicato";
    public const string Errore = "errore";

    /// <summary>
    /// Lo stato, e la frase da mostrare a chi guarda.
    ///
    /// L'ordine dei controlli e' la regola: un errore conta piu' di tutto, perche' e' la sola cosa
    /// su cui si debba intervenire; poi vengono i due stati di passaggio, perche' un invio gia'
    /// chiesto non si chiede due volte; e solo alla fine la libreria in cui il file sta.
    /// </summary>
    public static (string Stato, string Etichetta, string Spiega) Di(
        string library, bool invia, bool inviato, string? stato)
    {
        var testo = stato ?? string.Empty;
        var daInviare = string.Equals(library, "ImagesToSend", System.StringComparison.OrdinalIgnoreCase);
        var inviate = string.Equals(library, "ImagesSent", System.StringComparison.OrdinalIgnoreCase);

        // Gia' preso in carico ma ancora qui: chi accoda scrive "Inviato" **prima** di accodare, per
        // impedire che il giro successivo lo riprenda, e il file resta nella libreria di partenza
        // finche' non viene spostato. Senza questo stato ricadeva su "pronto", e l'applicazione
        // rimostrava il pulsante "Invia ai marketplace" su un file gia' in consegna.
        //
        // Basta "Inviato" da solo, senza guardare "Invia": e' quello il contrassegno che significa
        // preso in carico -- e' su quello che filtra la sorveglianza, ed e' quello che impedisce
        // ogni ulteriore scrittura. Chiedere anche "Invia" lasciava scoperta la combinazione in cui
        // qualcuno abbassa "Invia" a mano credendo di annullare un invio gia' partito: il file
        // tornava a mostrarsi "pronto" con i pulsanti attivi, che poi fallivano sempre.
        //
        // Viene **prima** del testo di errore, e non e' un dettaglio d'ordine: i contrassegni
        // dicono cosa sta succedendo adesso, la colonna di testo racconta il tentativo precedente.
        // Un file rimesso in coda dopo un fallimento porta ancora scritto "ERRORE", e finche' era
        // quella frase a decidere l'applicazione lo mostrava come "non riuscito" con i pulsanti
        // riattivati -- su un file che stava gia' risalendo. A fermare il doppione restava solo il
        // rifiuto dello store, cioe' l'ultima barriera invece della prima.
        if (daInviare && inviato)
            return (InConsegna, "In pubblicazione",
                    "Preso in carico: sta salendo ai marketplace. Non si puo' rimandare finche' non ha finito.");

        // Chiesto ma non ancora partito: e' lo stato che prima non si vedeva, e per cui si finiva a
        // premere "Invia" una seconda volta credendo che il primo non avesse funzionato. Anche
        // questo batte il testo di errore, per la stessa ragione: un invio appena richiesto e' una
        // notizia piu' fresca di un fallimento gia' archiviato.
        if (invia && !inviato)
            return (InAttesa, "In attesa di invio",
                    "L'invio e' stato chiesto. La pipeline prende in carico il file entro pochi minuti, oppure si puo' forzare subito.");

        if (testo.StartsWith("ERRORE", System.StringComparison.OrdinalIgnoreCase))
            return (Errore, "Non riuscito",
                    "L'ultimo tentativo di pubblicazione e' fallito. Il file resta qui e si puo' ritentare.");

        if (inviate)
            return (Pubblicato, "Pubblicato",
                    testo.StartsWith("COMPLETATO", System.StringComparison.OrdinalIgnoreCase) && testo.Contains("PARZIALE")
                        ? "Consegnato solo ad alcune destinazioni: il dettaglio e' nello stato."
                        : "Consegnato ai marketplace.");

        if (daInviare)
            return (Pronto, "Pronto per l'invio",
                    "Rivisto e pronto: manca solo di mandarlo ai marketplace.");

        return (InRevisione, "Da revisionare",
                "Metadati generati, in attesa di revisione.");
    }

    /// <summary>
    /// Se in questo stato ha senso chiedere un invio.
    ///
    /// Vale sia per il pulsante sia per il controllo lato server: sono la stessa domanda, e tenerne
    /// due versioni vorrebbe dire vederle divergere alla prima modifica.
    /// </summary>
    public static bool SiPuoInviare(string stato)
    {
        return stato == Pronto || stato == Errore;
    }

    /// <summary>
    /// Se in questo stato ha senso forzare la partenza immediata.
    ///
    /// Anche da "in attesa": e' proprio il caso per cui il pulsante esiste, cioe' un invio gia'
    /// chiesto che non si vuole aspettare. Da "in pubblicazione" no, perche' li' il file e' gia'
    /// nelle mani della coda.
    /// </summary>
    public static bool SiPuoForzare(string stato)
    {
        return stato == Pronto || stato == InAttesa || stato == Errore;
    }
}

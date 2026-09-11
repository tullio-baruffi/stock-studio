namespace StockStudio.Api.Services.Feedback;

/// <summary>
/// Le keyword che il magazzino ha ormai consumato.
///
/// Il generatore vede un'immagine per volta e non può sapere che è la tremillesima silhouette:
/// guardandola in isolamento "silhouette" è il soggetto, ed è giusto che la proponga per prima.
/// È giusto per quell'immagine e sbagliato per la libreria, dove quella parola compare sul cento
/// per cento dei file e quindi non ne distingue nessuno.
///
/// Questo è l'unico pezzo di contesto che il modello non può ricavare da solo, e per questo va
/// misurato fuori e passato dentro. La misura arriva dallo strumento di riordino, che la calcola
/// già per conto suo: qui viene solo tenuta da parte perché il prompt possa leggerla.
///
/// ## Perché in memoria e non su disco
/// È un dato derivato: si ricava di nuovo in una lettura di libreria, e se il servizio riparte
/// prima che qualcuno abbia aperto lo strumento, il prompt semplicemente torna a funzionare come
/// prima. Non vale la scrittura su disco né il rischio di lavorare su una misura vecchia di mesi.
/// </summary>
public class KeywordSaturation
{
    private readonly object _gate = new();
    private IReadOnlyList<string> _sature = Array.Empty<string>();
    private string _libreria = "";
    private int _campione;
    private DateTime? _quando;

    public void Aggiorna(string libreria, int campione, IEnumerable<string> sature)
    {
        lock (_gate)
        {
            // Poche e in ordine di diffusione: un elenco lungo diluirebbe l'istruzione invece di
            // rafforzarla, e le prime sono quelle che pesano davvero.
            _sature = sature.Take(20).ToList();
            _libreria = libreria;
            _campione = campione;
            _quando = DateTime.UtcNow;
        }
    }

    public (IReadOnlyList<string> Sature, string Libreria, int Campione, DateTime? Quando) Stato
    {
        get { lock (_gate) return (_sature, _libreria, _campione, _quando); }
    }

    /// <summary>Il blocco da aggiungere al prompt, o stringa vuota se non è stato misurato nulla.</summary>
    public string PromptBlock
    {
        get
        {
            var s = Stato;
            if (s.Sature.Count == 0) return "";

            return "\n\nSATURATED KEYWORDS. These terms already appear on most of this author's "
                 + $"library ({s.Campione} files examined), so they no longer tell one asset from "
                 + "another and a buyer typing them gets an undifferentiated wall of results. "
                 + "Include them further down the list when they genuinely apply, but NEVER inside "
                 + "the first seven: those positions must go to what makes THIS image different "
                 + "from the others. The saturated terms are: "
                 + string.Join(", ", s.Sature) + ".";
        }
    }
}

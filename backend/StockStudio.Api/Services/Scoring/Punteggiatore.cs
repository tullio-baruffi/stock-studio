using Microsoft.Extensions.Options;
using StockStudio.Api.Services.Integration;

namespace StockStudio.Api.Services.Scoring;

/// <summary>
/// L'unica definizione di "quanto vale questa immagine".
///
/// Sta in un servizio suo perché lo stesso numero serve in tre posti -- la galleria che lo mostra,
/// la lettura del singolo elemento, e il riempimento che lo deposita in libreria -- e se ognuno se
/// lo calcolasse per conto proprio basterebbe una differenza minuscola nel raggruppamento per far
/// comparire nella colonna un numero diverso da quello a schermo. Nessuno se ne accorgerebbe finché
/// non si filtra, e a quel punto il filtro escluderebbe cose che sembrano dentro.
/// </summary>
public class Punteggiatore
{
    private readonly StockValidator _validator;
    private readonly string _siteRoot;

    public Punteggiatore(StockValidator validator, IOptions<PipelineSettings> s)
    {
        _validator = validator;
        _siteRoot = string.IsNullOrWhiteSpace(s.Value.SiteUrl)
            ? ""
            : new Uri(s.Value.SiteUrl!).AbsolutePath.TrimEnd('/');
    }

    /// <summary>Cartella che contiene il file, come percorso server-relative.</summary>
    public static string CartellaDi(string serverRelativeUrl)
    {
        var slash = serverRelativeUrl.LastIndexOf('/');
        return slash <= 0 ? "" : serverRelativeUrl[..slash];
    }

    /// <summary>
    /// True quando la cartella è una sottocartella della libreria, cioè un gruppo di consegna.
    ///
    /// La radice non è un gruppo: è dove arrivano le immagini singole del percorso precedente, e
    /// trattarla come tale unirebbe l'intera libreria in una riga sola.
    /// </summary>
    public bool CartellaDiGruppo(string library, string folder) =>
        folder.Length > 0
        && !string.Equals(folder.TrimEnd('/'), $"{_siteRoot}/{library}", StringComparison.OrdinalIgnoreCase);

    public static bool EImmagineRaster(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant()
            is ".jpg" or ".jpeg" or ".png" or ".webp" or ".tif" or ".tiff" or ".bmp" or ".gif";

    /// <summary>
    /// Se il gruppo è una consegna vettoriale o un raster, guardando i file che lo compongono.
    ///
    /// Prima qui c'era scritto "vector" per tutti, e la validazione chiedeva anche alle fotografie
    /// le keyword 'vector' e 'graphic'. Su una foto quelle due parole non sono un miglioramento:
    /// sono keyword irrilevanti, cioè uno dei motivi di rifiuto dichiarati da Adobe.
    /// </summary>
    public static string ModoDi(IReadOnlyList<SharePointItem> gruppo) =>
        gruppo.Any(x => Path.GetExtension(x.FileName).ToLowerInvariant() is ".eps" or ".svg" or ".ai")
            ? "vector"
            : "raster";

    /// <summary>
    /// Una riga per immagine, non per file.
    ///
    /// Il percorso durevole deposita SVG, EPS e JPEG nella stessa cartella e con lo stesso nome:
    /// mostrarli come tre elementi indipendenti fa sembrare tre lavori quello che ne è uno.
    ///
    /// Il gruppo è cartella *e* nome. La sola cartella non basta: nella libreria storica ce ne sono
    /// che contengono decine di immagini diverse, e raggruppare per cartella le riduceva tutte a una
    /// riga sola -- ventitré immagini sparite dalla vista, e cancellate insieme alla prima se si
    /// fosse premuto Elimina.
    /// </summary>
    public List<List<SharePointItem>> Raggruppa(string library, IReadOnlyList<SharePointItem> items) =>
        items
            // Stessa cartella e stesso nome, estensione a parte: e' un gruppo di consegna, e vale
            // ovunque i file si trovino.
            //
            // Prima il raggruppamento valeva solo dentro una sottocartella, e nella radice della
            // libreria ogni file faceva gruppo per conto suo. In ImagesSent e' esattamente cosi':
            // move-sent-files sposta nella radice e appiattisce il gruppo, quindi la galleria dei
            // Pubblicati mostrava una scheda per file -- e di un EPS o di un SVG l'anteprima non
            // esiste, da cui due terzi di riquadri "anteprima non disponibile". Il gruppo non e'
            // fatto dalla cartella: la cartella era solo il modo in cui lo si riconosceva.
            .GroupBy(i => $"{CartellaDi(i.ServerRelativeUrl)}|{Path.GetFileNameWithoutExtension(i.FileName)}",
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .ToList();

    /// <summary>
    /// Il portatore del gruppo: il raster, perché è l'unico che si possa vedere in anteprima e
    /// l'unico che un modello sappia descrivere. È su di lui che agiscono i pulsanti, ed è la sua
    /// riga a portare il punteggio.
    /// </summary>
    public static SharePointItem PortatoreDi(IReadOnlyList<SharePointItem> gruppo) =>
        gruppo.FirstOrDefault(x => EImmagineRaster(x.FileName)) ?? gruppo[0];

    public static List<string> KeywordDi(string? tags) =>
        (tags ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>Il giudizio completo su un gruppo di consegna.</summary>
    public ValidationResult Valuta(IReadOnlyList<SharePointItem> gruppo)
    {
        var portatore = PortatoreDi(gruppo);
        return _validator.Validate(portatore.Title, portatore.Description,
                                   KeywordDi(portatore.Tags), ModoDi(gruppo));
    }
}

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
    private readonly SharePointStore _sp;
    private readonly ILogger<Punteggiatore> _log;

    public Punteggiatore(StockValidator validator, IOptions<PipelineSettings> s, SharePointStore sp,
                         ILogger<Punteggiatore> log)
    {
        _validator = validator;
        _sp = sp;
        _log = log;
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
            .GroupBy(i => Chiave(i), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .ToList();

    /// <summary>Cartella e nome senza estensione: i file che la condividono sono un'immagine sola.</summary>
    private static string Chiave(SharePointItem i) =>
        $"{CartellaDi(i.ServerRelativeUrl)}|{Path.GetFileNameWithoutExtension(i.FileName)}";

    /// <summary>I file che compongono una consegna completa del percorso durevole.</summary>
    private static readonly string[] Consegne = { ".jpg", ".svg", ".eps" };

    /// <summary>
    /// I gruppi della pagina, completati con i file della stessa immagine che stanno altrove.
    ///
    /// ## Il difetto che risolve
    /// <see cref="Raggruppa"/> lavora sulla pagina, e una pagina sono ventiquattro file in ordine di
    /// identificativo. In ImagesSent l'identificativo è l'ordine di **arrivo**: move-sent-files
    /// sposta ogni file quando parte, e se Freepik ne rifiuta uno per timeout quello parte giorni
    /// dopo, al secondo invio. Allora SVG da una parte, JPG ed EPS dall'altra: due pagine, due
    /// schede, e quella con l'anteprima senza SVG. Misurato su AB34: SVG partito il 27/09, JPG ed
    /// EPS l'11/10, scheda «senza SVG» in cima ai Pubblicati con l'SVG regolarmente consegnato.
    ///
    /// ## Come
    /// Per ogni gruppo si chiedono, per percorso esatto, le consegne che mancano: stessa cartella,
    /// stesso nome, le altre estensioni (<see cref="SharePointStore.ItemsByPaths"/>: niente
    /// elenco, quindi niente soglia dei cinquemila elementi). Poi <see cref="Completa"/> le
    /// aggiunge e decide dove mostrare il gruppo.
    ///
    /// La prima versione cercava per nome in tutta la libreria, e su ImagesSent SharePoint la
    /// rifiutava sempre per la soglia: il completamento non è mai partito, e Volpe2 restava divisa
    /// fra la prima e la terza pagina. Vedi ItemsByPaths.
    ///
    /// Se la ricerca non riesce si torna ai gruppi della sola pagina: è quel che si vedeva prima,
    /// e una galleria con una scheda incompleta è meglio di una galleria che non si apre.
    ///
    /// Lo usano tutti i posti che mostrano schede o depositano il punteggio, perché il punteggio
    /// dipende dal gruppo -- vettoriale o raster -- e due definizioni darebbero due numeri. Restano
    /// su <see cref="Raggruppa"/> le scansioni statistiche dell'intera libreria (Insights, vendite):
    /// lì ogni pezzo viene comunque visitato, e una ricerca in più per pagina moltiplicherebbe le
    /// chiamate a SharePoint senza mostrare niente a nessuno.
    /// </summary>
    public List<List<SharePointItem>> RaggruppaCompleti(string library, IReadOnlyList<SharePointItem> items)
    {
        var gruppi = Raggruppa(library, items);
        var percorsi = PercorsiMancanti(gruppi);
        if (percorsi.Count == 0) return gruppi;
        try
        {
            return Completa(gruppi, _sp.ItemsByPaths(percorsi));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Completamento dei gruppi non riuscito su {Library}: restano quelli della pagina", library);
            return gruppi;
        }
    }

    /// <summary>
    /// Per ogni gruppo, dove dovrebbero stare le consegne che nella pagina non ci sono: la sua
    /// cartella, il suo nome, le estensioni che mancano.
    ///
    /// Un elemento senza percorso server-relative (quelli che arrivano dall'indice di ricerca
    /// possono non averlo) non si completa: non si saprebbe dove guardare.
    /// </summary>
    public static HashSet<string> PercorsiMancanti(IEnumerable<List<SharePointItem>> gruppi)
    {
        var percorsi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in gruppi)
        {
            var cartella = CartellaDi(g[0].ServerRelativeUrl);
            var radice = Path.GetFileNameWithoutExtension(g[0].FileName);
            if (!cartella.StartsWith('/') || string.IsNullOrWhiteSpace(radice)) continue;
            foreach (var ext in Consegne)
                if (!g.Any(x => string.Equals(Path.GetExtension(x.FileName), ext, StringComparison.OrdinalIgnoreCase)))
                    percorsi.Add($"{cartella}/{radice}{ext}");
        }
        return percorsi;
    }

    /// <summary>
    /// Aggiunge a ogni gruppo i file trovati altrove con la sua stessa chiave, e toglie dalla
    /// pagina i pezzi che vanno mostrati in un'altra.
    ///
    /// Un gruppo si mostra **dove sta il suo raster**: è il portatore, quello con l'anteprima e su
    /// cui agiscono i pulsanti (<see cref="PortatoreDi"/>). Un pezzo senza raster, il cui raster
    /// sta in un'altra pagina, qui non compare: là comparirà intero. Senza questa regola
    /// l'immagine comparirebbe due volte, completa in entrambe le pagine, e il punteggio
    /// verrebbe scritto su due portatori diversi.
    ///
    /// Un gruppo che un raster non ce l'ha da nessuna parte resta dov'è, come prima.
    /// </summary>
    public static List<List<SharePointItem>> Completa(List<List<SharePointItem>> gruppi,
                                                     IEnumerable<SharePointItem> trovati)
    {
        var altrove = trovati.GroupBy(Chiave, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var risultato = new List<List<SharePointItem>>(gruppi.Count);
        foreach (var g in gruppi)
        {
            if (!altrove.TryGetValue(Chiave(g[0]), out var candidati))
            {
                risultato.Add(g);
                continue;
            }

            var presenti = new HashSet<int>(g.Select(x => x.Id));
            var aggiunti = candidati.Where(x => presenti.Add(x.Id)).ToList();
            if (aggiunti.Count == 0)
            {
                risultato.Add(g);
                continue;
            }

            if (!g.Any(x => EImmagineRaster(x.FileName)) && aggiunti.Any(x => EImmagineRaster(x.FileName)))
                continue;

            risultato.Add(g.Concat(aggiunti).ToList());
        }
        return risultato;
    }

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

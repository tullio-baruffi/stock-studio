using Microsoft.Extensions.Options;
using StockStudio.Shared.Pipeline;

namespace StockStudio.Api.Services.Integration;

/// <summary>
/// Se un'immagine che sta per entrare in pipeline ci è già passata.
///
/// ## Perché
/// La pipeline tratta ogni caricamento come un'immagine nuova: la traccia, la descrive e la manda
/// ai marketplace, anche se la stessa immagine era già stata pubblicata. Misurato l'11/10/2026:
/// BBoxer, Boxer e i due Border collie caricati due volte a pochi giorni di distanza, e saliti
/// due volte su Adobe Stock e su Freepik.
///
/// ## Come
/// Per nome, come lavora la pipeline: il file caricato diventa «nome.jpg» dentro la cartella
/// «nome» delle librerie di lavoro, e finisce nella radice dei pubblicati con lo stesso nome. Si
/// chiede quindi se esistono quei percorsi (<see cref="SharePointStore.ItemsByPaths"/>: niente
/// elenchi, quindi niente soglia dei cinquemila), per il nome com'è e per il nome senza i segni di
/// copia (<see cref="NomeDiCopia"/>).
///
/// Il contenuto non si confronta: un JPG pubblicato è già stato ricompresso e porta i metadati
/// scritti dalla pipeline, quindi non è più uguale byte per byte all'originale. Una copia
/// rinominata a mano -- BBoxer e Boxer -- resta fuori dal controllo.
/// </summary>
public class ControlloDoppioni
{
    public const string Pubblicata = "pubblicata";
    public const string DaInviare = "da-inviare";
    public const string InLavorazione = "in-lavorazione";

    private readonly SharePointStore _sp;
    private readonly string _siteRoot;

    public ControlloDoppioni(SharePointStore sp, IOptions<PipelineSettings> s)
    {
        _sp = sp;
        _siteRoot = string.IsNullOrWhiteSpace(s.Value.SiteUrl)
            ? ""
            : new Uri(s.Value.SiteUrl!).AbsolutePath.TrimEnd('/');
    }

    /// <summary>
    /// Dove starebbe un file caricato con questo nome, nell'ordine in cui conta: prima fra i
    /// pubblicati, poi fra quelli pronti per l'invio, poi fra quelli ancora in lavorazione.
    ///
    /// Fra i pubblicati si chiede anche l'SVG: di un vettoriale il JPG può non essere partito
    /// (AB34 ha avuto l'SVG pubblicato due settimane prima del JPG). Nelle librerie di lavoro si
    /// guarda sia la cartella col nome dell'immagine, dove deposita la pipeline durevole, sia la
    /// radice, dove arrivavano le immagini del percorso precedente.
    /// </summary>
    public static List<(string Url, string Dove)> PercorsiDa(string siteRoot, string nomeFile)
    {
        var percorsi = new List<(string Url, string Dove)>();
        // Il nome com'è, spazi compresi: «Hype2art_B_26_00601 .jpg» esiste davvero, e ripulito
        // non lo si troverebbe. Ripulita è la seconda forma, quella senza segni di copia.
        var radice = Path.GetFileNameWithoutExtension(nomeFile ?? string.Empty);
        if (string.IsNullOrWhiteSpace(radice)) return percorsi;

        var radici = new[] { radice, NomeDiCopia.Originale(radice) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var r in radici)
        {
            percorsi.Add(($"{siteRoot}/ImagesSent/{r}.jpg", Pubblicata));
            percorsi.Add(($"{siteRoot}/ImagesSent/{r}.svg", Pubblicata));
        }
        foreach (var r in radici)
        {
            // Una cartella non può finire con uno spazio: per quei nomi c'è solo la radice.
            if (r == r.Trim()) percorsi.Add(($"{siteRoot}/ImagesToSend/{r}/{r}.jpg", DaInviare));
            percorsi.Add(($"{siteRoot}/ImagesToSend/{r}.jpg", DaInviare));
        }
        foreach (var r in radici)
        {
            if (r == r.Trim()) percorsi.Add(($"{siteRoot}/ImagesToClassify/{r}/{r}.jpg", InLavorazione));
            percorsi.Add(($"{siteRoot}/ImagesToClassify/{r}.jpg", InLavorazione));
        }
        return percorsi;
    }

    /// <summary>
    /// Per ogni nome, il primo posto in cui la pipeline l'ha già: uno solo, il più avanzato.
    /// Riporta la posizione nell'elenco chiesto, perché due file con lo stesso nome sono due
    /// consegne distinte e il nome non basterebbe a ritrovarle.
    /// </summary>
    public List<GiaPresente> Cerca(IReadOnlyList<string> nomi)
    {
        var richieste = nomi.Select((n, i) => (Posizione: i, Nome: n ?? string.Empty, Percorsi: PercorsiDa(_siteRoot, n ?? string.Empty)))
                            .ToList();
        var tutti = richieste.SelectMany(r => r.Percorsi.Select(p => p.Url))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToList();
        var risultato = new List<GiaPresente>();
        if (tutti.Count == 0) return risultato;

        var trovati = _sp.ItemsByPaths(tutti)
            .GroupBy(t => t.ServerRelativeUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (posizione, nome, percorsi) in richieste)
        {
            foreach (var (url, dove) in percorsi)
            {
                if (!trovati.TryGetValue(url, out var item)) continue;
                risultato.Add(new GiaPresente(posizione, nome, dove, item.FileName, Quando(item.Modified)));
                break;
            }
        }
        return risultato;
    }

    private static string? Quando(string modified) =>
        DateTimeOffset.TryParse(modified, out var q) ? q.ToUniversalTime().ToString("o") : null;
}

/// <summary>Un'immagine dell'elenco che la pipeline ha già, e dove.</summary>
public record GiaPresente(int Posizione, string Nome, string Dove, string File, string? Quando);

public record GiaPresentiRichiesta(List<string>? Nomi);

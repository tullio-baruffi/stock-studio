using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services.Feedback;
using StockStudio.Api.Services.Integration;
using StockStudio.Api.Services.Sales;

namespace StockStudio.Api.Controllers;

/// <summary>
/// Gli interventi che si possono ancora fare su file già pubblicati.
///
/// Il magazzino è di diecimila pezzi e rifarli non è un'opzione. Ma due cose si possono cambiare
/// dall'esterno senza ricaricare niente: l'ordine delle keyword, che Adobe pesa in modo diseguale,
/// e la sovrapposizione fra file della stessa serie, che li fa competere fra loro nella stessa
/// ricerca. Sono le uniche due leve rimaste su ciò che è già lì.
/// </summary>
[ApiController]
[Route("api/tune")]
public class TuneController : ControllerBase
{
    private readonly SharePointStore _sp;
    private readonly SalesStore _sales;
    private readonly KeywordSaturation _saturation;
    private readonly PipelineSettings _s;
    private readonly ILogger<TuneController> _log;

    public TuneController(SharePointStore sp, SalesStore sales, KeywordSaturation saturation,
                          IOptions<PipelineSettings> s, ILogger<TuneController> log)
    {
        _sp = sp;
        _sales = sales;
        _saturation = saturation;
        _s = s.Value;
        _log = log;
    }

    /// <summary>
    /// Quante keyword contano davvero.
    ///
    /// Erano sette, prese da una dichiarazione di un rappresentante Adobe. La documentazione
    /// ufficiale però dice **dieci**, in due punti distinti: "The first 10 keywords carry the most
    /// weight in search results" e "the first 10 keywords listed are prioritized in search
    /// results". Fra una dichiarazione riferita e la documentazione, vale la documentazione.
    /// </summary>
    private const int Testa = 10;

    /// <summary>
    /// Le parole che nessuna lingua salva: valgono come rumore ovunque compaiano.
    ///
    /// È un elenco scritto a mano, e come tale è un giudizio, non una misura. Serve come rete di
    /// sicurezza per il caso in cui il campione esaminato sia troppo piccolo perché la frequenza
    /// dica qualcosa di affidabile. Il criterio vero è quello calcolato, non questo.
    /// </summary>
    private static readonly HashSet<string> Deboli = new(StringComparer.OrdinalIgnoreCase)
    {
        "background", "backdrop", "image", "photo", "picture", "photography", "illustration",
        "graphic", "design", "style", "concept", "view", "scene", "shot", "art", "artwork",
        "digital", "modern", "abstract", "template", "element", "decoration", "decorative",
        "copy space", "no people", "nobody", "isolated", "white", "black", "color", "colour",
        "vector", "icon", "symbol", "sign", "banner", "poster", "card", "print", "wallpaper",
    };

    /// <summary>
    /// Oltre questa quota di file in cui compare, una keyword non distingue più niente.
    ///
    /// Il ragionamento è quello che sta sotto qualsiasi motore di ricerca: un termine presente
    /// quasi ovunque non aiuta a scegliere. Se metà della libreria è marcata "silhouette", chi
    /// cerca "silhouette" trova tutto il magazzino indifferenziato, e quella parola in prima
    /// posizione non sta comprando nessuna visibilità.
    /// </summary>
    private const double SogliaFrequenza = 0.45;

    /// <summary>
    /// Quante volte ciascuna keyword compare, contata per file e non per occorrenza.
    ///
    /// È questo il pezzo che rende lo strumento indipendente dalla lingua e dal soggetto: non c'è
    /// bisogno di sapere cosa significhi una parola per accorgersi che, comparendo sul novanta per
    /// cento dei file, non ne distingue nessuno.
    /// </summary>
    public static Dictionary<string, int> Frequenze(IEnumerable<IReadOnlyList<string>> perFile)
    {
        var conta = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kws in perFile)
            foreach (var k in kws.Select(x => x.Trim()).Where(x => x.Length > 0)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                conta[k] = conta.GetValueOrDefault(k) + 1;
        return conta;
    }

    /// <summary>
    /// Sposta in fondo le keyword che non distinguono, lasciando intatto il resto dell'ordine.
    ///
    /// La prima versione di questo metodo era più ambiziosa: promuoveva le parole che comparivano
    /// nel titolo, sul presupposto che il titolo contenga il soggetto. Provata sui file veri ha
    /// peggiorato le cose. Su «Three polo players gallop across a grass field beside a club
    /// pavilion» faceva salire "grass", "field" e "pavilion" — che nel titolo ci sono davvero, ma
    /// come sfondo — scavalcando "horse", che nel titolo non compare ed è il secondo termine per
    /// cui quell'immagine viene cercata. Il titolo dice di cosa parla la frase, non quale parola
    /// vale di più.
    ///
    /// Resta quindi una sola regola, applicata con due criteri che si sommano: una keyword scende
    /// se compare in troppi file del campione (misurato) oppure se è una di quelle parole che non
    /// distinguono in nessuna libreria (elencato). L'ordine relativo di tutte le altre viene
    /// conservato, perché è già quello proposto per rilevanza e non c'è motivo di credere di
    /// saperne di più.
    /// </summary>
    /// <param name="frequenze">Presenze per keyword nel campione. Vuoto: vale solo l'elenco fisso.</param>
    /// <param name="fileEsaminati">Dimensione del campione, per trasformare le presenze in quota.</param>
    public static List<string> Riordina(string titolo, IReadOnlyList<string> keywords,
                                        IReadOnlyDictionary<string, int>? frequenze = null,
                                        int fileEsaminati = 0)
    {
        bool NonDistingue(string k)
        {
            if (Deboli.Contains(k)) return true;
            // Sotto una ventina di file la frequenza è aneddoto, non statistica: meglio tacere.
            if (frequenze is null || fileEsaminati < 20) return false;
            return frequenze.TryGetValue(k, out var n) && (double)n / fileEsaminati > SogliaFrequenza;
        }

        // OrderBy è stabile: le keyword che distinguono mantengono esattamente l'ordine d'origine,
        // e fra loro non cambia niente. L'unico movimento è quello delle altre verso il fondo.
        return keywords.Where(k => !string.IsNullOrWhiteSpace(k))
                       .Select(k => k.Trim())
                       .OrderBy(k => NonDistingue(k) ? 1 : 0)
                       .ToList();
    }

    /// <summary>Mostra come verrebbero riordinate, senza scrivere niente.</summary>
    [HttpGet("keywords/preview")]
    public IActionResult Anteprima([FromQuery] string library = "ImagesToSend", [FromQuery] int take = 50)
    {
        if (!_s.Enabled) return Ok(new { ok = false, error = "Pipeline disabilitata." });

        try
        {
            var page = _sp.ListItems(library, Math.Clamp(take, 1, 200), null, null, null);

            // Le frequenze si misurano sul campione appena letto: nessuna lettura in più, e il
            // criterio resta ancorato a questa libreria invece che a un'idea generale di cosa sia
            // una parola generica.
            var perFile = page.Items.Select(i => (IReadOnlyList<string>)Tags(i.Tags)).ToList();
            var freq = Frequenze(perFile);
            int n = perFile.Count;

            var righe = page.Items.Select(i =>
            {
                var kw = Tags(i.Tags);
                var nuovo = Riordina(i.Title, kw, freq, n);
                return new
                {
                    id = i.Id,
                    file = i.FileName,
                    titolo = i.Title,
                    prima = kw.Take(Testa).ToList(),
                    dopo = nuovo.Take(Testa).ToList(),
                    cambia = !kw.Take(Testa).SequenceEqual(nuovo.Take(Testa), StringComparer.OrdinalIgnoreCase),
                };
            }).ToList();

            // Cosa il campione ha giudicato non distintivo, e con quale presenza: senza questo lo
            // strumento chiederebbe fiducia su un criterio invisibile.
            var diffuse = freq.Where(p => n >= 20 && (double)p.Value / n > SogliaFrequenza)
                              .OrderByDescending(p => p.Value)
                              .Take(25)
                              .Select(p => new { parola = p.Key, file = p.Value, quota = Math.Round(100.0 * p.Value / n) })
                              .ToList();

            // La misura non serve solo a riordinare quello che c'è: passata al generatore, impedisce
            // che le prossime immagini nascano con lo stesso difetto. Senza questo passaggio lo
            // strumento correggerebbe per sempre un errore che la pipeline continua a produrre.
            if (diffuse.Count > 0)
                _saturation.Aggiorna(library, n, diffuse.Select(d => d.parola));

            return Ok(new
            {
                ok = true, library,
                esaminati = righe.Count,
                daCambiare = righe.Count(r => r.cambia),
                sogliaPercento = (int)(SogliaFrequenza * 100),
                diffuse,
                righe,
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Applica il riordino ai file indicati. Scrive solo dove l'ordine cambia davvero.</summary>
    [HttpPost("keywords/apply")]
    public IActionResult Applica([FromQuery] string library, [FromBody] int[] ids)
    {
        if (!_s.Enabled) return Ok(new { ok = false, error = "Pipeline disabilitata." });
        if (ids is null || ids.Length == 0) return Ok(new { ok = false, error = "Nessun file indicato." });

        int scritti = 0, invariati = 0;
        var falliti = new List<object>();

        // Le frequenze vanno misurate sulla libreria, non sui soli file da riscrivere: un campione
        // fatto di venti file selezionati direbbe che tutto è diffuso.
        IReadOnlyDictionary<string, int> freq = new Dictionary<string, int>();
        int campione = 0;
        try
        {
            var page = _sp.ListItems(library, 200, null, null, null);
            freq = Frequenze(page.Items.Select(i => (IReadOnlyList<string>)Tags(i.Tags)));
            campione = page.Items.Count;
        }
        catch (Exception ex)
        {
            // Senza frequenze resta l'elenco fisso: meno preciso, ma non pericoloso.
            _log.LogWarning(ex, "Frequenze non calcolabili su {Library}: uso il solo elenco fisso", library);
        }

        foreach (var id in ids.Take(300))
        {
            try
            {
                var it = _sp.GetItem(library, id);
                var kw = Tags(it.Tags);
                var nuovo = Riordina(it.Title, kw, freq, campione);

                if (kw.SequenceEqual(nuovo, StringComparer.Ordinal)) { invariati++; continue; }

                _sp.UpdateItem(library, id, it.Title, it.Description, string.Join(", ", nuovo));
                scritti++;
            }
            catch (Exception ex)
            {
                falliti.Add(new { id, errore = ex.Message });
            }
        }

        _log.LogInformation("Riordino keyword: {Scritti} riscritti, {Invariati} già in ordine", scritti, invariati);
        return Ok(new { ok = true, scritti, invariati, falliti });
    }

    /// <summary>
    /// Trova le serie i cui file si somigliano troppo nelle keyword.
    ///
    /// Le immagini nascono a gruppi, e il generatore descrive ogni variante quasi allo stesso modo.
    /// Il risultato è che i file di una serie finiscono nelle stesse ricerche e si tolgono il posto
    /// a vicenda: la pratica raccomandata è che una serie condivida al massimo circa metà delle
    /// keyword, differenziando il resto. Qui si misura quanto ciascuna serie se ne discosta.
    /// </summary>
    [HttpGet("overlap")]
    public IActionResult Sovrapposizione([FromQuery] string library = "ImagesToSend",
                                         [FromQuery] int take = 150,
                                         [FromQuery] int sogliaPercento = 50)
    {
        if (!_s.Enabled) return Ok(new { ok = false, error = "Pipeline disabilitata." });

        try
        {
            var page = _sp.ListItems(library, Math.Clamp(take, 1, 400), null, null, null);

            var serie = page.Items
                .Select(i => new { i.Id, i.FileName, i.Title, Kw = Tags(i.Tags) })
                .GroupBy(x => Serie(x.FileName), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var membri = g.ToList();
                    // Quante keyword compaiono in ogni singolo file della serie: è la parte che
                    // rende i file indistinguibili agli occhi del motore di ricerca.
                    var comuni = membri.Skip(1)
                        .Aggregate(new HashSet<string>(membri[0].Kw, StringComparer.OrdinalIgnoreCase),
                                   (acc, m) => { acc.IntersectWith(m.Kw); return acc; });
                    double media = membri.Average(m => m.Kw.Count);
                    int perc = media > 0 ? (int)Math.Round(comuni.Count / media * 100) : 0;

                    return new
                    {
                        serie = g.Key,
                        file = membri.Count,
                        keywordMedie = Math.Round(media, 1),
                        condivise = comuni.Count,
                        percentuale = perc,
                        oltreSoglia = perc > sogliaPercento,
                        esempi = comuni.Take(12).ToList(),
                    };
                })
                .OrderByDescending(x => x.percentuale)
                .ToList();

            return Ok(new
            {
                ok = true, library, sogliaPercento,
                serieTrovate = serie.Count,
                serieOltreSoglia = serie.Count(x => x.oltreSoglia),
                serie,
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Quali file conviene NON consegnare.
    ///
    /// La classifica dei venditori di punta ordina per rapporto fra caricamenti e vendite: ogni
    /// file consegnato che poi non vende peggiora la posizione. Quindi prima di spedire un lotto
    /// vale la pena sapere quali soggetti, in passato, non hanno mai venduto nulla — e tenerli
    /// indietro invece di allungare il denominatore.
    /// </summary>
    [HttpGet("mute")]
    public IActionResult Muti([FromQuery] string library = "ImagesToSend", [FromQuery] int take = 150)
    {
        if (!_s.Enabled) return Ok(new { ok = false, error = "Pipeline disabilitata." });

        var vendite = _sales.Range(null, null);
        if (vendite.Count == 0)
            return Ok(new { ok = false, error = "Nessuna vendita in archivio: importa prima l'esportazione." });

        // Le serie che hanno venduto almeno una volta, per nome-serie e non per file: il singolo
        // file nuovo non ha storia, la sua famiglia sì.
        var vendutoPerSerie = vendite
            .GroupBy(s => Serie(s.FileName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new { vendite = g.Count(), ricavi = Math.Round(g.Sum(x => x.Royalty), 2) },
                          StringComparer.OrdinalIgnoreCase);

        try
        {
            var page = _sp.ListItems(library, Math.Clamp(take, 1, 400), null, null, null);
            var righe = page.Items.Select(i =>
            {
                var s = Serie(i.FileName);
                vendutoPerSerie.TryGetValue(s, out var v);
                return new
                {
                    id = i.Id, file = i.FileName, titolo = i.Title, serie = s,
                    venditeSerie = v?.vendite ?? 0,
                    ricaviSerie = v?.ricavi ?? 0m,
                };
            }).ToList();

            return Ok(new
            {
                ok = true, library,
                esaminati = righe.Count,
                conStorico = righe.Count(r => r.venditeSerie > 0),
                senzaStorico = righe.Count(r => r.venditeSerie == 0),
                righe = righe.OrderBy(r => r.venditeSerie).ToList(),
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Cosa il prompt di generazione sa oggi delle keyword sature.</summary>
    [HttpGet("saturation")]
    public IActionResult Saturazione()
    {
        var s = _saturation.Stato;
        return Ok(new
        {
            ok = true,
            attiva = s.Sature.Count > 0,
            parole = s.Sature,
            libreria = s.Libreria,
            campione = s.Campione,
            quando = s.Quando?.ToString("o"),
        });
    }

    private static List<string> Tags(string? tags) =>
        (tags ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    /// <summary>La famiglia del file: nome senza estensione e senza la numerazione finale.</summary>
    private static string Serie(string fileName)
    {
        var s = Path.GetFileNameWithoutExtension(fileName).Trim();
        s = Regex.Replace(s, @"\s*\(\d+\)\s*$", "");
        s = Regex.Replace(s, @"[\s_-]*\d+\s*$", "");
        return s.Length > 0 ? s : Path.GetFileNameWithoutExtension(fileName);
    }
}

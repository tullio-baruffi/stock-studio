using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services.Integration;
using StockStudio.Api.Services.Sales;

namespace StockStudio.Api.Controllers;

/// <summary>
/// Le vendite: l'unica misura che dica se tutto il resto è servito a qualcosa.
///
/// Adobe non offre nessuna interfaccia programmabile agli autori -- lo dichiara nella propria
/// documentazione per sviluppatori -- quindi i dati entrano dall'unica porta che esiste: il file
/// che il portale lascia scaricare da Dettagli, vista "Attività". Che sia un'importazione a mano
/// non è una scelta di comodo: è l'unica strada disponibile.
/// </summary>
[ApiController]
[Route("api/sales")]
public class SalesController : ControllerBase
{
    private readonly SalesStore _store;
    private readonly SharePointStore _sp;
    private readonly PipelineSettings _pipeline;
    private readonly ILogger<SalesController> _log;

    public SalesController(SalesStore store, SharePointStore sp,
                           IOptions<PipelineSettings> pipeline, ILogger<SalesController> log)
    {
        _store = store;
        _sp = sp;
        _pipeline = pipeline.Value;
        _log = log;
    }

    /// <summary>
    /// Riceve l'esportazione del portale. Il corpo è il CSV così com'è, senza rimaneggiarlo.
    ///
    /// Reimportare lo stesso intervallo è previsto e innocuo: le vendite già viste vengono contate
    /// come tali e non sommate di nuovo. È la condizione per poter scaricare "l'ultimo mese" ogni
    /// tanto senza tenere il conto di dove si era arrivati.
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import()
    {
        using var reader = new StreamReader(Request.Body);
        var csv = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(csv))
            return Ok(new { ok = false, error = "Il file è vuoto." });

        var (sales, read, skipped) = AdobeSalesCsv.Parse(csv);

        if (sales.Count == 0)
            return Ok(new
            {
                ok = false,
                error = $"Nessuna vendita riconosciuta su {read} righe lette. " +
                        "Attese le colonne dell'esportazione Adobe: data, ID, titolo, licenza, royalty, " +
                        "tipo, nome file, autore, taglia.",
            });

        var esito = _store.Save(sales, read, skipped);

        return Ok(new
        {
            ok = true,
            lette = esito.Read,
            importate = esito.Imported,
            giaPresenti = esito.Duplicates,
            scartate = esito.Skipped,
            dal = esito.From?.ToString("o"),
            al = esito.To?.ToString("o"),
            totaleFile = esito.Total,
            inArchivio = _store.Count(),
        });
    }

    /// <summary>Il quadro d'insieme: quanto, di che tipo, con quale licenza, e cosa vende davvero.</summary>
    [HttpGet("summary")]
    public IActionResult Summary([FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        var (a, b) = Window(from, to);
        var sales = _store.Range(a, b);

        if (sales.Count == 0)
            return Ok(new { ok = true, vuoto = true, inArchivio = _store.Count() });

        // Il rendimento per download è il numero che distingue un portfolio che vende bene da uno
        // che vende molto e male: due file possono avere gli stessi download e la metà dei ricavi.
        decimal totale = sales.Sum(s => s.Royalty);

        // Quanti file diversi hanno venduto, e quanto è concentrato il guadagno.
        //
        // È la domanda che un portfolio di diecimila pezzi rende urgente: se a vendere sono sempre
        // gli stessi venti, produrre il decimillesimo file non serve a niente e conviene invece
        // capire cosa hanno di diverso quei venti. Il conto dei file che fanno metà dei ricavi lo
        // dice in un numero solo.
        var perFile = sales
            .GroupBy(s => s.FileName)
            .Select(g => g.Sum(x => x.Royalty))
            .OrderByDescending(v => v)
            .ToList();

        decimal meta = totale / 2, corsa = 0;
        int fileMeta = 0;
        foreach (var v in perFile)
        {
            corsa += v;
            fileMeta++;
            if (corsa >= meta) break;
        }

        var perMese = sales
            .GroupBy(s => s.SoldAt.ToUniversalTime().ToString("yyyy-MM"))
            .OrderBy(g => g.Key)
            .Select(g => new { mese = g.Key, vendite = g.Count(), ricavi = Round(g.Sum(x => x.Royalty)) })
            .ToList();

        return Ok(new
        {
            ok = true,
            vuoto = false,
            inArchivio = _store.Count(),
            dal = sales.Min(s => s.SoldAt).ToString("o"),
            al = sales.Max(s => s.SoldAt).ToString("o"),
            vendite = sales.Count,
            ricavi = Round(totale),
            perDownload = Round(totale / sales.Count),
            fileDistinti = perFile.Count,
            fileMetaRicavi = fileMeta,
            perTipo = Raggruppa(sales, s => s.AssetType),
            perLicenza = Raggruppa(sales, s => s.License),
            perSerie = Raggruppa(sales, s => Serie(s.FileName), 20),
            perMese,
            migliori = sales
                .GroupBy(s => s.FileName)
                .Select(g => new
                {
                    file = g.Key,
                    titolo = g.First().Title,
                    tipo = g.First().AssetType,
                    vendite = g.Count(),
                    ricavi = Round(g.Sum(x => x.Royalty)),
                })
                .OrderByDescending(x => x.ricavi)
                .Take(25)
                .ToList(),
        });
    }

    /// <summary>
    /// Incrocia le vendite col magazzino: quali file prodotti hanno venduto e quali no.
    ///
    /// È la domanda che ha fatto nascere tutto questo. Il collegamento passa dal nome del file,
    /// che l'esportazione riporta per intero: senza quello servirebbe una tabella di corrispondenza
    /// fra gli identificativi di Adobe e la libreria, e non esisterebbe modo di costruirla.
    /// </summary>
    [HttpGet("warehouse")]
    public IActionResult Warehouse([FromQuery] string library = "ImagesSent", [FromQuery] int take = 200)
    {
        if (!_pipeline.Enabled) return Ok(new { ok = false, error = "Pipeline disabilitata." });

        var sales = _store.Range(null, null);
        if (sales.Count == 0)
            return Ok(new { ok = false, error = "Nessuna vendita in archivio: importa prima l'esportazione." });

        // Il confronto avviene sul nome senza estensione: la stessa immagine è venduta come .ai o
        // .eps mentre in libreria è il .jpg che le fa da portatore.
        var venduto = sales
            .GroupBy(s => Path.GetFileNameWithoutExtension(s.FileName).Trim(),
                     StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                          g => new { vendite = g.Count(), ricavi = Round(g.Sum(x => x.Royalty)) },
                          StringComparer.OrdinalIgnoreCase);

        try
        {
            var page = _sp.ListItems(library, Math.Clamp(take, 1, 500), null, null, null);
            var righe = page.Items.Select(i =>
            {
                var b = Path.GetFileNameWithoutExtension(i.FileName).Trim();
                venduto.TryGetValue(b, out var v);
                return new
                {
                    file = i.FileName,
                    titolo = i.Title,
                    vendite = v?.vendite ?? 0,
                    ricavi = v?.ricavi ?? 0m,
                };
            }).ToList();

            return Ok(new
            {
                ok = true,
                library,
                esaminati = righe.Count,
                conVendite = righe.Count(r => r.vendite > 0),
                senzaVendite = righe.Count(r => r.vendite == 0),
                righe,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Incrocio col magazzino non riuscito su {Library}", library);
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Cosa hanno in comune i file che fanno la maggior parte dei guadagni.
    ///
    /// Con seicento file che vendono e novanta che fanno metà dei ricavi, la domanda utile non è
    /// più "quale file rende" ma "cosa avevano di diverso". Qui il confronto è fra il vocabolario
    /// dei titoli dei pochi che contano e quello di tutti gli altri: le parole che compaiono molto
    /// più spesso di là che di qua sono l'indizio più concreto disponibile su cosa il mercato stia
    /// effettivamente comprando.
    ///
    /// Non è una spiegazione causale -- il titolo non fa vendere da solo -- ma è il solo segnale
    /// che si possa ricavare senza rifare a mano la cronologia di diecimila file.
    /// </summary>
    [HttpGet("dna")]
    public IActionResult Dna([FromQuery] int minimo = 3)
    {
        var sales = _store.Range(null, null);
        if (sales.Count == 0)
            return Ok(new { ok = false, error = "Nessuna vendita in archivio." });

        var perFile = sales
            .GroupBy(s => s.FileName)
            .Select(g => new
            {
                file = g.Key,
                titolo = g.First().Title,
                tipo = g.First().AssetType,
                vendite = g.Count(),
                ricavi = g.Sum(x => x.Royalty),
                custom = g.Count(x => x.License == "custom"),
            })
            .OrderByDescending(x => x.ricavi)
            .ToList();

        decimal totale = perFile.Sum(x => x.ricavi), meta = totale / 2, corsa = 0;
        var vitali = new List<int>();
        for (int i = 0; i < perFile.Count && corsa < meta; i++) { corsa += perFile[i].ricavi; vitali.Add(i); }

        var pochi = perFile.Take(vitali.Count).ToList();
        var molti = perFile.Skip(vitali.Count).ToList();

        var freqPochi = Vocabolario(pochi.Select(x => x.titolo));
        var freqMolti = Vocabolario(molti.Select(x => x.titolo));

        // Il rapporto fra le due frequenze relative. Una parola comune ovunque ha rapporto vicino
        // a uno e non dice niente; quelle molto sopra sono ciò che i best seller hanno in più.
        var parole = freqPochi
            .Where(p => p.Value >= minimo)
            .Select(p =>
            {
                double quotaPochi = (double)p.Value / Math.Max(1, pochi.Count);
                freqMolti.TryGetValue(p.Key, out int altrove);
                double quotaMolti = (double)altrove / Math.Max(1, molti.Count);
                return new
                {
                    parola = p.Key,
                    neiPochi = p.Value,
                    altrove,
                    // Un piccolo addendo evita che una parola mai vista altrove esploda all'infinito.
                    rapporto = Math.Round(quotaPochi / (quotaMolti + 0.001), 2),
                };
            })
            .OrderByDescending(x => x.rapporto)
            .Take(30)
            .ToList();

        return Ok(new
        {
            ok = true,
            totaleFile = perFile.Count,
            vitali = pochi.Count,
            quotaRicaviVitali = Round(pochi.Sum(x => x.ricavi)),
            ricaviTotali = Round(totale),
            venditeMediaVitali = Math.Round(pochi.Average(x => (double)x.vendite), 1),
            venditeMediaResto = molti.Count > 0 ? Math.Round(molti.Average(x => (double)x.vendite), 1) : 0,
            perDownloadVitali = Round(pochi.Sum(x => x.ricavi) / Math.Max(1, pochi.Sum(x => x.vendite))),
            perDownloadResto = molti.Count > 0
                ? Round(molti.Sum(x => x.ricavi) / Math.Max(1, molti.Sum(x => x.vendite))) : 0,
            tipiVitali = pochi.GroupBy(x => x.tipo)
                              .Select(g => new { tipo = g.Key, file = g.Count() })
                              .OrderByDescending(x => x.file).ToList(),
            parole,
            elenco = pochi.Select(x => new
            {
                x.file, x.titolo, x.tipo, x.vendite,
                ricavi = Round(x.ricavi),
                quotaCustom = Math.Round(100.0 * x.custom / Math.Max(1, x.vendite)),
            }).ToList(),
        });
    }

    /// <summary>
    /// Le parole dei titoli, contate una volta per file: un titolo che ripete "dog" tre volte non
    /// vale tre file, e senza questa cautela i titoli lunghi peserebbero più degli altri.
    /// </summary>
    private static Dictionary<string, int> Vocabolario(IEnumerable<string> titoli)
    {
        var conta = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in titoli)
        {
            var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in (t ?? "").Split(new[] { ' ', ',', '.', ';', ':', '(', ')', '-', '"', '\'', '/' },
                                              StringSplitOptions.RemoveEmptyEntries))
            {
                var p = w.Trim().ToLowerInvariant();
                if (p.Length < 3 || Vuote.Contains(p) || !p.All(char.IsLetter)) continue;
                viste.Add(p);
            }
            foreach (var p in viste) conta[p] = conta.GetValueOrDefault(p) + 1;
        }
        return conta;
    }

    /// <summary>Parole che compaiono ovunque e non distinguono nulla.</summary>
    private static readonly HashSet<string> Vuote = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","with","for","from","that","this","are","was","its","their","have","has",
        "into","over","under","out","off","its","his","her","they","them","she","him",
        "-","a","an","of","in","on","at","to","by","as","is","it","or","be","up",
        "background","image","photo","picture","illustration","vector","graphic","style","design",
        "scene","view","shot","concept","creating","showing","featuring","surrounded","while",
    };

    private static object Raggruppa(IReadOnlyList<Sale> sales, Func<Sale, string> chiave, int limite = 0) =>
        sales.GroupBy(chiave)
             .Select(g => new
             {
                 nome = g.Key,
                 vendite = g.Count(),
                 ricavi = Round(g.Sum(x => x.Royalty)),
                 perDownload = Round(g.Sum(x => x.Royalty) / g.Count()),
             })
             .OrderByDescending(x => x.ricavi)
             .Take(limite > 0 ? limite : int.MaxValue)
             .ToList();

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// La serie a cui il file appartiene, ricavata dal nome.
    ///
    /// Le immagini nascono a gruppi -- una tornata di silhouette di montagne russe, una di animali
    /// in scatola -- e il nome lo racconta: "Silhouette_MontagneRusse (24).ai" e
    /// "Silhouette_MontagneRusse (6).ai" sono lo stesso lavoro fatto due volte. Sapere quale serie
    /// rende è una domanda diversa da quale file rende, e molto più utile per decidere cosa
    /// produrre la prossima volta: un singolo file che vende può essere fortuna, una serie no.
    ///
    /// Si toglie l'estensione, poi la numerazione finale in tutte le forme in cui compare qui:
    /// fra parentesi, dopo un trattino basso, dopo un trattino.
    /// </summary>
    private static string Serie(string fileName)
    {
        var s = Path.GetFileNameWithoutExtension(fileName).Trim();

        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(\d+\)\s*$", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s_-]*\d+\s*$", "");

        return s.Length > 0 ? s : Path.GetFileNameWithoutExtension(fileName);
    }

    private static (DateTimeOffset?, DateTimeOffset?) Window(string? from, string? to) =>
        (DateTimeOffset.TryParse(from, out var a) ? a : null,
         DateTimeOffset.TryParse(to, out var b) ? b : null);
}

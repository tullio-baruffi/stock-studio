using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Integration;
using StockStudio.Api.Services.Sales;
using StockStudio.Api.Services.Scoring;

namespace StockStudio.Api.Controllers;

/// <summary>
/// Che fare delle immagini già pubblicate.
///
/// Diecimila file sono online e non si toccano più: la pipeline li ha consegnati e da lì in poi
/// vivono su Adobe. Ma "non si toccano più" era una convinzione, non un fatto. Adobe consente di
/// modificare titolo e keyword di un'immagine già approvata e in vendita, e dichiara che rifinire
/// i metadati migliora la visibilità in ricerca. Quindi l'intervento esiste: manca solo sapere
/// **su quali** conviene farlo, perché rifarli tutti a mano è una vita.
///
/// Qui si incrociano le tre cose che si sanno di ogni immagine -- se ha venduto, quanto valgono i
/// suoi metadati, da quanto è online -- e se ne ricava un consiglio.
///
/// Sulla cancellazione, che è la terza opzione che viene sempre in mente: la ricerca sulla
/// documentazione Adobe non ha trovato **nessuna** prova che eliminare le immagini che non vendono
/// giovi alle altre. Non esiste un "punteggio di qualità del portfolio" documentato, non esiste
/// una penalizzazione dichiarata per chi ha molti file fermi. Un'immagine che non vende non costa
/// niente e potrebbe vendere domani; cancellarla è una perdita certa in cambio di un beneficio
/// mai dimostrato. Per questo qui non viene mai consigliata: viene solo detto quando è l'unica
/// cosa rimasta a cui pensare.
/// </summary>
[ApiController]
[Route("api/bonifica")]
public class BonificaController : ControllerBase
{
    private readonly SharePointStore _sp;
    private readonly SalesStore _sales;
    private readonly StockValidator _validator;
    private readonly Punteggiatore _punteggiatore;
    private readonly PipelineSettings _s;
    private readonly ILogger<BonificaController> _log;

    public BonificaController(SharePointStore sp, SalesStore sales, StockValidator validator,
                              Punteggiatore punteggiatore, IOptions<PipelineSettings> s,
                              ILogger<BonificaController> log)
    {
        _sp = sp;
        _sales = sales;
        _validator = validator;
        _punteggiatore = punteggiatore;
        _s = s.Value;
        _log = log;
    }

    /// <summary>
    /// Soglia oltre la quale i metadati non sono più il problema.
    ///
    /// Sotto, c'è margine documentato: rifinire i metadati migliora la visibilità. Sopra, se
    /// l'immagine non vende lo stesso, la causa è altrove -- il soggetto, il mercato, la
    /// concorrenza -- e riscrivere le keyword è tempo speso a lucidare la cosa giusta nel posto
    /// sbagliato.
    /// </summary>
    private const int MetadatiDeboli = 80;

    /// <summary>
    /// Quanto tempo serve prima di poter dire che un'immagine "non vende".
    ///
    /// Meno di così non è un giudizio, è impazienza: un file appena pubblicato non ha ancora
    /// attraversato una stagione, e su Adobe la stagionalità conta. Il valore è una scelta, non
    /// una regola di Adobe -- che sulla durata non dice nulla.
    /// </summary>
    private static readonly TimeSpan TempoPerGiudicare = TimeSpan.FromDays(180);

    public enum Consiglio { Aspetta, LasciaStare, RilavoraMetadati, SoggettoDebole }

    /// <summary>
    /// Il quadro d'insieme, costruito senza scorrere diecimila file.
    ///
    /// Le vendite le ho tutte in archivio, quindi "quanti hanno venduto" si conta di qui senza
    /// chiedere niente a SharePoint. Il totale della libreria è un numero che SharePoint tiene da
    /// sé. La distribuzione dei punteggi arriva da poche query mirate, ognuna su una fascia
    /// abbastanza stretta da restare sotto la soglia dei cinquemila.
    /// </summary>
    [HttpGet("quadro")]
    public IActionResult Quadro([FromQuery] string library = "ImagesSent")
    {
        if (!_s.Enabled) return Ok(new { ok = false, error = "Pipeline disabilitata." });

        try
        {
            var vendite = _sales.Range(null, null);
            if (vendite.Count == 0)
                return Ok(new { ok = false, error = "Nessuna vendita in archivio: importa prima l'esportazione da Adobe." });

            var perFile = vendite
                .GroupBy(v => Path.GetFileNameWithoutExtension(v.FileName).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => new { n = g.Count(), ricavi = g.Sum(x => x.Royalty) },
                              StringComparer.OrdinalIgnoreCase);

            var totale = _sp.ItemCount(library);
            var cheVendono = perFile.Count;
            var ricaviTotali = perFile.Values.Sum(x => x.ricavi);

            // La distribuzione dei punteggi, per sapere quanto lavoro ci sarebbe. Ogni fascia è
            // una query a sé perché una sola larga supererebbe la soglia.
            var fasce = new List<object>();
            foreach (var (etichetta, min, max) in new (string, int?, int?)[]
                     { ("sotto 60", null, 59), ("60-69", 60, 69), ("70-74", 70, 74),
                       ("75-79", 75, 79), ("80-84", 80, 84), ("85-89", 85, 89), ("90 e oltre", 90, null) })
            {
                try
                {
                    var n = _sp.ContaPerPunteggio(library, min, max);
                    fasce.Add(new { fascia = etichetta, quanti = n, deboli = (max ?? 100) < MetadatiDeboli });
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Conteggio fascia {F} non riuscito", etichetta);
                    fasce.Add(new { fascia = etichetta, quanti = -1, deboli = (max ?? 100) < MetadatiDeboli });
                }
            }

            var conMetadatiDeboli = fasce
                .Select(f => (dynamic)f)
                .Where(f => f.deboli == true && f.quanti > 0)
                .Sum(f => (int)f.quanti);

            return Ok(new
            {
                ok = true,
                library,
                totale,
                cheVendono,
                cheNonVendono = Math.Max(0, totale - cheVendono),
                percentualeCheVende = totale > 0 ? Math.Round(cheVendono * 100.0 / totale, 1) : 0,
                ricaviTotali = Math.Round(ricaviTotali, 2),
                ricavoMedioPerFileCheVende = cheVendono > 0 ? Math.Round(ricaviTotali / cheVendono, 2) : 0,
                conMetadatiDeboli,
                sogliaMetadatiDeboli = MetadatiDeboli,
                fasce,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Quadro di bonifica non riuscito su {Library}", library);
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// La coda di lavoro: le immagini su cui conviene intervenire, con il perché.
    ///
    /// Si parte dalle più deboli -- il filtro per punteggio lo fa fare a SharePoint -- e su quelle
    /// si incrociano vendite ed età. L'ordine non è casuale: prima quelle vecchie abbastanza da
    /// essere giudicabili e con i metadati peggiori, perché sono quelle dove il margine è
    /// documentato e il rischio nullo.
    /// </summary>
    [HttpGet("coda")]
    public IActionResult Coda([FromQuery] string library = "ImagesSent",
                              [FromQuery] int take = 24,
                              [FromQuery] string? pageToken = null,
                              [FromQuery] int punteggioMax = 79)
    {
        if (!_s.Enabled) return Ok(new { ok = false, error = "Pipeline disabilitata." });

        try
        {
            var vendite = _sales.Range(null, null);
            var perFile = vendite
                .GroupBy(v => Path.GetFileNameWithoutExtension(v.FileName).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => new
                              {
                                  n = g.Count(),
                                  ricavi = g.Sum(x => x.Royalty),
                                  ultima = g.Max(x => x.SoldAt),
                              },
                              StringComparer.OrdinalIgnoreCase);

            var page = _sp.ListItems(library, take, pageToken, null, null, null, punteggioMax);
            var adesso = DateTimeOffset.UtcNow;

            var righe = _punteggiatore.Raggruppa(library, page.Items).Select(gruppo =>
            {
                var i = Punteggiatore.PortatoreDi(gruppo);
                var kw = Punteggiatore.KeywordDi(i.Tags);
                var v = _punteggiatore.Valuta(gruppo);
                var b = Path.GetFileNameWithoutExtension(i.FileName).Trim();
                perFile.TryGetValue(b, out var vend);

                DateTimeOffset.TryParse(i.Modified, out var quando);
                var eta = quando == default ? TimeSpan.Zero : adesso - quando;
                var giudicabile = eta >= TempoPerGiudicare;

                var (consiglio, perche) = Decidi(vend?.n ?? 0, v.Score, giudicabile, eta);

                return new
                {
                    i.Id,
                    fileName = i.FileName,
                    i.Title,
                    keywords = kw,
                    punteggio = v.Score,
                    vendite = vend?.n ?? 0,
                    ricavi = Math.Round(vend?.ricavi ?? 0m, 2),
                    ultimaVendita = vend?.ultima,
                    giorniOnline = eta == TimeSpan.Zero ? (int?)null : (int)eta.TotalDays,
                    consiglio = consiglio.ToString(),
                    perche,
                    problemi = v.Issues.Where(x => x.Severity != "info")
                                       .Select(x => new { x.Severity, x.Field, x.Message })
                                       .Take(4),
                    previewUrl = Miniatura(i.ServerRelativeUrl),
                    fileUrl = Diretto(i.ServerRelativeUrl),
                };
            })
            .OrderBy(r => r.punteggio)
            .ToList();

            return Ok(new
            {
                ok = true,
                library,
                righe,
                nextPageToken = page.NextPageToken,
                riepilogo = new
                {
                    daRilavorare = righe.Count(r => r.consiglio == nameof(Consiglio.RilavoraMetadati)),
                    soggettoDebole = righe.Count(r => r.consiglio == nameof(Consiglio.SoggettoDebole)),
                    daLasciare = righe.Count(r => r.consiglio == nameof(Consiglio.LasciaStare)),
                    troppoPresto = righe.Count(r => r.consiglio == nameof(Consiglio.Aspetta)),
                },
            });
        }
        catch (Exception ex) when (ex.Message.Contains("5.000", StringComparison.Ordinal))
        {
            return Ok(new { ok = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Coda di bonifica non riuscita su {Library}", library);
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Il consiglio, e soprattutto il perché.
    ///
    /// Un consiglio senza motivazione è un ordine, e su un portfolio da diecimila immagini nessuno
    /// dovrebbe eseguire ordini che non capisce. La motivazione dice anche su quale base sta: dove
    /// c'è una regola Adobe la si cita, dove c'è solo una scelta nostra lo si ammette.
    /// </summary>
    private static (Consiglio, string) Decidi(int vendite, int punteggio, bool giudicabile, TimeSpan eta)
    {
        if (vendite > 0)
            return (Consiglio.LasciaStare,
                $"Ha già venduto {vendite} {(vendite == 1 ? "volta" : "volte")}. Adobe non dichiara cosa " +
                "succede al posizionamento di un'immagine dopo una modifica dei metadati: su una che " +
                "funziona, il rischio non è misurabile e il guadagno nemmeno.");

        if (!giudicabile)
            return (Consiglio.Aspetta,
                eta == TimeSpan.Zero
                    ? "Non risulta da quanto è online: senza quella data non si può dire se è presto o tardi."
                    : $"Online da {(int)eta.TotalDays} giorni. Troppo pochi per dire che non vende: " +
                      "non ha ancora attraversato una stagione intera.");

        if (punteggio < MetadatiDeboli)
            return (Consiglio.RilavoraMetadati,
                $"Ferma da {(int)eta.TotalDays} giorni con metadati da {punteggio}. È il caso con la base " +
                "più solida: Adobe consente di modificare titolo e keyword di un'immagine già pubblicata, " +
                "e dichiara che rifinire i metadati migliora la visibilità in ricerca.");

        return (Consiglio.SoggettoDebole,
            $"Ferma da {(int)eta.TotalDays} giorni ma i metadati valgono {punteggio}: il problema non sono " +
            "loro. Riscriverli non cambierebbe niente. Non c'è nulla di documentato da fare qui -- e " +
            "cancellarla non gioverebbe alle altre, perché Adobe non dichiara nessun effetto del genere.");
    }

    private string Diretto(string serverRelativeUrl) =>
        UrlSharePoint.Diretto(_s.SiteUrl!, serverRelativeUrl);

    private string Miniatura(string serverRelativeUrl) =>
        UrlSharePoint.Miniatura(_s.SiteUrl!, serverRelativeUrl);
}

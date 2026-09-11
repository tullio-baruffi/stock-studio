using Microsoft.AspNetCore.Mvc;
using StockStudio.Api.Services.Sales;

namespace StockStudio.Api.Controllers;

/// <summary>
/// Il quadro strategico: gli stessi numeri delle vendite, letti però come domande.
///
/// La scheda Vendite dice cosa è successo. Questa dice cosa farne. La differenza non è cosmetica:
/// davanti a un archivio di tremila movimenti nessuno calcola a mano il rapporto fra quanto ha
/// caricato e quanto ha venduto, o quale quota del magazzino non abbia mai prodotto nulla. Finché
/// quei numeri non sono scritti da qualche parte, le decisioni si prendono a sensazione.
///
/// ## Sul rapporto caricamenti/vendite
/// Adobe mette in evidenza dieci autori a settimana per ciascun tipo di risorsa, e li ordina per
/// il rapporto fra caricamenti e vendite considerando *solo* le risorse caricate negli ultimi sei
/// mesi. È una classifica di efficienza, non di volume: un magazzino enorme di file che non
/// vendono peggiora il rapporto invece di migliorarlo. Qui il rapporto si calcola per davvero,
/// perché è l'unico numero su cui si possa agire di settimana in settimana.
/// </summary>
[ApiController]
[Route("api/strategy")]
public class StrategyController : ControllerBase
{
    private readonly SalesStore _store;

    public StrategyController(SalesStore store) => _store = store;

    /// <summary>Finestra che Adobe considera per la classifica: sei mesi di caricamenti.</summary>
    private const int MesiFinestra = 6;

    [HttpGet("quadro")]
    public IActionResult Quadro([FromQuery] int caricatiUltimiSeiMesi = 0)
    {
        var tutte = _store.Range(null, null);
        if (tutte.Count == 0)
            return Ok(new { ok = true, vuoto = true });

        var oggi = DateTimeOffset.UtcNow;
        var settimana = tutte.Where(s => s.SoldAt >= oggi.AddDays(-7)).ToList();
        var mese = tutte.Where(s => s.SoldAt >= oggi.AddDays(-30)).ToList();

        // Due anni affiancati: è l'unico confronto che distingua una stagione storta da una
        // tendenza. Mesi pieni soltanto, così l'ultimo mese incompleto non falsa la direzione.
        var inizioMeseCorrente = new DateTimeOffset(oggi.Year, oggi.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var annoRecente = Fascia(tutte, inizioMeseCorrente.AddMonths(-12), inizioMeseCorrente);
        var annoPrima = Fascia(tutte, inizioMeseCorrente.AddMonths(-24), inizioMeseCorrente.AddMonths(-12));

        // Quanto è concentrato il guadagno: pochi file che fanno metà dei ricavi significano che
        // produrne altri mille alla cieca non sposta niente.
        var perFile = tutte.GroupBy(s => s.FileName)
                           .Select(g => g.Sum(x => x.Royalty))
                           .OrderByDescending(v => v).ToList();
        decimal totale = perFile.Sum(), meta = totale / 2, corsa = 0;
        int fileMeta = 0;
        foreach (var v in perFile) { corsa += v; fileMeta++; if (corsa >= meta) break; }

        var perTipoSettimana = settimana.GroupBy(s => s.AssetType)
            .Select(g => new { tipo = g.Key, vendite = g.Count(), ricavi = R(g.Sum(x => x.Royalty)) })
            .OrderByDescending(x => x.vendite).ToList();

        // Il rapporto vero, quando l'autore sa quanti file ha consegnato nella finestra. Senza quel
        // numero si può solo dire quante vendite la finestra ha prodotto, e lo si dice.
        object? rapporto = null;
        if (caricatiUltimiSeiMesi > 0)
        {
            var venditeFinestra = tutte.Count(s => s.SoldAt >= oggi.AddMonths(-MesiFinestra));
            rapporto = new
            {
                caricati = caricatiUltimiSeiMesi,
                venditeSeiMesi = venditeFinestra,
                venditeSettimana = settimana.Count,
                // Vendite per file caricato: è la grandezza che la classifica ordina.
                perFileCaricato = Math.Round((double)venditeFinestra / caricatiUltimiSeiMesi, 3),
                perFileCaricatoSettimana = Math.Round((double)settimana.Count / caricatiUltimiSeiMesi, 4),
            };
        }

        var perTipoAnno = annoRecente.GroupBy(s => s.AssetType)
            .Select(g => new
            {
                tipo = g.Key,
                vendite = g.Count(),
                ricavi = R(g.Sum(x => x.Royalty)),
                perDownload = R(g.Sum(x => x.Royalty) / g.Count()),
            })
            .OrderByDescending(x => x.ricavi).ToList();

        return Ok(new
        {
            ok = true,
            vuoto = false,
            aggiornato = oggi.ToString("o"),
            archivio = new
            {
                vendite = tutte.Count,
                ricavi = R(totale),
                dal = tutte.Min(s => s.SoldAt).ToString("o"),
                al = tutte.Max(s => s.SoldAt).ToString("o"),
                fileVenduti = perFile.Count,
                fileMetaRicavi = fileMeta,
            },
            settimana = new { vendite = settimana.Count, ricavi = R(settimana.Sum(s => s.Royalty)), perTipo = perTipoSettimana },
            mese = new { vendite = mese.Count, ricavi = R(mese.Sum(s => s.Royalty)) },
            finestra = new { mesi = MesiFinestra, rapporto },
            tendenza = new
            {
                recente = Sintesi(annoRecente),
                precedente = Sintesi(annoPrima),
                variazioneVendite = Var(annoPrima.Count, annoRecente.Count),
                variazioneRicavi = Var((double)annoPrima.Sum(s => s.Royalty), (double)annoRecente.Sum(s => s.Royalty)),
            },
            perTipoAnno,
        });
    }

    private static List<Sale> Fascia(IReadOnlyList<Sale> s, DateTimeOffset a, DateTimeOffset b) =>
        s.Where(x => x.SoldAt >= a && x.SoldAt < b).ToList();

    private static object Sintesi(List<Sale> s) => new
    {
        vendite = s.Count,
        ricavi = R(s.Sum(x => x.Royalty)),
        perDownload = s.Count > 0 ? R(s.Sum(x => x.Royalty) / s.Count) : 0m,
    };

    /// <summary>Variazione percentuale, oppure null quando il termine di paragone non esiste.</summary>
    private static double? Var(double prima, double dopo) =>
        prima <= 0 ? null : Math.Round((dopo - prima) / prima * 100, 1);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

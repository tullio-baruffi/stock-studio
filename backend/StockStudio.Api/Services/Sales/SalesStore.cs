using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;

namespace StockStudio.Api.Services.Sales;

/// <summary>Una licenza venduta, come la racconta l'esportazione di Adobe Stock.</summary>
public record Sale(
    DateTimeOffset SoldAt,
    string AssetId,
    string Title,
    string License,
    decimal Royalty,
    string AssetType,
    string FileName,
    string Size)
{
    /// <summary>
    /// Chiave stabile della vendita: stesso istante e stesso asset sono la stessa riga.
    ///
    /// Serve perché il portale esporta a finestre di date e le finestre si sovrappongono quasi
    /// sempre -- si riscarica "l'ultimo mese" sopra un archivio che arriva a ieri. Senza una chiave
    /// deterministica ogni sovrapposizione gonfierebbe i guadagni, che è esattamente l'errore che
    /// questo strumento esiste per non fare.
    /// </summary>
    public string Key => $"{SoldAt.ToUniversalTime():yyyyMMddHHmmss}-{AssetId}";

    /// <summary>Mese di competenza, usato come partizione: le letture sono quasi sempre per periodo.</summary>
    public string Month => SoldAt.ToUniversalTime().ToString("yyyy-MM");
}

/// <summary>Esito di un'importazione, in termini che si possano riferire a chi l'ha lanciata.</summary>
public record ImportOutcome(int Read, int Imported, int Duplicates, int Skipped,
                            DateTimeOffset? From, DateTimeOffset? To, decimal Total);

/// <summary>
/// L'archivio delle vendite.
///
/// Sta su Azure Table e non su un file come il journal dei feedback, per una ragione appresa:
/// la pubblicazione sostituisce la cartella dell'applicazione, e con essa qualunque file scritto
/// accanto ai binari. Le vendite sono il registro contabile del lavoro: non possono dipendere dal
/// prossimo rilascio.
/// </summary>
public class SalesStore
{
    private readonly TableClient _table;
    private readonly ILogger<SalesStore> _log;

    public SalesStore(IOptions<TableOptions> opt, ILogger<SalesStore> log)
    {
        _log = log;
        // Tabella separata da quella dei job: hanno cicli di vita diversi, e i job si possono
        // svuotare senza portarsi via lo storico dei guadagni.
        _table = new TableClient(opt.Value.ConnectionString, "stockstudiosales");
        _table.CreateIfNotExists();
    }

    /// <summary>Scrive le vendite, saltando quelle già presenti. Ritorna quante erano nuove.</summary>
    public ImportOutcome Save(IReadOnlyList<Sale> sales, int read, int skipped)
    {
        int nuove = 0, doppie = 0;

        foreach (var s in sales)
        {
            var e = new TableEntity(s.Month, s.Key)
            {
                ["SoldAt"] = s.SoldAt.UtcDateTime,
                ["AssetId"] = s.AssetId,
                ["Title"] = s.Title,
                ["License"] = s.License,
                ["Royalty"] = (double)s.Royalty,
                ["AssetType"] = s.AssetType,
                ["FileName"] = s.FileName,
                ["Size"] = s.Size,
            };

            try
            {
                // AddEntity e non Upsert: il conflitto è l'unico modo di distinguere una riga già
                // vista da una nuova, ed è proprio il conteggio che si vuole riferire.
                _table.AddEntity(e);
                nuove++;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                doppie++;
            }
        }

        var totale = sales.Sum(x => x.Royalty);
        _log.LogInformation("Vendite importate: {Nuove} nuove, {Doppie} già presenti", nuove, doppie);

        return new ImportOutcome(read, nuove, doppie, skipped,
                                 sales.Count > 0 ? sales.Min(x => x.SoldAt) : null,
                                 sales.Count > 0 ? sales.Max(x => x.SoldAt) : null,
                                 totale);
    }

    /// <summary>Tutte le vendite dell'intervallo, dalla più recente.</summary>
    public IReadOnlyList<Sale> Range(DateTimeOffset? from, DateTimeOffset? to)
    {
        var filtro = new List<string>();
        if (from is not null) filtro.Add($"SoldAt ge datetime'{from.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");
        if (to is not null) filtro.Add($"SoldAt le datetime'{to.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");

        var query = filtro.Count > 0
            ? _table.Query<TableEntity>(string.Join(" and ", filtro))
            : _table.Query<TableEntity>();

        return query.Select(Read).OrderByDescending(s => s.SoldAt).ToList();
    }

    public int Count() => _table.Query<TableEntity>(select: new[] { "RowKey" }).Count();

    private static Sale Read(TableEntity e) => new(
        e.GetDateTimeOffset("SoldAt") ?? default,
        e.GetString("AssetId") ?? "",
        e.GetString("Title") ?? "",
        e.GetString("License") ?? "",
        (decimal)(e.GetDouble("Royalty") ?? 0),
        e.GetString("AssetType") ?? "",
        e.GetString("FileName") ?? "",
        e.GetString("Size") ?? "");
}

/// <summary>
/// Lettore dell'esportazione "Attività" del portale autori di Adobe Stock.
///
/// Il file non ha riga di intestazione: la prima riga è già una vendita. Le colonne, in ordine,
/// sono data ISO, identificativo dell'asset, titolo, tipo di licenza, royalty col simbolo di
/// valuta, tipo di risorsa, nome del file originale, nome dell'autore, taglia.
///
/// Il titolo contiene virgole e punti quasi sempre, quindi la divisione va fatta rispettando le
/// virgolette: una Split secca spezzerebbe proprio le righe che contano di più.
/// </summary>
public static class AdobeSalesCsv
{
    /// <summary>Colonne dell'esportazione. Una riga più corta non è una vendita.</summary>
    private const int Columns = 9;

    public static (List<Sale> Sales, int Read, int Skipped) Parse(string csv)
    {
        var sales = new List<Sale>();
        int read = 0, skipped = 0;

        foreach (var line in csv.Split('\n'))
        {
            var row = line.Trim('\r', ' ');
            if (row.Length == 0) continue;
            read++;

            var c = SplitCsv(row);

            // Chi passa dal foglio di calcolo si porta dietro l'intestazione che ha aggiunto lui:
            // se la prima colonna non è una data, la riga non è una vendita e si tace.
            if (c.Count < Columns || !DateTimeOffset.TryParse(c[0], CultureInfo.InvariantCulture,
                                                              DateTimeStyles.AdjustToUniversal, out var when))
            {
                skipped++;
                continue;
            }

            sales.Add(new Sale(
                when,
                c[1].Trim(),
                c[2].Trim(),
                c[3].Trim().ToLowerInvariant(),
                Money(c[4]),
                c[5].Trim().ToLowerInvariant(),
                c[6].Trim(),
                c[8].Trim()));
        }

        return (sales, read, skipped);
    }

    /// <summary>"$1.10" -> 1.10. La valuta è sempre il dollaro e il separatore sempre il punto.</summary>
    private static decimal Money(string raw)
    {
        var cleaned = new string(raw.Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    private static List<string> SplitCsv(string row)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < row.Length; i++)
        {
            var ch = row[i];
            if (ch == '"')
            {
                // Due virgolette dentro un campo quotato valgono una virgoletta letterale.
                if (quoted && i + 1 < row.Length && row[i + 1] == '"') { cur.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                parts.Add(cur.ToString());
                cur.Clear();
            }
            else cur.Append(ch);
        }

        parts.Add(cur.ToString());
        return parts;
    }
}

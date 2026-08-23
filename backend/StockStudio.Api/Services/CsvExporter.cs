using System.Text;
using StockStudio.Api.Domain;

namespace StockStudio.Api.Services;

/// <summary>
/// Builds the per-site CSV metadata files. Column layouts are best-effort defaults and can be
/// tuned once the exact Adobe Stock / Freepik contributor templates are confirmed.
/// </summary>
public class CsvExporter
{
    public string BuildAdobeStock(Job job)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Filename,Title,Keywords,Category,Releases");
        foreach (var it in Exportable(job))
        {
            var file = it.EpsFile ?? it.JpgFile ?? it.OriginalFileName;
            var kw = string.Join(", ", it.Keywords);
            sb.AppendLine(string.Join(",", Csv(file), Csv(it.Title), Csv(kw), Csv(it.Category), Csv("")));
        }
        return sb.ToString();
    }

    public string BuildFreepik(Job job)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Filename,Title,Keywords");
        foreach (var it in Exportable(job))
        {
            var file = it.EpsFile ?? it.JpgFile ?? it.OriginalFileName;
            var kw = string.Join(";", it.Keywords);
            sb.AppendLine(string.Join(",", Csv(file), Csv(it.Title), Csv(kw)));
        }
        return sb.ToString();
    }

    /// <summary>Items whose processing succeeded — including those already dispatched/published.</summary>
    public static IEnumerable<JobItem> Exportable(Job job) =>
        job.Items.Where(i => i.CompletedAt != null && i.Status != "failed");

    private static string Csv(string? v)
    {
        v ??= "";
        // Spreadsheet applications also accept formulas after leading whitespace.
        var trimmed = v.TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            v = "'" + v;
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StockStudio.Api.Services;

/// <summary>
/// Metadata provider that delegates to the 'metadata-generator-001' Logic App instead of calling
/// OpenAI directly.
///
/// The point is to have one prompt, not two. The same description used to be produced in two
/// places — here and in the pipeline Logic App — each with its own copy of the Adobe rules, and
/// the copies drifted apart until they scored six points differently on the same pictures.
///
/// The dependency runs this way round on purpose: a Consumption Logic App with an HTTP trigger is
/// always ready, while this API sits on a plan that goes to sleep. Leaning the part that sleeps on
/// the part that does not is the safe direction, and it also keeps the OpenAI key out of here.
/// </summary>
public class LogicAppMetadataProvider : IMetadataProvider
{
    private readonly AiOptions _opt;
    private readonly IHttpClientFactory _http;
    private readonly MetadataNormalizer _normalizer;
    private readonly ILogger<LogicAppMetadataProvider> _log;

    public string Name => "logicapp";

    public LogicAppMetadataProvider(IOptions<AiOptions> opt, IHttpClientFactory http,
                                    MetadataNormalizer normalizer, ILogger<LogicAppMetadataProvider> log)
    {
        _opt = opt.Value;
        _http = http;
        _normalizer = normalizer;
        _log = log;
    }

    public async Task<MetadataResult> GenerateAsync(string imagePath, string baseName, CancellationToken ct)
    {
        using var img = await Image.LoadAsync<Rgb24>(imagePath, ct);
        return await DescribeAsync(img, baseName, ct);
    }

    public async Task<MetadataResult> GenerateFromBytesAsync(byte[] image, string baseName, CancellationToken ct)
    {
        using var img = Image.Load<Rgb24>(image);
        return await DescribeAsync(img, baseName, ct);
    }

    private async Task<MetadataResult> DescribeAsync(Image<Rgb24> image, string baseName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.GeneratorUrl))
            throw new InvalidOperationException(
                "Ai:GeneratorUrl mancante: senza l'indirizzo della Logic App non c'e' nessuno a cui chiedere i metadati.");

        // The generator accepts a public URL or a data URL. The picture under review lives in
        // SharePoint, not in public storage, so it travels inline — downscaled first, because a
        // 4000px original costs about seven times the tokens and adds nothing on a silhouette.
        var dataUrl = await ToDataUrlAsync(image, ct);

        var payload = JsonSerializer.Serialize(new { imageUrl = dataUrl, fileName = baseName });

        var client = _http.CreateClient();
        // The generator reasons before answering: a short timeout here would abandon a call that
        // is still perfectly healthy, and the work would be billed anyway.
        client.Timeout = TimeSpan.FromSeconds(180);

        using var res = await client.PostAsync(_opt.GeneratorUrl,
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Generatore metadati HTTP {(int)res.StatusCode}: {Truncate(body, 400)}");

        _log.LogInformation("Metadati generati dalla Logic App per {File}", baseName);
        return Parse(body);
    }

    private MetadataResult Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var keywords = new List<string>();
        if (root.TryGetProperty("keywords", out var kw))
        {
            if (kw.ValueKind == JsonValueKind.Array)
                keywords = kw.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            else if (kw.ValueKind == JsonValueKind.String)
                keywords = (kw.GetString() ?? "").Split(',').ToList();
        }

        // The generator answers with title/description/keywords only; the category is not part of
        // that contract, and the pipeline never wrote one either.
        return _normalizer.Normalize(
            GetString(root, "title"),
            GetString(root, "description"),
            keywords,
            GetString(root, "category"),
            _opt.MaxKeywords);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<string> ToDataUrlAsync(Image<Rgb24> img, CancellationToken ct)
    {
        int longEdge = Math.Max(img.Width, img.Height);
        if (_opt.ImageMaxEdge > 0 && longEdge > _opt.ImageMaxEdge)
        {
            double s = (double)_opt.ImageMaxEdge / longEdge;
            img.Mutate(x => x.Resize(Math.Max(1, (int)(img.Width * s)), Math.Max(1, (int)(img.Height * s))));
        }
        using var ms = new MemoryStream();
        await img.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 85 }, ct);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

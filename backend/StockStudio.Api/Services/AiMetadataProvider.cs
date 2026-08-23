using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StockStudio.Api.Services.Feedback;

namespace StockStudio.Api.Services;

/// <summary>
/// AI vision metadata provider. Sends a downscaled image to a vision-capable chat model
/// (Azure OpenAI or OpenAI) and asks for stock-ready title/description/keywords/category as JSON.
/// The rules learned from the author's corrections are appended to the request.
/// </summary>
public class AiMetadataProvider : IMetadataProvider
{
    private readonly AiOptions _opt;
    private readonly IHttpClientFactory _http;
    private readonly MetadataGuidance _guidance;
    private readonly ILogger<AiMetadataProvider> _log;

    public string Name => $"ai-{_opt.Provider}";

    /// <summary>
    /// True for the model families that reason before answering (GPT-5 and the o-series). They
    /// take a different set of request parameters, so the payload has to be shaped accordingly.
    /// Matched on the name because the API exposes no capability flag to ask.
    /// </summary>
    internal static bool IsReasoningModel(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        return m.StartsWith("gpt-5") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4");
    }

    public AiMetadataProvider(IOptions<AiOptions> opt, IHttpClientFactory http,
                              MetadataGuidance guidance, ILogger<AiMetadataProvider> log)
    {
        _opt = opt.Value;
        _http = http;
        _guidance = guidance;
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
        var dataUrl = await ToDataUrlAsync(image, ct);
        // The guide puts the ideal band at 15-35 and the hard cap under 50.
        int maxKw = Math.Clamp(_opt.MaxKeywords, 10, 49);
        int minKw = Math.Min(15, maxKw);

        var system =
            "You are a microstock metadata specialist. You follow the Adobe Stock \"Guide to Mastering " +
            "Metadata\" (v2, August 2021) to the letter, because metadata that breaks it gets rejected " +
            "or buried in search. Its rules, which you must apply:\n" +
            "TITLE\n" +
            "- Concise: aim for 70 characters or fewer. The title is searchable and becomes the URL.\n" +
            "- No brand names, no product names, no people's names.\n" +
            "- No special characters and no repeated punctuation.\n" +
            "KEYWORDS\n" +
            $"- Between {minKw} and {maxKw} tags. Work through every axis listed under WHAT TO DESCRIBE " +
            $"before stopping: on a normal image that yields about {Math.Min(maxKw, minKw + 10)} tags. " +
            "Return fewer only when the picture is genuinely bare, and never pad with vague synonyms.\n" +
            "- Order matters. The first 10 carry the most weight, so put the most important subject " +
            "terms first, and make sure every significant word of the title appears among them.\n" +
            "- Nouns must be singular: 'cat', never 'cats'.\n" +
            "- Adjectives must be descriptive, not subjective: 'red', 'furry', 'sunny' are fine; " +
            "'cute', 'beautiful', 'stunning', 'trendy', 'graceful', 'elegant', 'dramatic', " +
            "'majestic' and any other judgement of taste are not.\n" +
            "- Only terms a dictionary would list. 'monkey bars' is a real phrase, 'red dress' is not: " +
            "split invented combinations into their separate words.\n" +
            "- Each tag is a real word or a natural phrase of at most three words, with normal spaces. " +
            "Never concatenate words into one token: 'svg cut file' is valid, 'svgcutfile' is not.\n" +
            "- No people's names, no trademarks.\n" +
            "WHAT TO DESCRIBE\n" +
            "- Main subject, using the most specific singular noun you can justify from the image. " +
            "Specific sells: prefer 'indian classical dancer' over 'dancer', 'golden retriever' over 'dog'. " +
            "Never drop a distinguishing detail just to keep the title short.\n" +
            "- Its pattern, colour, texture or condition.\n" +
            "- Theme, supporting details, action (root form of the verb: run, jump), setting.\n" +
            "- Concepts the subject evokes (competition, luxury, freedom).\n" +
            "- Only if the image contains no human being at all, include both 'no people' and 'nobody'. " +
            "A silhouette, outline or stylised drawing of a person IS a person: in that case never use " +
            "those two keywords, and describe the figures instead (man, woman, dancer, crowd).\n" +
            "- For a vector ALWAYS include both 'vector' and 'graphic' among the keywords, and 'icon' " +
            "too when it is an icon. For an illustration include 'illustration', 'art' and 'graphic'.\n" +
            "- Skip Latin or scientific names on stylised or cartoon-like subjects.";

        // Rules the review process distilled from this author's own corrections. They live in the
        // system message on purpose: appended to the user turn they were treated as a suggestion.
        var learned = _guidance.PromptBlock;
        if (learned.Length > 0)
        {
            system += learned;
            _log.LogDebug("Prompt metadati esteso con la guida appresa v{V}", _guidance.Current.Version);
        }

        var instructions =
            "Analyze the image and return ONLY a JSON object with keys: " +
            "\"title\" (see the TITLE rules), " +
            "\"description\" (one clear factual sentence), " +
            $"\"keywords\" (an array of {minKw}-{maxKw} lowercase English tags, ordered by relevance, no duplicates), " +
            "\"category\" (a single Adobe Stock category name). " +
            $"The artwork is a black and white vector silhouette/illustration. Filename hint: '{baseName}'.";

        var payload = new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = instructions },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "auto" } }
                    }
                }
            },
            ["response_format"] = new { type = "json_object" },
        };

        // The GPT-5 family reasons before answering, and rejects both "temperature" other than the
        // default and "max_tokens". Its budget also has to cover the reasoning that precedes the
        // JSON: asking for 900 there returns an empty string with finish_reason "length" — billed
        // in full for nothing. Older models keep the settings they were tuned with.
        if (IsReasoningModel(_opt.EffectiveModel))
        {
            payload["max_completion_tokens"] = Math.Max(_opt.MaxCompletionTokens, 1200);
        }
        else
        {
            payload["temperature"] = _opt.Temperature;
            payload["max_tokens"] = 900;
        }

        var isAzure = string.Equals(_opt.Provider, "azure", StringComparison.OrdinalIgnoreCase);
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);

        string url;
        if (isAzure)
        {
            var endpoint = (_opt.Endpoint ?? throw new InvalidOperationException("Ai:Endpoint mancante per Azure.")).TrimEnd('/');
            url = $"{endpoint}/openai/deployments/{_opt.EffectiveModel}/chat/completions?api-version={_opt.ApiVersion}";
            client.DefaultRequestHeaders.Add("api-key", _opt.ApiKey);
        }
        else
        {
            url = "https://api.openai.com/v1/chat/completions";
            payload["model"] = _opt.EffectiveModel;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        }

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI ({_opt.Provider}) HTTP {(int)res.StatusCode}: {Truncate(body, 500)}");

        return Parse(body);
    }

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

    private MetadataResult Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        using var md = JsonDocument.Parse(content);
        var root = md.RootElement;

        var title = GetString(root, "title") ?? "Vector Silhouette";
        var description = GetString(root, "description") ?? title;
        var category = GetString(root, "category") ?? "Graphic Resources";

        var keywords = new List<string>();
        if (root.TryGetProperty("keywords", out var kw))
        {
            if (kw.ValueKind == JsonValueKind.Array)
                keywords = kw.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
            else if (kw.ValueKind == JsonValueKind.String)
                keywords = (kw.GetString() ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }
        keywords = keywords.Select(k => k.ToLowerInvariant())
            .Distinct()
            .ToList();

        // Hard guard rail. The written ban is respected most of the time, and "most of the time"
        // would still push terms the author rejected onto Adobe Stock and Freepik.
        var banned = _guidance.BannedKeywords;
        if (banned.Count > 0)
        {
            var block = banned.ToHashSet(StringComparer.OrdinalIgnoreCase);
            int before = keywords.Count;
            keywords = keywords.Where(k => !block.Contains(k)).ToList();
            if (keywords.Count < before)
                _log.LogInformation("Rimosse {N} keyword vietate dai feedback dell'autore", before - keywords.Count);
        }

        // Same reasoning for the guide's own rules: asking politely in the prompt left subjective
        // adjectives in, dropped the mandatory "graphic", and kept "no people" on pictures of people.
        var raw = keywords.Count;
        keywords = AdobeStockRules.Enforce(keywords, title, "vector", _opt.MaxKeywords);
        if (keywords.Count != raw)
            _log.LogDebug("Keyword normalizzate secondo la guida Adobe: {Before} -> {After}", raw, keywords.Count);

        return new MetadataResult(title.Trim(), description.Trim(), keywords, category.Trim());
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

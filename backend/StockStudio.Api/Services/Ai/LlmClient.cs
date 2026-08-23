using System.Text;
using System.Text.Json;

namespace StockStudio.Api.Services.Ai;

public record LlmTool(string Name, string Description, object ParametersSchema);

public record LlmToolCall(string Id, string Name, string ArgumentsJson);

public record LlmReply(string? Content, IReadOnlyList<LlmToolCall> ToolCalls);

/// <summary>
/// Minimal chat client for any OpenAI-compatible endpoint, with tool calling.
///
/// Deliberately provider-agnostic: the same code talks to Azure OpenAI, api.openai.com, or a local
/// server (Ollama, LM Studio, vLLM), because the only thing that changes is the URL and the auth
/// header. Nothing here assumes a specific vendor.
/// </summary>
public class LlmClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<LlmClient> _log;

    public LlmClient(IHttpClientFactory http, IConfiguration cfg, ILogger<LlmClient> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string Provider => (_cfg["Ai:Provider"] ?? "").Trim().ToLowerInvariant();
    public string? Endpoint => _cfg["Ai:Endpoint"]?.TrimEnd('/');
    private string? ApiKey => _cfg["Ai:ApiKey"];

    /// <summary>
    /// The agent needs tool calling, which not every model offers — the strongest vision model in
    /// this account refuses tools outright. Ai:AgentModel keeps that choice separate from the one
    /// used to read images.
    /// </summary>
    public string Model => _cfg["Ai:AgentModel"] ?? _cfg["Ai:Model"] ?? _cfg["Ai:Deployment"] ?? "gpt-4o-mini";
    private string ApiVersion => _cfg["Ai:ApiVersion"] ?? "2024-08-01-preview";

    /// <summary>
    /// Dove parla l'agente, che non e' detto sia dove nascono i metadati.
    ///
    /// Ai:Provider risponde alla domanda "chi scrive titolo e descrizione", e "logicapp" significa
    /// che li scrive la Logic App: non che non ci sia un modello da interpellare. L'agente serve a
    /// un'altra cosa -- le Opportunita' -- vuole il tool calling, e puo' benissimo parlare con
    /// OpenAI mentre i metadati passano dalla Logic App.
    ///
    /// Finche' quel singolo valore governava entrambi, impostare "logicapp" spegneva l'agente con
    /// la chiave OpenAI configurata e inutilizzata, e la Configurazione dichiarava tutto il gruppo
    /// "AI e metadati" incompleto quando invece i metadati funzionavano benissimo. Qui il provider
    /// dell'agente si deduce da cio' che c'e': un endpoint con deployment e' Azure, un endpoint
    /// soltanto e' un servizio compatibile, una chiave soltanto e' OpenAI.
    /// </summary>
    public string AgentProvider
    {
        get
        {
            if (Provider is "azure" or "openai" or "compatible") return Provider;
            if (!string.IsNullOrWhiteSpace(Endpoint))
                return string.IsNullOrWhiteSpace(_cfg["Ai:Deployment"]) ? "compatible" : "azure";
            return string.IsNullOrWhiteSpace(ApiKey) ? "" : "openai";
        }
    }

    /// <summary>True when enough configuration exists to attempt a call.</summary>
    public bool IsConfigured => AgentProvider switch
    {
        "azure" => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey)
                   && !string.IsNullOrWhiteSpace(_cfg["Ai:Deployment"]),
        "openai" => !string.IsNullOrWhiteSpace(ApiKey),
        "compatible" => !string.IsNullOrWhiteSpace(Endpoint),
        _ => false,
    };

    public string Describe() => AgentProvider switch
    {
        "azure" => $"Azure OpenAI · {_cfg["Ai:Deployment"]} · {Endpoint}",
        "openai" => $"OpenAI · {Model}",
        "compatible" => $"Endpoint compatibile · {Model} · {Endpoint}",
        _ => "non configurato",
    };

    private (string Url, string HeaderName, string HeaderValue) Route() => AgentProvider switch
    {
        "azure" => ($"{Endpoint}/openai/deployments/{_cfg["Ai:Deployment"]}/chat/completions?api-version={ApiVersion}",
                    "api-key", ApiKey!),
        "openai" => ("https://api.openai.com/v1/chat/completions", "Authorization", $"Bearer {ApiKey}"),
        _ => ($"{Endpoint}/v1/chat/completions", "Authorization", $"Bearer {ApiKey ?? "local"}"),
    };

    /// <summary>
    /// One chat turn. <paramref name="messages"/> is the running conversation (including tool
    /// results); returns either text or the tool calls the model wants executed.
    /// </summary>
    public async Task<LlmReply> ChatAsync(IEnumerable<object> messages, IReadOnlyList<LlmTool>? tools,
                                          double temperature, bool jsonMode, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Motore AI non configurato.");

        var (url, headerName, headerValue) = Route();

        var payload = new Dictionary<string, object?>
        {
            ["messages"] = messages,
        };
        // Reasoning models accept only the default temperature and reject the parameter outright,
        // so it is sent only to the models that can actually honour it.
        if (!AiMetadataProvider.IsReasoningModel(Model)) payload["temperature"] = temperature;
        // Azure takes the model from the deployment in the URL; everyone else needs it in the body.
        if (Provider != "azure") payload["model"] = Model;
        if (jsonMode) payload["response_format"] = new { type = "json_object" };
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema },
            }).ToArray();
            payload["tool_choice"] = "auto";
        }

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation(headerName, headerValue);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("LLM {Status}: {Body}", (int)res.StatusCode, Trim(body));
            throw new HttpRequestException($"Il motore AI ha risposto {(int)res.StatusCode}: {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

        string? content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        var calls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                calls.Add(new LlmToolCall(
                    call.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    fn.GetProperty("name").GetString() ?? "",
                    fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}"));
            }
        }

        return new LlmReply(content, calls);
    }

    /// <summary>Quick reachability probe used by the Sistema panel.</summary>
    public async Task<(bool Ok, string Detail)> PingAsync(CancellationToken ct)
    {
        if (!IsConfigured) return (false, "Nessuna configurazione AI (Ai:Provider mancante).");
        try
        {
            var reply = await ChatAsync(
                new object[]
                {
                    new { role = "user", content = "Rispondi solo con: ok" },
                },
                null, 0, false, ct);
            return (true, $"{Describe()} → \"{reply.Content?.Trim()}\"");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;
}

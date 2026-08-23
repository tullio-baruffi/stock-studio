namespace StockStudio.Api.Services;

/// <summary>Configuration for the AI vision metadata provider (Azure OpenAI or OpenAI).</summary>
public class AiOptions
{
    /// <summary>"stub" (default, no AI), "openai", "azure", or "logicapp".</summary>
    public string Provider { get; set; } = "stub";

    /// <summary>API key. Store in user-secrets / env, never in appsettings.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Azure resource endpoint, e.g. https://my-res.openai.azure.com (Azure only).</summary>
    public string? Endpoint { get; set; }

    /// <summary>OpenAI model name, or Azure deployment name.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Model for reading images, when it should differ from <see cref="Model"/>. The vision task
    /// and the agent have incompatible requirements — the best vision model here does not support
    /// tool calling at all — so a single setting cannot serve both.
    /// </summary>
    public string? VisionModel { get; set; }

    /// <summary>The model actually used to describe an image.</summary>
    public string EffectiveModel =>
        string.IsNullOrWhiteSpace(VisionModel) ? Model : VisionModel!;

    /// <summary>Azure API version.</summary>
    public string ApiVersion { get; set; } = "2024-08-01-preview";

    /// <summary>Downscale the image to this longest edge (px) before sending, to save tokens.</summary>
    public int ImageMaxEdge { get; set; } = 1024;

    /// <summary>
    /// Maximum number of keywords to keep. The Adobe Stock metadata guide puts the ideal band at
    /// 15-35 and asks to stay under 50, so 35 is the default rather than the hard cap.
    /// </summary>
    public int MaxKeywords { get; set; } = 35;

    public double Temperature { get; set; } = 0.4;

    /// <summary>
    /// Callback address of the 'metadata-generator-001' Logic App, used when Provider is
    /// "logicapp". It carries its own signature, so it belongs in Key Vault, not in appsettings.
    /// </summary>
    public string? GeneratorUrl { get; set; }

    /// <summary>
    /// Token budget for the reasoning models, which must cover the thinking that precedes the JSON
    /// answer. Too low and the reply comes back empty with finish_reason "length" — and is billed.
    /// </summary>
    public int MaxCompletionTokens { get; set; } = 2000;
}

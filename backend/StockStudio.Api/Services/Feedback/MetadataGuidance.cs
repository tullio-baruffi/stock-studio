using System.Text.Json;

namespace StockStudio.Api.Services.Feedback;

/// <summary>
/// The extra instructions the review process derived from the author's corrections. They are
/// appended to the metadata generation prompt so the next images already respect those lessons.
/// </summary>
public record GuidanceState(
    int Version,
    string Text,
    DateTime? UpdatedAt,
    int BasedOnEntries,
    DateTime? LastEntryAt,
    string Engine,
    IReadOnlyList<string> BannedKeywords)
{
    public static GuidanceState Empty => new(0, "", null, 0, null, "none", Array.Empty<string>());

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>Persists the learned guidance so it survives restarts and can be inspected or reset.</summary>
public class MetadataGuidance
{
    private readonly string _file;
    private readonly ILogger<MetadataGuidance> _log;
    private readonly object _gate = new();
    private GuidanceState _state = GuidanceState.Empty;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public MetadataGuidance(ILogger<MetadataGuidance> log)
    {
        _log = log;
        var dir = Path.Combine(AppContext.BaseDirectory, "metadata-feedback");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "guidance.json");
        Load();
    }

    public GuidanceState Current { get { lock (_gate) return _state; } }

    /// <summary>The block injected into the generation prompt, or empty when nothing was learned yet.</summary>
    public string PromptBlock
    {
        get
        {
            var s = Current;
            if (!s.HasText) return "";
            return "\n\nMANDATORY RULES, learned from corrections this author applied to your previous "
                 + "output. They override your defaults and the generic instructions. Apply every one "
                 + "of them; a keyword the author banned must never appear, and a keyword the author "
                 + "requires must be present whenever it is relevant to the image:\n" + s.Text.Trim();
        }
    }

    /// <summary>
    /// Keywords the author removed and never added back. Enforced in code after generation:
    /// the model follows a written ban only most of the time, and "most of the time" would still
    /// push banned terms onto the marketplaces.
    /// </summary>
    public IReadOnlyList<string> BannedKeywords => Current.BannedKeywords;

    public GuidanceState Update(string text, int basedOn, DateTime? lastEntryAt, string engine,
                                IReadOnlyList<string> banned)
    {
        lock (_gate)
        {
            _state = new GuidanceState(_state.Version + 1, text.Trim(), DateTime.UtcNow,
                                       basedOn, lastEntryAt, engine, banned);
            Persist();
            return _state;
        }
    }

    public GuidanceState Reset()
    {
        lock (_gate)
        {
            _state = GuidanceState.Empty;
            Persist();
            return _state;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            _state = JsonSerializer.Deserialize<GuidanceState>(File.ReadAllText(_file)) ?? GuidanceState.Empty;
            // Files written before the ban list existed deserialize it as null.
            if (_state.BannedKeywords is null)
                _state = _state with { BannedKeywords = Array.Empty<string>() };
            if (_state.HasText)
                _log.LogInformation("Guida metadati appresa caricata: versione {V}, da {N} feedback", _state.Version, _state.BasedOnEntries);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Guida metadati illeggibile: riparto dal prompt predefinito");
            _state = GuidanceState.Empty;
        }
    }

    private void Persist()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Salvataggio della guida metadati non riuscito");
        }
    }
}

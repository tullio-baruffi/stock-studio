using Microsoft.AspNetCore.Mvc;
using StockStudio.Api.Domain;
using StockStudio.Api.Models;
using StockStudio.Api.Services.Ai;
using StockStudio.Api.Services.Feedback;

namespace StockStudio.Api.Controllers;

/// <summary>
/// The review loop: read the corrections the author made to the generated metadata, rebuild the
/// extra prompt rules from them, and expose the result so it can be inspected or reset.
/// </summary>
[ApiController]
[Route("api/metadata-prompt")]
public class MetadataPromptController : ControllerBase
{
    private readonly MetadataFeedbackStore _store;
    private readonly MetadataGuidance _guidance;
    private readonly MetadataPromptTuner _tuner;
    private readonly LlmClient _llm;

    public MetadataPromptController(MetadataFeedbackStore store, MetadataGuidance guidance,
                                    MetadataPromptTuner tuner, LlmClient llm)
    {
        _store = store;
        _guidance = guidance;
        _tuner = tuner;
        _llm = llm;
    }

    /// <summary>Current learned guidance plus how much feedback is waiting to be processed.</summary>
    [HttpGet]
    public ActionResult<GuidanceDto> Get() => Ok(Describe());

    /// <summary>Runs the review: processes the feedback and regenerates the metadata prompt rules.</summary>
    [HttpPost("rebuild")]
    public async Task<IActionResult> Rebuild(CancellationToken ct)
    {
        var result = await _tuner.RebuildAsync(ct);
        return Ok(new
        {
            guidance = Describe(),
            engine = result.Engine,
            entriesUsed = result.EntriesUsed,
            warning = result.Warning,
        });
    }

    /// <summary>Clears the learned rules and goes back to the default prompt. Feedback is kept.</summary>
    [HttpDelete]
    public ActionResult<GuidanceDto> Reset()
    {
        _guidance.Reset();
        return Ok(Describe());
    }

    /// <summary>The journalled corrections, most recent first.</summary>
    [HttpGet("feedback")]
    public ActionResult<IReadOnlyList<FeedbackEntryDto>> Feedback([FromQuery] int limit = 50)
        => Ok(_store.Recent(limit).Select(ToDto).ToList());

    /// <summary>Empties the journal. Irreversible, so it is a separate call from resetting the rules.</summary>
    [HttpDelete("feedback")]
    public IActionResult ClearFeedback()
    {
        _store.Clear();
        return Ok(new { ok = true, remaining = _store.Count });
    }

    private GuidanceDto Describe()
    {
        var g = _guidance.Current;
        return new GuidanceDto(
            g.Version,
            g.Text,
            g.UpdatedAt?.ToString("o"),
            g.BasedOnEntries,
            g.Engine,
            _store.Count,
            _tuner.PendingCount,
            _llm.IsConfigured,
            g.BannedKeywords);
    }

    private static MetadataSnapshotDto? Snap(MetadataSnapshot? s) =>
        s is null ? null : new MetadataSnapshotDto(s.Title, s.Description, s.Keywords, s.Category);

    private static FeedbackEntryDto ToDto(FeedbackEntry e) => new(
        e.Id,
        e.At.ToString("o"),
        e.JobId,
        e.ItemId,
        e.BaseName,
        e.Note,
        Snap(e.Generated),
        Snap(e.Corrected)!,
        e.KeywordsAdded,
        e.KeywordsRemoved,
        e.TitleChanged,
        e.CategoryChanged);
}

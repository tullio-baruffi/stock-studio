namespace StockStudio.Api.Models;

public record ItemFilesDto(string? svg, string? eps, string? jpg, string? ai);

public record ItemStepsDto(
    string? queuedAt, string? startedAt, string? vectorizedAt, string? completedAt, string? dispatchedAt, string? publishedAt);

public record ValidationIssueDto(string severity, string field, string message, string? site);
public record SiteScoreDto(string site, int score);
public record ValidationDto(int score, bool blocksDispatch, IReadOnlyList<SiteScoreDto> sites, IReadOnlyList<ValidationIssueDto> issues);

public record ItemDto(
    string id,
    string originalFileName,
    string baseName,
    string status,
    string? error,
    string title,
    string description,
    IReadOnlyList<string> keywords,
    string category,
    ItemFilesDto files,
    string? previewUrl,
    ItemStepsDto steps,
    long? durationMs,
    string? publishStatus,
    string? publishMessage,
    string? metadataSource,
    ValidationDto validation,
    string mode,
    string? feedback,
    bool editedFromAi);

public record JobDto(string id, string createdAt, string mode, IReadOnlyList<ItemDto> items);

public record PublishCallback(
    string? File, string? FilePath, int? StatusCode, string? Message,
    string? Title, string? Description, string? Tags);

public record JobSummaryDto(
    string id,
    string createdAt,
    int total,
    int queued,
    int processing,
    int completed,
    int failed,
    int dispatched,
    int published);

/// <summary>
/// Metadata edit. <paramref name="feedback"/> is optional: the author can explain why the
/// generated metadata was wrong, and the review process turns those notes into prompt rules.
/// </summary>
public record UpdateItemRequest(string? title, string? description, List<string>? keywords, string? category, string? feedback);
public record RevectorizeRequest(bool? AutoThreshold, int? Threshold);
public record ValidateRequest(string? Title, string? Description, List<string>? Keywords);

public record MetadataSnapshotDto(string title, string description, IReadOnlyList<string> keywords, string category);

public record FeedbackEntryDto(
    string id,
    string at,
    string jobId,
    string itemId,
    string baseName,
    string? note,
    MetadataSnapshotDto? generated,
    MetadataSnapshotDto corrected,
    IReadOnlyList<string> keywordsAdded,
    IReadOnlyList<string> keywordsRemoved,
    bool titleChanged,
    bool categoryChanged);

public record GuidanceDto(
    int version,
    string text,
    string? updatedAt,
    int basedOnEntries,
    string engine,
    int totalFeedback,
    int pendingFeedback,
    bool agenticAvailable,
    IReadOnlyList<string> bannedKeywords);

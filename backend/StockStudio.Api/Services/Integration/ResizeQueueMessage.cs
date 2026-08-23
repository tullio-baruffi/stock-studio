namespace StockStudio.Api.Services.Integration;

// The queue message contract now lives in the shared library (StockStudio.Shared.Contracts.ResizeQueueMessage)
// so the Web API (producer) and the Azure Functions app (consumer) share one definition.

/// <summary>Result of dropping one file into the SharePoint back-office.</summary>
public record SharePointUploadResult(string ServerRelativeUrl, int ItemId, string FileName);

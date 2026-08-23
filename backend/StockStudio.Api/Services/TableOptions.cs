namespace StockStudio.Api.Services;

/// <summary>Azure Table Storage settings for job persistence.</summary>
public class TableOptions
{
    /// <summary>
    /// Storage connection string. Defaults to the Azurite emulator for local dev.
    /// In production, source this from Key Vault (e.g. secret "Tables--ConnectionString").
    /// </summary>
    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";

    /// <summary>Table name (alphanumeric, no dashes).</summary>
    public string TableName { get; set; } = "stockstudiojobs";
}

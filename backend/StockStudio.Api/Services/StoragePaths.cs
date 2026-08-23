namespace StockStudio.Api.Services;

/// <summary>
/// Where generated assets live. Centralised because the job store and the static file middleware
/// must agree: if they diverge, files are written in one place and served from another.
/// </summary>
public static class StoragePaths
{
    /// <summary>
    /// On App Service only %HOME%\data survives a redeploy and is writable under
    /// run-from-package; locally there is no HOME, so the content root is used instead.
    /// </summary>
    public static string Root(string contentRootPath)
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(contentRootPath, "Storage")
            : Path.Combine(home, "data", "Storage");
    }
}

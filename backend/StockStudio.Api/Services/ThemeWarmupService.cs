namespace StockStudio.Api.Services;

/// <summary>
/// Fills the Wikipedia cache for the whole theme catalogue in the background.
///
/// Measuring 38 themes takes ~90 seconds against a rate-limited API. Doing it lazily on the first
/// request means the user waits; doing it here, slowly and out of the way, means the Opportunità
/// tab answers instantly and the data is already on disk after the first run.
/// </summary>
public class ThemeWarmupService : BackgroundService
{
    private readonly WikipediaTrendClient _wiki;
    private readonly ILogger<ThemeWarmupService> _log;

    public ThemeWarmupService(WikipediaTrendClient wiki, ILogger<ThemeWarmupService> log)
    {
        _wiki = wiki;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before adding background traffic.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        int done = 0, failed = 0;
        foreach (var theme in StockThemeCatalog.All)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                var signal = await _wiki.GetAsync(theme.WikiArticle, stoppingToken);
                if (signal is null) { failed++; continue; }

                await _wiki.GetDailySeriesAsync(theme.WikiArticle, 15, stoppingToken);
                done++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                failed++;
                _log.LogDebug(ex, "Warm-up fallito per {Article}", theme.WikiArticle);
            }
        }

        _log.LogInformation("Cache temi pronta: {Done} misurati, {Failed} non disponibili", done, failed);
    }
}

namespace StockStudio.Api.Services;

/// <summary>Drains the job queue and processes each job in its own DI scope.</summary>
public class JobProcessorService : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JobProcessorService> _log;

    public JobProcessorService(JobQueue queue, IServiceScopeFactory scopes, ILogger<JobProcessorService> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RequeueUnfinishedJobs(stoppingToken);

        await foreach (var jobId in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<PipelineService>();
                await pipeline.ProcessJobAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Elaborazione job {Job} fallita", jobId);
            }
        }
    }

    /// <summary>
    /// The work queue is in-memory, so a restart would leave items stuck in queued/processing forever.
    /// Re-enqueue those jobs at startup so they resume instead of hanging.
    /// </summary>
    private void RequeueUnfinishedJobs(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
            foreach (var job in store.List(200))
            {
                if (job.Items.Any(i => i.Status is "queued" or "processing"))
                {
                    _queue.EnqueueAsync(job.Id, ct).AsTask().GetAwaiter().GetResult();
                    _log.LogInformation("Job {Job} rimesso in coda dopo il riavvio", job.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Impossibile ripristinare i job non completati");
        }
    }
}

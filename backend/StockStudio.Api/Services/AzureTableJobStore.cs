using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using StockStudio.Api.Domain;

namespace StockStudio.Api.Services;

/// <summary>
/// Job registry backed by Azure Table Storage (local dev: Azurite). Each job is stored as a header
/// entity (PartitionKey "job", RowKey = jobId) plus one entity per item (PartitionKey = jobId,
/// RowKey = itemId, Data = JSON). Generated asset files remain on disk under <see cref="StorageRoot"/>.
/// An in-memory cache lets in-flight mutations share a reference; call <see cref="Save"/> to persist.
/// </summary>
public class AzureTableJobStore : IJobStore
{
    private const string HeaderPartition = "job";

    private readonly ConcurrentDictionary<string, Job> _cache = new();
    private readonly IWebHostEnvironment _env;
    private readonly TableClient _table;

    public AzureTableJobStore(IWebHostEnvironment env, IOptions<TableOptions> opt, ILogger<AzureTableJobStore> log)
    {
        _env = env;
        Directory.CreateDirectory(StorageRoot);
        _table = new TableClient(opt.Value.ConnectionString, opt.Value.TableName);
        _table.CreateIfNotExists();
        log.LogInformation("Job store: Azure Table '{Table}'", opt.Value.TableName);
    }

    public string StorageRoot => StoragePaths.Root(_env.ContentRootPath);
    public string JobDir(string jobId) => Path.Combine(StorageRoot, jobId);

    public void Add(Job job) => Save(job);

    public void Save(Job job)
    {
        _cache[job.Id] = job;

        var header = new TableEntity(HeaderPartition, job.Id)
        {
            ["CreatedAt"] = job.CreatedAt.ToUniversalTime(),
        };
        _table.UpsertEntity(header, TableUpdateMode.Replace);

        for (int i = 0; i < job.Items.Count; i++)
        {
            var item = job.Items[i];
            var entity = new TableEntity(job.Id, item.Id)
            {
                ["Ordinal"] = i,
                ["Data"] = JsonSerializer.Serialize(item),
            };
            _table.UpsertEntity(entity, TableUpdateMode.Replace);
        }
    }

    public Job? Get(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;

        TableEntity header;
        try
        {
            header = _table.GetEntity<TableEntity>(HeaderPartition, id).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var items = _table.Query<TableEntity>(e => e.PartitionKey == id).ToList();
        var job = new Job
        {
            Id = id,
            CreatedAt = header.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.UtcNow,
            Items = items
                .OrderBy(e => e.GetInt32("Ordinal") ?? 0)
                .Select(e => JsonSerializer.Deserialize<JobItem>(e.GetString("Data") ?? "{}")!)
                .ToList(),
        };

        _cache[id] = job;
        return job;
    }

    public IReadOnlyList<Job> List(int limit = 50)
    {
        var headers = _table.Query<TableEntity>(e => e.PartitionKey == HeaderPartition).ToList();
        return headers
            .OrderByDescending(e => e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue)
            .Take(limit)
            .Select(e => Get(e.RowKey))
            .Where(j => j != null)
            .Select(j => j!)
            .ToList();
    }

    public void Delete(string id)
    {
        _cache.TryRemove(id, out _);
        // Delete item entities (partition = jobId) then the header row.
        foreach (var e in _table.Query<TableEntity>(x => x.PartitionKey == id).ToList())
            _table.DeleteEntity(e.PartitionKey, e.RowKey);
        try { _table.DeleteEntity(HeaderPartition, id); } catch (RequestFailedException ex) when (ex.Status == 404) { }

        // Best-effort: remove generated asset files on disk.
        try
        {
            var dir = JobDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { /* best effort */ }
    }

    public void IndexDispatch(string fileName, string jobId, string itemId)    {
        var entity = new TableEntity("dispatch", SanitizeKey(fileName))
        {
            ["JobId"] = jobId,
            ["ItemId"] = itemId,
            ["FileName"] = fileName,
        };
        _table.UpsertEntity(entity, TableUpdateMode.Replace);
    }

    public (string jobId, string itemId)? ResolveDispatch(string fileName)
    {
        try
        {
            var e = _table.GetEntity<TableEntity>("dispatch", SanitizeKey(fileName)).Value;
            var jobId = e.GetString("JobId");
            var itemId = e.GetString("ItemId");
            if (jobId == null || itemId == null) return null;
            // The index outlives job deletion; verify the target still exists before returning it.
            return Get(jobId)?.Items.Any(i => i.Id == itemId) == true ? (jobId, itemId) : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // Azure Table keys cannot contain / \ # ? or control chars.
    private static string SanitizeKey(string s) =>
        new string(s.Select(c => c is '/' or '\\' or '#' or '?' ? '_' : c).ToArray());
}

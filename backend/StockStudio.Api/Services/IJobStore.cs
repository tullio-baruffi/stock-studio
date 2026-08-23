using StockStudio.Api.Domain;

namespace StockStudio.Api.Services;

/// <summary>Durable registry of jobs + resolution of on-disk asset paths.</summary>
public interface IJobStore
{
    /// <summary>Root folder where generated assets (SVG/EPS/JPG) are written per job.</summary>
    string StorageRoot { get; }

    /// <summary>Absolute folder for a job's generated assets.</summary>
    string JobDir(string jobId);

    void Add(Job job);
    void Save(Job job);
    Job? Get(string id);
    IReadOnlyList<Job> List(int limit = 50);
    void Delete(string id);

    /// <summary>Records that a dispatched file maps to a job item, so pipeline callbacks can resolve it.</summary>
    void IndexDispatch(string fileName, string jobId, string itemId);

    /// <summary>Resolves a dispatched file name back to its (jobId, itemId), or null if unknown.</summary>
    (string jobId, string itemId)? ResolveDispatch(string fileName);
}

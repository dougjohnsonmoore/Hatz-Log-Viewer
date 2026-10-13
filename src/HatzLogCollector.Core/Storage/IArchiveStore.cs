namespace HatzLogCollector.Storage;

public interface IArchiveStore
{
    Task<bool> ExistsAsync(string container, string path, CancellationToken ct);

    /// <summary>
    /// Writes text. With <paramref name="overwrite"/> false the write is create-only and an existing
    /// object is left untouched (returns false), which keeps the archive append-only.
    /// </summary>
    Task<bool> WriteAsync(string container, string path, string content, bool overwrite, CancellationToken ct);

    Task<string?> ReadAsync(string container, string path, CancellationToken ct);

    IAsyncEnumerable<string> ListAsync(string container, string prefix, CancellationToken ct);

    IAsyncEnumerable<string> ListContainersAsync(string prefix, CancellationToken ct);
}

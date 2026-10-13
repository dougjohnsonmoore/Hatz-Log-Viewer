using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace HatzLogCollector.Storage;

/// <summary>
/// Blob-backed store. Archive writes are create-only (If-None-Match: *), so re-runs and overlapping
/// windows can never replace an archived record, and an immutability policy on the container is safe.
/// </summary>
public sealed class AzureBlobArchiveStore : IArchiveStore
{
    private readonly BlobServiceClient _service;
    private readonly ConcurrentDictionary<string, BlobContainerClient> _containers = new();

    public AzureBlobArchiveStore(BlobServiceClient service) => _service = service;

    public async Task<bool> ExistsAsync(string container, string path, CancellationToken ct)
    {
        var blob = _service.GetBlobContainerClient(container).GetBlobClient(path);
        try
        {
            return (await blob.ExistsAsync(ct).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false; // container does not exist yet
        }
    }

    public async Task<bool> WriteAsync(string container, string path, string content, bool overwrite, CancellationToken ct)
    {
        var containerClient = await EnsureContainerAsync(container, ct).ConfigureAwait(false);
        var blob = containerClient.GetBlobClient(path);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" },
        };
        if (!overwrite) options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };

        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(new UTF8Encoding(false).GetBytes(content)), options, ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (!overwrite && ex.Status is 409 or 412)
        {
            return false; // already archived
        }
    }

    public async Task<string?> ReadAsync(string container, string path, CancellationToken ct)
    {
        try
        {
            var result = await _service.GetBlobContainerClient(container).GetBlobClient(path)
                .DownloadContentAsync(ct).ConfigureAwait(false);
            return result.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<string> ListAsync(
        string container, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = _service.GetBlobContainerClient(container);
        var pages = client.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).AsPages().GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                try
                {
                    if (!await pages.MoveNextAsync().ConfigureAwait(false)) yield break;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    yield break; // container does not exist
                }
                foreach (var item in pages.Current.Values) yield return item.Name;
            }
        }
        finally
        {
            await pages.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<string> ListContainersAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var container in _service.GetBlobContainersAsync(BlobContainerTraits.None, prefix, ct).ConfigureAwait(false))
            yield return container.Name;
    }

    private async Task<BlobContainerClient> EnsureContainerAsync(string name, CancellationToken ct)
    {
        if (_containers.TryGetValue(name, out var existing)) return existing;
        var client = _service.GetBlobContainerClient(name);
        await client.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        _containers[name] = client;
        return client;
    }
}

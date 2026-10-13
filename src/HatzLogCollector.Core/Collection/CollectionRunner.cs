using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HatzLogCollector.Hatz;
using HatzLogCollector.Storage;
using Microsoft.Extensions.Logging;

namespace HatzLogCollector.Collection;

public sealed class CollectionRunner
{
    private readonly CollectorOptions _options;
    private readonly ISecretResolver _secrets;
    private readonly Func<string, IHatzApi> _clientFactory;
    private readonly IArchiveStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public CollectionRunner(
        CollectorOptions options,
        ISecretResolver secrets,
        Func<string, IHatzApi> clientFactory,
        IArchiveStore store,
        ILogger logger,
        TimeProvider? time = null)
    {
        _options = options;
        _secrets = secrets;
        _clientFactory = clientFactory;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<TenantRunResult>> RunAsync(CollectRequest request, CancellationToken ct)
    {
        var results = new List<TenantRunResult>();
        var sources = _options.Sources
            .Where(s => request.SourceName == null || string.Equals(s.Name, request.SourceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sources.Count == 0)
            throw new InvalidOperationException(request.SourceName == null
                ? "No sources configured."
                : $"No source named '{request.SourceName}'.");

        foreach (var source in sources)
        {
            IHatzApi api;
            IReadOnlyList<(string Id, string? Name)> tenants;
            try
            {
                api = _clientFactory(await _secrets.GetAsync(source.ApiKeyRef, ct));
                tenants = await ResolveTenantsAsync(source, api, ct);
            }
            catch (Exception ex) when (ex is HatzApiException or InvalidOperationException)
            {
                _logger.LogError("Source {Source}: {Message}", source.Name, ex.Message);
                results.Add(new TenantRunResult
                {
                    Source = source.Name, TenantId = "(all)", Status = TenantRunStatus.Failed, Reason = ex.Message,
                });
                continue;
            }

            foreach (var (id, name) in tenants)
            {
                if (request.TenantIds is { Count: > 0 } wanted && !wanted.Contains(id, StringComparer.Ordinal)) continue;
                results.Add(await CollectTenantAsync(source.Name, api, id, name, request, ct));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<(string Id, string? Name)>> ResolveTenantsAsync(
        SourceOptions source, IHatzApi api, CancellationToken ct)
    {
        IEnumerable<(string Id, string? Name)> tenants;
        if (source.TenantIds is { Count: > 0 })
        {
            foreach (var id in source.TenantIds) ConfigGuard.RejectPlaceholder(id, $"Collector:Sources[{source.Name}]:TenantIds");
            tenants = source.TenantIds.Select(id => (id, (string?)null));
        }
        else if (source.DiscoverTenants)
            tenants = (await api.ListTenantsAsync(ct)).Select(t => (t.Id, (string?)t.Name));
        else
            throw new InvalidOperationException(
                $"Source '{source.Name}' needs TenantIds or DiscoverTenants=true.");

        var excluded = source.ExcludeTenantIds ?? new List<string>();
        return tenants.Where(t => !excluded.Contains(t.Id, StringComparer.Ordinal)).ToList();
    }

    private async Task<TenantRunResult> CollectTenantAsync(
        string source, IHatzApi api, string tenantId, string? tenantName, CollectRequest request, CancellationToken ct)
    {
        var result = new TenantRunResult { Source = source, TenantId = tenantId, TenantName = tenantName };
        try
        {
            var settings = await api.GetComplianceSettingsAsync(tenantId, ct);
            if (settings is { InvocationsViewEnabled: false })
            {
                result.Status = TenantRunStatus.Skipped;
                result.Reason = "Invocations view is disabled for this tenant (compliance settings).";
                return result;
            }

            // Unknown (settings unreadable) is treated optimistically; a denied content call falls back per item.
            var contentAllowed = settings?.InvocationsViewContentEnabled ?? true;
            if (!contentAllowed)
                _logger.LogWarning("Tenant {Tenant}: content access is disabled; archiving metadata only.", tenantId);

            var now = _time.GetUtcNow();
            var watermark = await ReadWatermarkAsync(tenantId, ct);
            result.WatermarkBefore = watermark;

            DateTimeOffset start;
            if (request.From != null) start = request.From.Value;
            else if (watermark != null) start = watermark.Value - TimeSpan.FromMinutes(_options.OverlapMinutes);
            else start = now - TimeSpan.FromDays(_options.InitialLookbackDays);
            var end = request.To ?? now;

            var step = TimeSpan.FromHours(Math.Max(1, _options.WindowHours));
            for (var windowStart = start; windowStart < end; windowStart += step)
            {
                var windowEnd = windowStart + step < end ? windowStart + step : end;
                var failedInWindow = await CollectWindowAsync(
                    api, tenantId, contentAllowed, windowStart, windowEnd, request.DryRun, result, ct);
                if (failedInWindow > 0) break; // never advance past a gap

                result.WindowsCompleted++;
                if (!request.IsManualRange && !request.DryRun)
                {
                    await WriteWatermarkAsync(tenantId, windowEnd, ct);
                    result.WatermarkAfter = windowEnd;
                }
            }

            result.Status = result.Failed > 0 ? TenantRunStatus.CompletedWithErrors : TenantRunStatus.Completed;
        }
        catch (HatzApiException ex)
        {
            _logger.LogError("Tenant {Tenant}: {Message}", tenantId, ex.Message);
            result.Status = TenantRunStatus.Failed;
            result.Reason = ex.Message;
        }

        return result;
    }

    private async Task<int> CollectWindowAsync(
        IHatzApi api, string tenantId, bool contentAllowed,
        DateTimeOffset windowStart, DateTimeOffset windowEnd, bool dryRun,
        TenantRunResult result, CancellationToken ct)
    {
        var container = ArchivePaths.TenantContainer(tenantId);
        var pending = new List<InvocationListItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? cursor = null;
        do
        {
            var page = await api.ListInvocationsAsync(
                new InvocationQuery(windowStart, windowEnd, tenantId, cursor, _options.PageSize), ct);
            foreach (var item in page.Items)
            {
                if (!seen.Add(item.InvocationId)) continue; // window edges are inclusive on both sides
                result.Listed++;
                if (await _store.ExistsAsync(container, ArchivePaths.InvocationPath(item.Timestamp, item.InvocationId), ct))
                    result.AlreadyArchived++;
                else
                    pending.Add(item);
            }
            cursor = page.HasMore ? page.NextCursor : null;
        } while (cursor != null);

        if (dryRun || pending.Count == 0) return 0;

        var failed = 0;
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelDetailRequests), CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    var (record, status) = await FetchRecordAsync(api, item, contentAllowed, token);
                    var envelope = BuildEnvelope(record, status);
                    var written = await _store.WriteAsync(
                        container, ArchivePaths.InvocationPath(item.Timestamp, item.InvocationId), envelope, overwrite: false, token);

                    lock (result)
                    {
                        if (written) result.Archived++; else result.AlreadyArchived++;
                        if (written && status != ContentStatus.Included) result.MetadataOnly++;
                    }
                }
                catch (Exception ex) when (ex is HatzApiException or HttpRequestException or IOException)
                {
                    // Ids only - never log content.
                    _logger.LogWarning("Invocation {Id} failed: {Message}", item.InvocationId, ex.Message);
                    Interlocked.Increment(ref failed);
                    lock (result) result.Failed++;
                }
            });
        return failed;
    }

    private static async Task<(JsonElement Record, string Status)> FetchRecordAsync(
        IHatzApi api, InvocationListItem item, bool contentAllowed, CancellationToken ct)
    {
        if (!contentAllowed) return (item.Raw, ContentStatus.DisabledByTenantSetting);
        try
        {
            return (await api.GetInvocationAsync(item.InvocationId, includeContent: true, ct), ContentStatus.Included);
        }
        catch (HatzApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return (item.Raw, ContentStatus.Denied);
        }
    }

    private string BuildEnvelope(JsonElement record, string contentStatus)
    {
        var envelope = new JsonObject
        {
            ["schema"] = 1,
            ["collectedAtUtc"] = _time.GetUtcNow().ToString("o"),
            ["contentStatus"] = contentStatus,
            ["invocation"] = JsonNode.Parse(record.GetRawText()),
        };
        return envelope.ToJsonString();
    }

    private async Task<DateTimeOffset?> ReadWatermarkAsync(string tenantId, CancellationToken ct)
    {
        var text = await _store.ReadAsync(ArchivePaths.StateContainer, ArchivePaths.WatermarkPath(tenantId), ct);
        if (text == null) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("lastWindowEndUtc", out var v) && v.GetString() is { } s
            ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private Task WriteWatermarkAsync(string tenantId, DateTimeOffset value, CancellationToken ct) =>
        _store.WriteAsync(
            ArchivePaths.StateContainer,
            ArchivePaths.WatermarkPath(tenantId),
            new JsonObject
            {
                ["lastWindowEndUtc"] = value.ToUniversalTime().ToString("o"),
                ["updatedAtUtc"] = _time.GetUtcNow().ToString("o"),
            }.ToJsonString(),
            overwrite: true,
            ct);
}

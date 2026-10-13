using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HatzLogCollector.Hatz;

public interface IHatzApi
{
    Task<IReadOnlyList<TenantInfo>> ListTenantsAsync(CancellationToken ct);
    Task<ComplianceSettings?> GetComplianceSettingsAsync(string tenantId, CancellationToken ct);
    Task<InvocationPage> ListInvocationsAsync(InvocationQuery query, CancellationToken ct);

    /// <summary>Returns the raw invocation JSON. With content, includes the message history and file references.</summary>
    Task<JsonElement> GetInvocationAsync(string invocationId, bool includeContent, CancellationToken ct);
}

public sealed class HatzApiClient : IHatzApi
{
    private readonly HttpClient _http;

    public HatzApiClient(HttpClient http) => _http = http;

    public static HatzApiClient Create(
        string baseUrl,
        string apiKey,
        ILogger? logger = null,
        HttpMessageHandler? innerHandler = null)
    {
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        var inner = innerHandler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var http = new HttpClient(new RetryHandler(inner, logger: logger))
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(2),
        };
        http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return new HatzApiClient(http);
    }

    public async Task<IReadOnlyList<TenantInfo>> ListTenantsAsync(CancellationToken ct)
    {
        const int limit = 1000;
        var tenants = new List<TenantInfo>();
        for (var offset = 0; ; offset += limit)
        {
            var root = await GetJsonAsync($"admin/tenants?limit={limit}&offset={offset}", ct);
            var count = 0;
            foreach (var t in root.EnumerateArray())
            {
                count++;
                tenants.Add(new TenantInfo(
                    t.GetProperty("id").GetString()!,
                    Str(t, "name") ?? "",
                    Str(t, "status")));
            }
            if (count < limit) return tenants;
        }
    }

    public async Task<ComplianceSettings?> GetComplianceSettingsAsync(string tenantId, CancellationToken ct)
    {
        try
        {
            var s = await GetJsonAsync($"compliance/settings/{Uri.EscapeDataString(tenantId)}", ct);
            return new ComplianceSettings(
                Str(s, "tenant_id") ?? tenantId,
                Bool(s, "audit_events_view_enabled"),
                Bool(s, "invocations_view_enabled"),
                Bool(s, "invocations_view_content_enabled"),
                Str(s, "file_egress_policy"));
        }
        catch (HatzApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // Reading settings may need more privilege than reading invocations; caller decides how to proceed.
            return null;
        }
    }

    public async Task<InvocationPage> ListInvocationsAsync(InvocationQuery q, CancellationToken ct)
    {
        var parts = new List<string> { $"limit={q.Limit}" };
        if (q.Start is { } s) parts.Add($"start={Uri.EscapeDataString(FormatTimestamp(s))}");
        if (q.End is { } e) parts.Add($"end={Uri.EscapeDataString(FormatTimestamp(e))}");
        if (!string.IsNullOrEmpty(q.TenantId)) parts.Add($"tenant_id={Uri.EscapeDataString(q.TenantId)}");
        if (!string.IsNullOrEmpty(q.Cursor)) parts.Add($"cursor={Uri.EscapeDataString(q.Cursor)}");

        var root = await GetJsonAsync($"compliance/invocations?{string.Join('&', parts)}", ct);
        var items = new List<InvocationListItem>();
        foreach (var row in root.GetProperty("data").EnumerateArray())
        {
            var tenant = row.TryGetProperty("tenant", out var t) && t.ValueKind == JsonValueKind.Object ? t : (JsonElement?)null;
            items.Add(new InvocationListItem(
                row.GetProperty("invocation_id").GetString()!,
                DateTimeOffset.Parse(row.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture),
                Str(row, "session_id"),
                tenant is { } tn ? Str(tn, "id") : null,
                tenant is { } tn2 ? Str(tn2, "name") : null,
                row.Clone()));
        }

        var hasMore = false;
        string? next = null;
        if (root.TryGetProperty("pagination", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            hasMore = Bool(p, "has_more");
            next = Str(p, "next_cursor");
        }
        return new InvocationPage(items, hasMore && next != null, next);
    }

    public Task<JsonElement> GetInvocationAsync(string invocationId, bool includeContent, CancellationToken ct) =>
        GetJsonAsync(
            $"compliance/invocations/{Uri.EscapeDataString(invocationId)}" + (includeContent ? "?include_content=true" : ""),
            ct);

    /// <summary>HTTP status of a GET, without throwing. For diagnosing keys and permissions.</summary>
    public async Task<HttpStatusCode> ProbeAsync(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return response.StatusCode;
    }

    /// <summary>Whole-second UTC, as shown in the Hatz docs examples.</summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<JsonElement> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var endpoint = relativeUrl.Split('?')[0];
            throw new HatzApiException(response.StatusCode, endpoint, body.Length > 300 ? body[..300] : body);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

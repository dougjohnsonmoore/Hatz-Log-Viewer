using System.Net;
using System.Text.Json;

namespace HatzLogCollector.Hatz;

public sealed record TenantInfo(string Id, string Name, string? Status);

public sealed record ComplianceSettings(
    string TenantId,
    bool AuditEventsViewEnabled,
    bool InvocationsViewEnabled,
    bool InvocationsViewContentEnabled,
    string? FileEgressPolicy);

public sealed record InvocationQuery(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? TenantId,
    string? Cursor,
    int Limit = 200);

/// <summary>Summary row from the list endpoint. <see cref="Raw"/> keeps the full original JSON.</summary>
public sealed record InvocationListItem(
    string InvocationId,
    DateTimeOffset Timestamp,
    string? SessionId,
    string? TenantId,
    string? TenantName,
    JsonElement Raw);

public sealed record InvocationPage(IReadOnlyList<InvocationListItem> Items, bool HasMore, string? NextCursor);

public sealed class HatzApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string Endpoint { get; }

    public HatzApiException(HttpStatusCode statusCode, string endpoint, string? bodySnippet)
        : base($"Hatz API {endpoint} returned {(int)statusCode} {statusCode}. {bodySnippet}".TrimEnd())
    {
        StatusCode = statusCode;
        Endpoint = endpoint;
    }
}

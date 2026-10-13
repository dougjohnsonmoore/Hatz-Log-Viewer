using System.Text.Json.Serialization;

namespace HatzLogCollector.Pii;

public sealed record DetectedEntity(string Text, string Category, string? SubCategory, double Confidence, int Offset, int Length);

public sealed record PiiDocumentResult(bool Failed, string? Error, IReadOnlyList<DetectedEntity> Entities)
{
    public static PiiDocumentResult Ok(IReadOnlyList<DetectedEntity> entities) => new(false, null, entities);
    public static PiiDocumentResult Fail(string error) => new(true, error, Array.Empty<DetectedEntity>());
}

/// <summary>Swappable detector (Azure AI Language today; Purview or Presidio later).</summary>
public interface IPiiDetector
{
    /// <summary>Documents per request the service accepts.</summary>
    int MaxBatchSize { get; }

    /// <summary>Largest document, in UTF-16 code units, that is safe to send.</summary>
    int MaxDocumentLength { get; }

    /// <summary>Returns one result per input document, in order.</summary>
    Task<IReadOnlyList<PiiDocumentResult>> DetectAsync(IReadOnlyList<string> documents, CancellationToken ct);
}

public enum Severity
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// One PII hit. Only a masked value is stored; the pointer fields locate the original text
/// in the raw archive, so the report is not a second copy of the PII.
/// </summary>
public sealed record PiiFinding
{
    public required string TenantId { get; init; }
    public string? TenantName { get; init; }
    public required string SessionId { get; init; }
    public required string InvocationId { get; init; }
    public required string BlobPath { get; init; }
    public string? Source { get; init; }
    public string? UserId { get; init; }
    public string? UserEmail { get; init; }

    /// <summary>user, assistant, tool_call, tool_result, step, or metadata:*</summary>
    public required string Origin { get; init; }
    public string? MessageId { get; init; }
    public required string Category { get; init; }
    public string? SubCategory { get; init; }
    public double Confidence { get; init; }
    public required string Severity { get; init; }
    public required string MaskedText { get; init; }

    /// <summary>Used only to count distinct values in memory. Never serialized.</summary>
    [JsonIgnore]
    public string ValueKey { get; init; } = "";
}

public sealed record SessionSummary(
    string TenantId,
    string SessionId,
    string? Source,
    string? UserEmail,
    DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp,
    int InvocationCount,
    int MessageCount,
    int Characters,
    bool Truncated,
    int FindingCount,
    string? MaxSeverity);

public sealed class ScanCounters
{
    public int InvocationsRead { get; set; }
    public int InvocationsWithoutContent { get; set; }
    public int Sessions { get; set; }
    public int TruncatedSessions { get; set; }
    public int Chunks { get; set; }
    public int ChunkErrors { get; set; }
    public int UnreadableBlobs { get; set; }
}

public sealed class ScanOutput
{
    public required string Container { get; init; }
    public string? TenantId { get; set; }
    public string? TenantName { get; set; }
    public List<PiiFinding> Findings { get; } = new();
    public List<SessionSummary> Sessions { get; } = new();
    public ScanCounters Counters { get; } = new();
}

/// <summary>The detector failed repeatedly with no successes: a configuration, network or permission problem.</summary>
public sealed class PiiDetectorUnavailableException : Exception
{
    public PiiDetectorUnavailableException(string message) : base(message) { }
}

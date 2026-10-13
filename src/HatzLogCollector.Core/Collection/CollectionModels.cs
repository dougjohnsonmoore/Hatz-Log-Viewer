namespace HatzLogCollector.Collection;

public sealed record CollectRequest(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyCollection<string>? TenantIds = null,
    string? SourceName = null,
    bool DryRun = false)
{
    /// <summary>An explicit range is a manual backfill/audit: it never moves the watermark.</summary>
    public bool IsManualRange => From != null || To != null;
}

public enum TenantRunStatus
{
    Completed,
    CompletedWithErrors,
    Skipped,
    Failed,
}

public sealed class TenantRunResult
{
    public required string Source { get; init; }
    public required string TenantId { get; init; }
    public string? TenantName { get; set; }
    public TenantRunStatus Status { get; set; } = TenantRunStatus.Completed;
    public string? Reason { get; set; }

    public int Listed { get; set; }
    public int Archived { get; set; }
    public int AlreadyArchived { get; set; }

    /// <summary>Archived with metadata only because the tenant setting or API denied content.</summary>
    public int MetadataOnly { get; set; }

    public int Failed { get; set; }
    public int WindowsCompleted { get; set; }
    public DateTimeOffset? WatermarkBefore { get; set; }
    public DateTimeOffset? WatermarkAfter { get; set; }
}

/// <summary>Content availability recorded in each archived record.</summary>
public static class ContentStatus
{
    public const string Included = "included";
    public const string DisabledByTenantSetting = "disabled_by_tenant_setting";
    public const string Denied = "denied";
}

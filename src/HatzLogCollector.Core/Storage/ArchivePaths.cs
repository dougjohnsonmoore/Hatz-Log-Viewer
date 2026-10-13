using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HatzLogCollector.Storage;

/// <summary>
/// Layout:
///   hatz-{tenant}/invocations/yyyy/MM/dd/{invocation_id}.json   raw archive (apply an immutability policy here)
///   hatz-state/{tenant}/watermark.json                          mutable collector state
///   hatz-reports/{tenant}/{runId}/...                           PII scan output
/// State and reports are separate containers so they stay writable when the archive is WORM-locked.
/// </summary>
public static class ArchivePaths
{
    public const string StateContainer = "hatz-state";
    public const string ReportsContainer = "hatz-reports";
    public const string InvocationsPrefix = "invocations";

    public static string TenantSlug(string tenantId)
    {
        var slug = Regex.Replace(tenantId.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "tenant";
        if (slug.Length <= 50) return slug;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)))[..8].ToLowerInvariant();
        return $"{slug[..41]}-{hash}";
    }

    public static string TenantContainer(string tenantId) => $"hatz-{TenantSlug(tenantId)}";

    public static string DayPrefix(DateTimeOffset day)
    {
        var d = day.ToUniversalTime();
        return $"{InvocationsPrefix}/{d:yyyy}/{d:MM}/{d:dd}/";
    }

    public static string InvocationPath(DateTimeOffset timestamp, string invocationId) =>
        $"{DayPrefix(timestamp)}{invocationId}.json";

    public static string WatermarkPath(string tenantId) => $"{TenantSlug(tenantId)}/watermark.json";

    public static string ReportPrefix(string tenantId, string runId) => $"{TenantSlug(tenantId)}/{runId}/";
}

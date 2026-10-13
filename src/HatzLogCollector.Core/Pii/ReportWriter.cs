using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HatzLogCollector.Storage;

namespace HatzLogCollector.Pii;

/// <summary>
/// Writes findings.jsonl, sessions.csv, summary.csv and run.json to
/// hatz-reports/{tenant}/{runId}/. Values are masked; pointers lead back to the raw archive.
/// </summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static string NewRunId(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static async Task<string> WriteAsync(
        IArchiveStore store, ScanOutput output, string runId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var prefix = ArchivePaths.ReportPrefix(output.TenantId ?? output.Container, runId);
        const string c = ArchivePaths.ReportsContainer;

        await store.WriteAsync(c, prefix + "findings.jsonl", FindingsJsonl(output), overwrite: true, ct);
        await store.WriteAsync(c, prefix + "sessions.csv", SessionsCsv(output), overwrite: true, ct);
        await store.WriteAsync(c, prefix + "summary.csv", SummaryCsv(output), overwrite: true, ct);
        await store.WriteAsync(c, prefix + "run.json", RunJson(output, runId, from, to), overwrite: true, ct);
        return prefix;
    }

    public static string FindingsJsonl(ScanOutput output)
    {
        var sb = new StringBuilder();
        foreach (var f in output.Findings) sb.Append(JsonSerializer.Serialize(f, JsonOptions)).Append('\n');
        return sb.ToString();
    }

    public static string SessionsCsv(ScanOutput output)
    {
        var sb = new StringBuilder("tenantId,sessionId,source,userEmail,firstTimestamp,lastTimestamp,invocations,messages,characters,truncated,findings,maxSeverity\n");
        foreach (var s in output.Sessions)
        {
            sb.AppendJoin(',',
                    Csv(s.TenantId), Csv(s.SessionId), Csv(s.Source), Csv(s.UserEmail),
                    Csv(s.FirstTimestamp.ToString("o")), Csv(s.LastTimestamp.ToString("o")),
                    s.InvocationCount, s.MessageCount, s.Characters, s.Truncated ? "true" : "false",
                    s.FindingCount, Csv(s.MaxSeverity))
                .Append('\n');
        }
        return sb.ToString();
    }

    public static string SummaryCsv(ScanOutput output)
    {
        var sb = new StringBuilder("tenantId,userEmail,category,severity,occurrences,distinctValues,sessions\n");
        var groups = output.Findings
            .GroupBy(f => (f.UserEmail ?? "(unknown)", f.Category, f.Severity))
            .OrderByDescending(g => Enum.Parse<Severity>(g.Key.Severity))
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.Key.Item1, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            sb.AppendJoin(',',
                    Csv(output.TenantId), Csv(g.Key.Item1), Csv(g.Key.Category), Csv(g.Key.Severity),
                    g.Count(), g.Select(f => f.ValueKey).Distinct().Count(), g.Select(f => f.SessionId).Distinct().Count())
                .Append('\n');
        }
        return sb.ToString();
    }

    public static string RunJson(ScanOutput output, string runId, DateTimeOffset from, DateTimeOffset to) =>
        new JsonObject
        {
            ["runId"] = runId,
            ["tenantId"] = output.TenantId,
            ["tenantName"] = output.TenantName,
            ["from"] = from.ToString("o"),
            ["to"] = to.ToString("o"),
            ["findings"] = output.Findings.Count,
            ["counters"] = JsonSerializer.SerializeToNode(output.Counters),
            ["note"] = "Values are masked. BlobPath/MessageId on each finding locate the original text in the raw archive.",
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>RFC 4180 quoting plus a guard so spreadsheet apps do not evaluate cells as formulas.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if ("=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}

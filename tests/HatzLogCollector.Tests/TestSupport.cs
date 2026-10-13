using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HatzLogCollector.Hatz;
using HatzLogCollector.Pii;
using HatzLogCollector.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HatzLogCollector.Tests;

/// <summary>Synthetic data shaped like real Hatz responses. No real names, emails or tenant ids.</summary>
public static class Fixtures
{
    public const string Tenant = "tenant_TEST0001";
    public const string TenantName = "Example Co";
    public const string UserEmail = "alex.example@example.test";

    public static JsonObject UserMessage(string id, string text, string createdAt) => new()
    {
        ["id"] = id,
        ["role"] = "user",
        ["created_at"] = createdAt,
        ["parent_message_id"] = "00000000-0000-4000-8000-000000000000",
        ["content"] = new JsonArray(new JsonObject { ["content"] = text, ["signature"] = null, ["type"] = "content" }),
        ["files"] = new JsonArray(),
        ["sources"] = new JsonArray(),
    };

    public static JsonObject AssistantMessage(string id, string text, string createdAt, string? code = null, string? toolResult = null)
    {
        var blocks = new JsonArray(new JsonObject { ["content"] = "Working on it.", ["type"] = "content" });
        if (code != null)
        {
            blocks.Add(new JsonObject
            {
                ["args"] = new JsonObject { ["code"] = code },
                ["id"] = "tooluse_1",
                ["name"] = "run_python_script",
                ["type"] = "tool_call",
            });
        }
        if (toolResult != null)
        {
            blocks.Add(new JsonObject
            {
                ["content"] = toolResult,
                ["name"] = "run_python_script",
                ["status"] = "success",
                ["tool_call_id"] = "tooluse_1",
                ["type"] = "tool_result",
            });
        }
        blocks.Add(new JsonObject { ["content"] = text, ["type"] = "content" });
        return new JsonObject
        {
            ["id"] = id,
            ["role"] = "assistant",
            ["created_at"] = createdAt,
            ["model_name"] = "anthropic.claude-haiku-4-5",
            ["content"] = blocks,
            ["files"] = new JsonArray(),
        };
    }

    public static JsonObject Invocation(
        string invocationId, string sessionId, DateTimeOffset timestamp,
        IEnumerable<JsonObject>? messages = null, string tenantId = Tenant, string? sessionName = "Test session",
        string[]? fileNames = null)
    {
        var files = new JsonArray();
        foreach (var name in fileNames ?? Array.Empty<string>())
            files.Add(new JsonObject { ["file_uuid"] = Guid.NewGuid().ToString(), ["original_filename"] = name, ["status"] = "ready" });

        return new JsonObject
        {
            ["invocation_id"] = invocationId,
            ["session_id"] = sessionId,
            ["session_name"] = sessionName,
            ["timestamp"] = timestamp.ToString("o", CultureInfo.InvariantCulture),
            ["source"] = "chat",
            ["tenant"] = new JsonObject { ["id"] = tenantId, ["name"] = TenantName },
            ["user"] = new JsonObject { ["id"] = "user_TEST0001", ["email"] = UserEmail, ["name"] = "Alex Example" },
            ["agent"] = null,
            ["api_key"] = null,
            ["models"] = new JsonObject
            {
                ["requested"] = "anthropic.claude-haiku-4-5",
                ["actual"] = new JsonArray("anthropic.claude-haiku-4-5"),
                ["providers"] = new JsonArray("Anthropic"),
            },
            ["files"] = files,
            ["content"] = new JsonArray((messages ?? Array.Empty<JsonObject>()).Select(m => (JsonNode)m.DeepClone()).ToArray()),
        };
    }

    public static string Envelope(JsonObject invocation, string contentStatus = "included") =>
        new JsonObject { ["schema"] = 1, ["contentStatus"] = contentStatus, ["invocation"] = invocation.DeepClone() }.ToJsonString();
}

public sealed class InMemoryStore : IArchiveStore
{
    private readonly ConcurrentDictionary<string, string> _items = new();

    private static string Key(string container, string path) => container + "\u0000" + path;

    public IEnumerable<(string Container, string Path)> All =>
        _items.Keys.Select(k => { var p = k.Split('\u0000'); return (p[0], p[1]); }).OrderBy(x => x.Item1).ThenBy(x => x.Item2);

    public int Count(string container, string prefix) => All.Count(x => x.Container == container && x.Path.StartsWith(prefix, StringComparison.Ordinal));

    public Task<bool> ExistsAsync(string container, string path, CancellationToken ct) =>
        Task.FromResult(_items.ContainsKey(Key(container, path)));

    public Task<bool> WriteAsync(string container, string path, string content, bool overwrite, CancellationToken ct)
    {
        if (overwrite)
        {
            _items[Key(container, path)] = content;
            return Task.FromResult(true);
        }
        return Task.FromResult(_items.TryAdd(Key(container, path), content));
    }

    public Task<string?> ReadAsync(string container, string path, CancellationToken ct) =>
        Task.FromResult(_items.TryGetValue(Key(container, path), out var v) ? v : null);

    public async IAsyncEnumerable<string> ListAsync(string container, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (c, p) in All)
            if (c == container && p.StartsWith(prefix, StringComparison.Ordinal)) yield return p;
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListContainersAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var c in All.Select(x => x.Container).Distinct())
            if (c.StartsWith(prefix, StringComparison.Ordinal)) yield return c;
        await Task.CompletedTask;
    }
}

public sealed class FixedTime : TimeProvider
{
    private DateTimeOffset _now;
    public FixedTime(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset now) => _now = now;
}

public sealed class FakeHatzApi : IHatzApi
{
    public List<TenantInfo> Tenants { get; } = new();
    public Dictionary<string, ComplianceSettings?> Settings { get; } = new();
    public List<JsonObject> Invocations { get; } = new();
    public HashSet<string> FailDetailFor { get; } = new();
    public HashSet<string> DenyContentFor { get; } = new();
    public int DetailCalls;
    public int ListCalls;
    public int PageSize { get; set; } = 2;

    public Task<IReadOnlyList<TenantInfo>> ListTenantsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TenantInfo>>(Tenants);

    public Task<ComplianceSettings?> GetComplianceSettingsAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult(Settings.TryGetValue(tenantId, out var s) ? s : new ComplianceSettings(tenantId, true, true, true, "direct_and_microsoft_365"));

    public Task<InvocationPage> ListInvocationsAsync(InvocationQuery q, CancellationToken ct)
    {
        Interlocked.Increment(ref ListCalls);
        var matching = Invocations
            .Where(i => q.TenantId == null || i["tenant"]!["id"]!.GetValue<string>() == q.TenantId)
            .Where(i => { var ts = Ts(i); return (q.Start == null || ts >= Floor(q.Start.Value)) && (q.End == null || ts <= q.End.Value); })
            .OrderByDescending(Ts)
            .ToList();
        var skip = q.Cursor == null ? 0 : int.Parse(q.Cursor);
        var page = matching.Skip(skip).Take(PageSize).ToList();
        var items = page.Select(i =>
        {
            var raw = (JsonObject)i.DeepClone();
            raw["content"] = null;
            raw["files"] = null;
            var element = JsonSerializer.SerializeToElement(raw);
            return new InvocationListItem(
                i["invocation_id"]!.GetValue<string>(), Ts(i), i["session_id"]!.GetValue<string>(),
                i["tenant"]!["id"]!.GetValue<string>(), i["tenant"]!["name"]!.GetValue<string>(), element);
        }).ToList();
        var hasMore = skip + page.Count < matching.Count;
        return Task.FromResult(new InvocationPage(items, hasMore, hasMore ? (skip + page.Count).ToString() : null));
    }

    public Task<JsonElement> GetInvocationAsync(string invocationId, bool includeContent, CancellationToken ct)
    {
        Interlocked.Increment(ref DetailCalls);
        if (FailDetailFor.Contains(invocationId))
            throw new HatzApiException(System.Net.HttpStatusCode.InternalServerError, "compliance/invocations/{id}", "boom");
        if (includeContent && DenyContentFor.Contains(invocationId))
            throw new HatzApiException(System.Net.HttpStatusCode.Forbidden, "compliance/invocations/{id}", "no content access");

        var source = Invocations.Single(i => i["invocation_id"]!.GetValue<string>() == invocationId);
        var copy = (JsonObject)source.DeepClone();
        if (!includeContent)
        {
            copy["content"] = null;
            copy["files"] = null;
        }
        return Task.FromResult(JsonSerializer.SerializeToElement(copy));
    }

    // The real API takes whole-second timestamps; mimic that so sub-second values are not lost at window starts.
    private static DateTimeOffset Floor(DateTimeOffset v) => new(v.Ticks - v.Ticks % TimeSpan.TicksPerSecond, v.Offset);

    private static DateTimeOffset Ts(JsonObject i) =>
        DateTimeOffset.Parse(i["timestamp"]!.GetValue<string>(), CultureInfo.InvariantCulture);
}

/// <summary>Regex stand-in for Azure AI Language so scan logic is tested offline.</summary>
public sealed class FakeDetector : IPiiDetector
{
    private static readonly (Regex Pattern, string Category, double Confidence)[] Rules =
    {
        (new Regex(@"\b\d{3}-\d{2}-\d{4}\b"), "USSocialSecurityNumber", 0.95),
        (new Regex(@"[\w.+-]+@[\w-]+\.[\w.]+"), "Email", 0.97),
        (new Regex(@"\bJordan Rivera\b"), "Person", 0.9),
        (new Regex(@"\blast Friday\b"), "DateTime", 0.9),
        (new Regex(@"\bMaybe Person\b"), "Person", 0.4),
    };

    public int MaxBatchSize { get; init; } = 5;
    public int MaxDocumentLength { get; init; } = 5000;
    public int Calls;
    public ConcurrentBag<string> Documents { get; } = new();
    public bool FailEverything { get; set; }

    public Task<IReadOnlyList<PiiDocumentResult>> DetectAsync(IReadOnlyList<string> documents, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (documents.Count > MaxBatchSize) throw new InvalidOperationException("batch too large");
        var results = new List<PiiDocumentResult>();
        foreach (var doc in documents)
        {
            if (doc.Length > MaxDocumentLength) throw new InvalidOperationException("document too long");
            Documents.Add(doc);
            if (FailEverything) { results.Add(PiiDocumentResult.Fail("simulated")); continue; }
            var entities = Rules
                .SelectMany(r => r.Pattern.Matches(doc).Select(m => new DetectedEntity(m.Value, r.Category, null, r.Confidence, m.Index, m.Length)))
                .ToList();
            results.Add(PiiDocumentResult.Ok(entities));
        }
        return Task.FromResult<IReadOnlyList<PiiDocumentResult>>(results);
    }
}

public static class TestLog
{
    public static Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;
}

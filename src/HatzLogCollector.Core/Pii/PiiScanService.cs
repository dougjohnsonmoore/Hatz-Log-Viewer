using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using HatzLogCollector.Storage;
using Microsoft.Extensions.Logging;

namespace HatzLogCollector.Pii;

/// <summary>
/// Scans archived invocations for PII. Hatz returns the whole chat history on each invocation, so
/// records are grouped by session and messages are de-duplicated by message id before scanning:
/// a long conversation is scanned once, not once per turn.
/// </summary>
public sealed class PiiScanService
{
    private readonly IArchiveStore _store;
    private readonly IPiiDetector _detector;
    private readonly PiiOptions _options;
    private readonly SeverityClassifier _severity;
    private readonly ILogger _logger;

    public PiiScanService(IArchiveStore store, IPiiDetector detector, PiiOptions options, ILogger logger)
    {
        _store = store;
        _detector = detector;
        _options = options;
        _severity = new SeverityClassifier(options.SeverityOverrides);
        _logger = logger;
    }

    public async Task<ScanOutput> ScanContainerAsync(
        string container, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var output = new ScanOutput { Container = container };
        var sessions = new Dictionary<string, SessionAccumulator>(StringComparer.Ordinal);
        await LoadSessionsAsync(container, from, to, output, sessions, ct);
        output.TenantId ??= container;

        // Build every chunk for the tenant, then batch across sessions to keep request counts low.
        var jobs = new List<ChunkJob>();
        foreach (var session in sessions.Values)
        {
            var plan = session.BuildSegments(_options.MaxCharsPerSession, _options.IncludeToolOutput);
            session.Truncated = plan.Truncated;
            session.Characters = plan.Characters;
            if (plan.Truncated) output.Counters.TruncatedSessions++;
            foreach (var chunk in TextChunker.Pack(plan.Segments, _detector.MaxDocumentLength))
                jobs.Add(new ChunkJob(session, plan.Info, chunk));
        }
        output.Counters.Sessions = sessions.Count;
        output.Counters.Chunks = jobs.Count;

        var findings = new ConcurrentBag<PiiFinding>();
        var errors = 0;
        var batches = jobs.Chunk(_detector.MaxBatchSize).ToList();
        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelRequests), CancellationToken = ct },
            async (batch, token) =>
            {
                var results = await _detector.DetectAsync(batch.Select(j => j.Chunk.Text).ToList(), token);
                for (var i = 0; i < batch.Length; i++)
                {
                    if (i >= results.Count || results[i].Failed)
                    {
                        Interlocked.Increment(ref errors);
                        _logger.LogWarning("PII detection failed for a chunk: {Error}", i < results.Count ? results[i].Error : "no result");
                        continue;
                    }
                    foreach (var finding in ToFindings(batch[i], results[i], output.TenantId!, output.TenantName))
                        findings.Add(finding);
                }
            });
        output.Counters.ChunkErrors = errors;

        output.Findings.AddRange(findings
            .OrderBy(f => f.SessionId, StringComparer.Ordinal)
            .ThenBy(f => f.BlobPath, StringComparer.Ordinal)
            .ThenBy(f => f.MessageId, StringComparer.Ordinal)
            .ThenByDescending(f => f.Severity, StringComparer.Ordinal));

        var bySession = output.Findings.GroupBy(f => f.SessionId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var s in sessions.Values.OrderBy(s => s.FirstTimestamp))
        {
            bySession.TryGetValue(s.SessionId, out var list);
            output.Sessions.Add(new SessionSummary(
                output.TenantId!, s.SessionId, s.Source, s.UserEmail, s.FirstTimestamp, s.LastTimestamp,
                s.InvocationIds.Count, s.Messages.Count, s.Characters, s.Truncated,
                list?.Count ?? 0,
                list == null ? null : list.Max(f => Enum.Parse<Severity>(f.Severity)).ToString()));
        }

        return output;
    }

    private async Task LoadSessionsAsync(
        string container, DateTimeOffset from, DateTimeOffset to, ScanOutput output,
        Dictionary<string, SessionAccumulator> sessions, CancellationToken ct)
    {
        var paths = new List<string>();
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            await foreach (var path in _store.ListAsync(container, ArchivePaths.DayPrefix(new DateTimeOffset(day, TimeSpan.Zero)), ct))
                paths.Add(path);
        }

        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (path, token) =>
            {
                var text = await _store.ReadAsync(container, path, token);
                if (text == null) return;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    lock (sessions) Accumulate(doc.RootElement, path, from, to, output, sessions);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    lock (sessions) output.Counters.UnreadableBlobs++;
                    _logger.LogWarning("Unreadable archive record {Path}: {Error}", path, ex.GetType().Name);
                }
            });
    }

    private void Accumulate(
        JsonElement envelope, string path, DateTimeOffset from, DateTimeOffset to,
        ScanOutput output, Dictionary<string, SessionAccumulator> sessions)
    {
        var inv = envelope.GetProperty("invocation");
        var timestamp = DateTimeOffset.Parse(inv.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture);
        if (timestamp < from || timestamp > to) return;
        output.Counters.InvocationsRead++;

        JsonElement content = default;
        var hasContent = Str(envelope, "contentStatus") == "included"
                         && inv.TryGetProperty("content", out content)
                         && content.ValueKind == JsonValueKind.Array;
        if (!hasContent)
        {
            output.Counters.InvocationsWithoutContent++;
            return;
        }

        var invocationId = inv.GetProperty("invocation_id").GetString()!;
        var sessionId = Str(inv, "session_id") ?? invocationId;
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            var tenant = Obj(inv, "tenant");
            var user = Obj(inv, "user");
            output.TenantId ??= tenant is { } t ? Str(t, "id") : null;
            output.TenantName ??= tenant is { } t2 ? Str(t2, "name") : null;
            session = new SessionAccumulator(sessionId)
            {
                TenantId = output.TenantId,
                UserId = user is { } u ? Str(u, "id") : null,
                UserEmail = user is { } u2 ? Str(u2, "email") : null,
                Source = Str(inv, "source"),
                FirstTimestamp = timestamp,
                LastTimestamp = timestamp,
            };
            sessions[sessionId] = session;
        }

        session.InvocationIds.Add(invocationId);
        if (timestamp < session.FirstTimestamp) session.FirstTimestamp = timestamp;
        if (timestamp > session.LastTimestamp) session.LastTimestamp = timestamp;

        if (Str(inv, "session_name") is { Length: > 0 } name) session.MetaTexts.TryAdd("metadata:session_name", name);
        if (inv.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
                if (Str(file, "original_filename") is { Length: > 0 } fileName)
                    session.MetaTexts.TryAdd($"metadata:file_name:{fileName}", fileName);
        }

        var index = 0;
        foreach (var item in content.EnumerateArray())
        {
            var id = ContentTextExtractor.MessageId(item, index++);
            if (session.Messages.ContainsKey(id)) continue; // already seen via an earlier invocation
            var parts = ContentTextExtractor.Extract(item, _options.IncludeToolOutput).ToList();
            session.Messages[id] = new MessageAccumulator(
                id, Str(item, "created_at") ?? "", invocationId, path, parts);
        }
    }

    private IEnumerable<PiiFinding> ToFindings(ChunkJob job, PiiDocumentResult result, string tenantId, string? tenantName)
    {
        var ignored = new HashSet<string>(_options.EffectiveIgnoredCategories, StringComparer.OrdinalIgnoreCase);
        foreach (var entity in result.Entities)
        {
            if (entity.Confidence < _options.MinConfidence || ignored.Contains(entity.Category)) continue;

            var info = job.Info[job.Chunk.Spans[0].SegmentId];
            var position = LocateEntity(job.Chunk.Text, entity);
            if (position >= 0)
            {
                foreach (var span in job.Chunk.Spans)
                {
                    if (position >= span.ChunkStart && position < span.ChunkStart + span.Length)
                    {
                        info = job.Info[span.SegmentId];
                        break;
                    }
                }
            }

            yield return new PiiFinding
            {
                TenantId = tenantId,
                TenantName = tenantName,
                SessionId = job.Session.SessionId,
                InvocationId = info.InvocationId,
                BlobPath = info.BlobPath,
                Source = job.Session.Source,
                UserId = job.Session.UserId,
                UserEmail = job.Session.UserEmail,
                Origin = info.Origin,
                MessageId = info.MessageId,
                Category = entity.Category,
                SubCategory = entity.SubCategory,
                Confidence = Math.Round(entity.Confidence, 3),
                Severity = _severity.Classify(entity.Category).ToString(),
                MaskedText = Mask(entity.Text),
                ValueKey = entity.Category + "|" + entity.Text.Trim().ToLowerInvariant(),
            };
        }
    }

    /// <summary>Trusts the service offset only when it points at the reported text.</summary>
    private static int LocateEntity(string chunk, DetectedEntity e)
    {
        if (e.Offset >= 0 && e.Length == e.Text.Length && e.Offset + e.Length <= chunk.Length
            && string.CompareOrdinal(chunk, e.Offset, e.Text, 0, e.Length) == 0)
            return e.Offset;
        return chunk.IndexOf(e.Text, StringComparison.Ordinal);
    }

    public static string Mask(string value)
    {
        var text = value.Trim();
        if (text.Length <= 4) return new string('*', Math.Max(text.Length, 1));
        return text[0] + new string('*', Math.Min(text.Length - 2, 12)) + text[^1];
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private sealed record ChunkJob(SessionAccumulator Session, IReadOnlyDictionary<int, SegmentInfo> Info, Chunk Chunk);

    private sealed record SegmentInfo(string Origin, string? MessageId, string InvocationId, string BlobPath);

    private sealed record MessageAccumulator(string Id, string CreatedAt, string InvocationId, string BlobPath, List<TextPart> Parts);

    private sealed record SegmentPlan(
        List<Segment> Segments, Dictionary<int, SegmentInfo> Info, int Characters, bool Truncated);

    private sealed class SessionAccumulator
    {
        public SessionAccumulator(string sessionId) => SessionId = sessionId;

        public string SessionId { get; }
        public string? TenantId { get; init; }
        public string? UserId { get; init; }
        public string? UserEmail { get; init; }
        public string? Source { get; init; }
        public DateTimeOffset FirstTimestamp { get; set; }
        public DateTimeOffset LastTimestamp { get; set; }
        public HashSet<string> InvocationIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MessageAccumulator> Messages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> MetaTexts { get; } = new(StringComparer.Ordinal);
        public int Characters { get; set; }
        public bool Truncated { get; set; }

        public SegmentPlan BuildSegments(int maxChars, bool includeToolOutput)
        {
            var segments = new List<Segment>();
            var info = new Dictionary<int, SegmentInfo>();
            var total = 0;
            var truncated = false;

            bool Add(string text, SegmentInfo meta)
            {
                if (string.IsNullOrWhiteSpace(text)) return true;
                var room = maxChars - total;
                if (room <= 0) { truncated = true; return false; }
                if (text.Length > room)
                {
                    text = text[..room];
                    if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
                    truncated = true;
                }
                var id = segments.Count;
                segments.Add(new Segment(id, text));
                info[id] = meta;
                total += text.Length;
                return !truncated;
            }

            var firstInvocation = Messages.Values.Select(m => m.InvocationId).FirstOrDefault() ?? SessionId;
            var firstPath = Messages.Values.Select(m => m.BlobPath).FirstOrDefault() ?? "";
            foreach (var (origin, text) in MetaTexts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var label = origin.StartsWith("metadata:file_name", StringComparison.Ordinal) ? "metadata:file_name" : origin;
                if (!Add(text, new SegmentInfo(label, null, firstInvocation, firstPath))) return new SegmentPlan(segments, info, total, truncated);
            }

            foreach (var message in Messages.Values.OrderBy(m => m.CreatedAt, StringComparer.Ordinal))
            {
                foreach (var part in message.Parts)
                {
                    if (!includeToolOutput && part.Origin is "tool_call" or "tool_result") continue;
                    if (!Add(part.Text, new SegmentInfo(part.Origin, message.Id, message.InvocationId, message.BlobPath)))
                        return new SegmentPlan(segments, info, total, truncated);
                }
            }

            return new SegmentPlan(segments, info, total, truncated);
        }
    }
}

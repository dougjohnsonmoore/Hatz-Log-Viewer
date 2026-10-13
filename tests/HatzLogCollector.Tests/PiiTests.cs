using System.Text.Json;
using System.Text.Json.Nodes;
using HatzLogCollector.Pii;
using HatzLogCollector.Storage;

namespace HatzLogCollector.Tests;

public class ChunkerTests
{
    [Fact]
    public void Chunks_never_exceed_the_limit_and_spans_map_back_to_the_source_text()
    {
        var segments = new[]
        {
            new Segment(0, "short first segment"),
            new Segment(1, string.Join(' ', Enumerable.Range(0, 400).Select(i => "word" + i))),
            new Segment(2, "tail"),
        };

        var chunks = TextChunker.Pack(segments, 300);

        Assert.All(chunks, c => Assert.True(c.Text.Length <= 300));
        foreach (var chunk in chunks)
        {
            foreach (var span in chunk.Spans)
            {
                var slice = chunk.Text.Substring(span.ChunkStart, span.Length);
                Assert.Contains(slice, segments[span.SegmentId].Text);
            }
        }

        // Every character of every segment is covered exactly once, in order.
        foreach (var segment in segments)
        {
            var rebuilt = string.Concat(chunks.SelectMany(c => c.Spans.Where(s => s.SegmentId == segment.Id).Select(s => c.Text.Substring(s.ChunkStart, s.Length))));
            Assert.Equal(segment.Text, rebuilt);
        }
    }

    [Fact]
    public void Small_segments_are_packed_together_to_limit_billed_records()
    {
        var segments = Enumerable.Range(0, 20).Select(i => new Segment(i, "tiny " + i)).ToList();
        var chunks = TextChunker.Pack(segments, 5000);
        Assert.Single(chunks);
        Assert.Equal(20, chunks[0].Spans.Count);
    }

    [Fact]
    public void Never_splits_a_surrogate_pair()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F600", 200)); // 400 UTF-16 units, no whitespace
        var chunks = TextChunker.Pack(new[] { new Segment(0, text) }, 51);

        Assert.All(chunks, c =>
        {
            Assert.True(c.Text.Length <= 51);
            Assert.False(char.IsHighSurrogate(c.Text[^1]));
            Assert.False(char.IsLowSurrogate(c.Text[0]));
        });
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public void Prefers_whitespace_for_split_points()
    {
        var text = string.Join(' ', Enumerable.Repeat("abcdefghi", 30)); // 299 chars
        var chunks = TextChunker.Pack(new[] { new Segment(0, text) }, 100);
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.EndsWith(" ", c.Text));
    }
}

public class ExtractorTests
{
    [Fact]
    public void Chat_message_yields_text_tool_call_code_and_tool_result_with_origins()
    {
        var message = Fixtures.AssistantMessage("m1", "final answer", "2026-10-12T20:41:05Z", code: "print('x')", toolResult: "{\"exit_code\": 0}");
        var parts = ContentTextExtractor.Extract(JsonSerializer.SerializeToElement(message), includeToolOutput: true).ToList();

        Assert.Contains(parts, p => p is { Origin: "assistant", Text: "final answer" });
        Assert.Contains(parts, p => p is { Origin: "tool_call", Text: "print('x')" });
        Assert.Contains(parts, p => p is { Origin: "tool_result", Text: "{\"exit_code\": 0}" });
    }

    [Fact]
    public void Tool_output_can_be_excluded()
    {
        var message = Fixtures.AssistantMessage("m1", "final answer", "2026-10-12T20:41:05Z", code: "print('x')", toolResult: "out");
        var parts = ContentTextExtractor.Extract(JsonSerializer.SerializeToElement(message), includeToolOutput: false).ToList();

        Assert.DoesNotContain(parts, p => p.Origin is "tool_call" or "tool_result");
        Assert.Contains(parts, p => p.Origin == "assistant");
    }

    [Fact]
    public void Non_chat_records_are_walked_generically_without_bookkeeping_fields()
    {
        var step = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["id"] = "11111111-2222-3333-4444-555555555555",
            ["step_id"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            ["created_at"] = "2026-10-12T20:41:05Z",
            ["status"] = "completed",
            ["input"] = new JsonObject { ["prompt"] = "Summarise the claim for Jordan Rivera" },
            ["output"] = new JsonArray("done"),
        });

        var parts = ContentTextExtractor.Extract(step, true).Select(p => p.Text).ToList();

        Assert.Contains("Summarise the claim for Jordan Rivera", parts);
        Assert.Contains("done", parts);
        Assert.DoesNotContain("completed", parts);
        Assert.DoesNotContain(parts, p => p.StartsWith("1111", StringComparison.Ordinal) || p.StartsWith("aaaa", StringComparison.Ordinal));
    }

    [Fact]
    public void Message_id_falls_back_to_the_index()
    {
        Assert.Equal("item-3", ContentTextExtractor.MessageId(JsonSerializer.SerializeToElement(new JsonObject()), 3));
    }
}

public class SeverityTests
{
    [Theory]
    [InlineData("USSocialSecurityNumber", Severity.High)]
    [InlineData("CreditCardNumber", Severity.High)]
    [InlineData("ABARoutingNumber", Severity.High)]
    [InlineData("USBankAccountNumber", Severity.High)]
    [InlineData("AzureStorageAccountKey", Severity.High)]
    [InlineData("Email", Severity.Medium)]
    [InlineData("PhoneNumber", Severity.Medium)]
    [InlineData("Address", Severity.Medium)]
    [InlineData("Person", Severity.Low)]
    [InlineData("URL", Severity.Low)]
    [InlineData("SomethingNew", Severity.Medium)]
    public void Defaults(string category, Severity expected) =>
        Assert.Equal(expected, new SeverityClassifier().Classify(category));

    [Fact]
    public void Overrides_win_and_bad_values_are_rejected()
    {
        var classifier = new SeverityClassifier(new Dictionary<string, string> { ["Email"] = "High", ["person"] = "medium" });
        Assert.Equal(Severity.High, classifier.Classify("Email"));
        Assert.Equal(Severity.Medium, classifier.Classify("Person"));
        Assert.Throws<InvalidOperationException>(() => new SeverityClassifier(new Dictionary<string, string> { ["Email"] = "Critical" }));
    }
}

public class ScanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);
    private static string Container => ArchivePaths.TenantContainer(Fixtures.Tenant);

    private static async Task Put(InMemoryStore store, JsonObject invocation, string status = "included")
    {
        var ts = DateTimeOffset.Parse(invocation["timestamp"]!.GetValue<string>());
        await store.WriteAsync(Container, ArchivePaths.InvocationPath(ts, invocation["invocation_id"]!.GetValue<string>()),
            Fixtures.Envelope(invocation, status), false, default);
    }

    private static PiiScanService Service(InMemoryStore store, FakeDetector detector, PiiOptions? options = null) =>
        new(store, detector, options ?? new PiiOptions(), TestLog.Logger);

    private static Task<ScanOutput> Scan(PiiScanService service) =>
        service.ScanContainerAsync(Container, T0.AddDays(-1), T0.AddDays(1), default);

    [Fact]
    public async Task Detects_attributes_and_masks_findings()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0, new[]
        {
            Fixtures.UserMessage("m1", "My SSN is 123-45-6789 and email jordan@example.test", T0.ToString("o")),
            Fixtures.AssistantMessage("m2", "Noted.", T0.AddSeconds(2).ToString("o"), toolResult: "contact: 987-65-4321"),
        }));

        var output = await Scan(Service(store, new FakeDetector()));

        var ssnUser = output.Findings.Single(f => f.Category == "USSocialSecurityNumber" && f.Origin == "user");
        Assert.Equal("High", ssnUser.Severity);
        Assert.Equal("m1", ssnUser.MessageId);
        Assert.Equal("inv-1", ssnUser.InvocationId);
        Assert.Equal(Fixtures.UserEmail, ssnUser.UserEmail);
        Assert.Equal("1*********9", ssnUser.MaskedText);

        Assert.Single(output.Findings, f => f.Category == "USSocialSecurityNumber" && f.Origin == "tool_result");
        Assert.Equal("Medium", output.Findings.Single(f => f.Category == "Email").Severity);

        var session = Assert.Single(output.Sessions);
        Assert.Equal(3, session.FindingCount);
        Assert.Equal("High", session.MaxSeverity);
    }

    [Fact]
    public async Task Reports_never_contain_the_raw_values()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0, new[]
        {
            Fixtures.UserMessage("m1", "SSN 123-45-6789, mail jordan@example.test, ask Jordan Rivera", T0.ToString("o")),
        }));
        var output = await Scan(Service(store, new FakeDetector()));

        var prefix = await ReportWriter.WriteAsync(store, output, "run1", T0, T0.AddDays(1), default);

        foreach (var file in new[] { "findings.jsonl", "sessions.csv", "summary.csv", "run.json" })
        {
            var text = (await store.ReadAsync(ArchivePaths.ReportsContainer, prefix + file, default))!;
            Assert.DoesNotContain("123-45-6789", text);
            Assert.DoesNotContain("jordan@example.test", text);
            Assert.DoesNotContain("Jordan Rivera", text);
        }
        Assert.NotEmpty(output.Findings);
    }

    [Fact]
    public async Task Filters_low_confidence_and_ignored_categories_by_default()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0, new[]
        {
            Fixtures.UserMessage("m1", "We spoke last Friday with Maybe Person about Jordan Rivera", T0.ToString("o")),
        }));

        var output = await Scan(Service(store, new FakeDetector()));

        Assert.Equal(new[] { "Person" }, output.Findings.Select(f => f.Category).ToArray());
        Assert.Equal(0.9, output.Findings.Single().Confidence);

        var everything = await Scan(Service(store, new FakeDetector(), new PiiOptions { IgnoredCategories = new(), MinConfidence = 0 }));
        Assert.Equal(3, everything.Findings.Count); // DateTime and the low-confidence Person are back
    }

    [Fact]
    public async Task A_session_spanning_several_invocations_is_scanned_once_per_message()
    {
        var store = new InMemoryStore();
        var m1 = Fixtures.UserMessage("m1", "SSN 123-45-6789", T0.ToString("o"));
        var m2 = Fixtures.AssistantMessage("m2", "ok", T0.AddSeconds(1).ToString("o"));
        var m3 = Fixtures.UserMessage("m3", "again 123-45-6789", T0.AddMinutes(1).ToString("o"));
        var m4 = Fixtures.AssistantMessage("m4", "ok again", T0.AddMinutes(1).AddSeconds(1).ToString("o"));

        // Hatz returns the full history on every invocation of the session.
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0.AddSeconds(1), new[] { m1, m2 }));
        await Put(store, Fixtures.Invocation("inv-2", "s-1", T0.AddMinutes(1).AddSeconds(1), new[] { m1, m2, m3, m4 }));

        var output = await Scan(Service(store, new FakeDetector()));

        Assert.Equal(1, output.Counters.Sessions);
        Assert.Equal(2, output.Findings.Count(f => f.Category == "USSocialSecurityNumber")); // m1 once, m3 once
        var session = Assert.Single(output.Sessions);
        Assert.Equal(2, session.InvocationCount);
        Assert.Equal(4, session.MessageCount);
    }

    [Fact]
    public async Task Metadata_only_records_are_counted_but_not_scanned()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0, null, sessionName: "SSN 123-45-6789"), status: "denied");
        var detector = new FakeDetector();

        var output = await Scan(Service(store, detector));

        Assert.Equal(1, output.Counters.InvocationsRead);
        Assert.Equal(1, output.Counters.InvocationsWithoutContent);
        Assert.Equal(0, detector.Calls);
        Assert.Empty(output.Findings);
    }

    [Fact]
    public async Task Session_name_and_file_names_are_scanned_as_metadata()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0,
            new[] { Fixtures.UserMessage("m1", "hello", T0.ToString("o")) },
            sessionName: "Claim for Jordan Rivera", fileNames: new[] { "123-45-6789.pdf" }));

        var output = await Scan(Service(store, new FakeDetector()));

        Assert.Contains(output.Findings, f => f.Origin == "metadata:session_name" && f.Category == "Person");
        Assert.Contains(output.Findings, f => f.Origin == "metadata:file_name" && f.Category == "USSocialSecurityNumber");
    }

    [Fact]
    public async Task Long_sessions_are_chunked_within_detector_limits_and_batched()
    {
        var store = new InMemoryStore();
        var text = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"filler{i}")) + " reach me at jordan@example.test";
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0, new[] { Fixtures.UserMessage("m1", text, T0.ToString("o")) }));
        var detector = new FakeDetector { MaxBatchSize = 2, MaxDocumentLength = 400 };

        var output = await Scan(Service(store, detector));

        Assert.True(output.Counters.Chunks > 4);
        Assert.True(detector.Calls >= output.Counters.Chunks / 2);
        Assert.Single(output.Findings, f => f.Category == "Email");
    }

    [Fact]
    public async Task Sessions_over_the_character_cap_are_truncated_and_reported()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0, new[]
        {
            Fixtures.UserMessage("m1", new string('a', 5000) + " 123-45-6789", T0.ToString("o")),
        }));

        var output = await Scan(Service(store, new FakeDetector(), new PiiOptions { MaxCharsPerSession = 1000 }));

        Assert.Equal(1, output.Counters.TruncatedSessions);
        Assert.True(output.Sessions.Single().Truncated);
        Assert.True(output.Sessions.Single().Characters <= 1000);
    }

    [Fact]
    public async Task Detector_failures_are_counted_not_swallowed()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-1", "s-1", T0, new[] { Fixtures.UserMessage("m1", "123-45-6789", T0.ToString("o")) }));

        var output = await Scan(Service(store, new FakeDetector { FailEverything = true }));

        Assert.Equal(1, output.Counters.ChunkErrors);
        Assert.Empty(output.Findings);
    }

    [Fact]
    public async Task Unreadable_blobs_are_counted_and_the_scan_continues()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-ok", "s-1", T0, new[] { Fixtures.UserMessage("m1", "123-45-6789", T0.ToString("o")) }));
        await store.WriteAsync(Container, ArchivePaths.InvocationPath(T0, "inv-corrupt"), "{not json", false, default);

        var output = await Scan(Service(store, new FakeDetector()));

        Assert.Equal(1, output.Counters.UnreadableBlobs);
        Assert.Single(output.Findings);
    }

    [Fact]
    public async Task Records_outside_the_requested_range_are_ignored()
    {
        var store = new InMemoryStore();
        await Put(store, Fixtures.Invocation("inv-in", "s-in", T0, new[] { Fixtures.UserMessage("m1", "123-45-6789", T0.ToString("o")) }));
        await Put(store, Fixtures.Invocation("inv-out", "s-out", T0.AddDays(-5), new[] { Fixtures.UserMessage("m2", "123-45-6789", T0.ToString("o")) }));

        var output = await Scan(Service(store, new FakeDetector()));

        Assert.Equal(1, output.Counters.InvocationsRead);
        Assert.Equal("s-in", output.Findings.Single().SessionId);
    }

    [Fact]
    public void Csv_cells_that_look_like_formulas_are_neutralised()
    {
        var output = new ScanOutput { Container = "c", TenantId = Fixtures.Tenant };
        output.Sessions.Add(new SessionSummary(Fixtures.Tenant, "s-1", "chat", "=HYPERLINK(\"http://evil\")", T0, T0, 1, 1, 10, false, 0, null));

        var csv = ReportWriter.SessionsCsv(output);

        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
    }

    [Theory]
    [InlineData("ab", "**")]
    [InlineData("1234", "****")]
    [InlineData("123-45-6789", "1*********9")]
    [InlineData("a-very-long-secret-value-here", "a************e")]
    public void Mask_hides_the_middle(string value, string expected) =>
        Assert.Equal(expected, PiiScanService.Mask(value));
}

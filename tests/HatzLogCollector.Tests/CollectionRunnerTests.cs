using System.Text.Json;
using System.Text.Json.Nodes;
using HatzLogCollector.Collection;
using HatzLogCollector.Hatz;
using HatzLogCollector.Storage;

namespace HatzLogCollector.Tests;

public class CollectionRunnerTests
{
    private sealed class FakeSecrets : ISecretResolver
    {
        public Task<string> GetAsync(string reference, CancellationToken ct) => Task.FromResult("key-for-" + reference);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static (CollectionRunner Runner, FakeHatzApi Api, InMemoryStore Store, FixedTime Time) Setup(
        Action<CollectorOptions>? tweak = null, SourceOptions? source = null)
    {
        var api = new FakeHatzApi();
        var store = new InMemoryStore();
        var time = new FixedTime(Now);
        var options = new CollectorOptions
        {
            InitialLookbackDays = 3,
            WindowHours = 24,
            OverlapMinutes = 15,
            PageSize = 2,
            MaxParallelDetailRequests = 3,
            Sources = { source ?? new SourceOptions { Name = "client", ApiKeyRef = "KEY", TenantIds = new() { Fixtures.Tenant } } },
        };
        tweak?.Invoke(options);
        var runner = new CollectionRunner(options, new FakeSecrets(), _ => api, store, TestLog.Logger, time);
        return (runner, api, store, time);
    }

    private static JsonObject Inv(string id, DateTimeOffset ts, string? session = null) =>
        Fixtures.Invocation(id, session ?? "s-" + id, ts, new[]
        {
            Fixtures.UserMessage("m-" + id, "hello " + id, ts.ToString("o")),
        });

    private static string Container => ArchivePaths.TenantContainer(Fixtures.Tenant);

    [Fact]
    public async Task Archives_every_invocation_across_pages_and_windows_with_content()
    {
        var (runner, api, store, _) = Setup(o => o.InitialLookbackDays = 10);
        for (var i = 0; i < 5; i++) api.Invocations.Add(Inv($"inv-{i}", Now.AddHours(-30 * i - 1)));

        var results = await runner.RunAsync(new CollectRequest(), CancellationToken.None);

        var r = Assert.Single(results);
        Assert.Equal(TenantRunStatus.Completed, r.Status);
        Assert.Equal(5, r.Archived);
        Assert.Equal(0, r.Failed);
        Assert.Equal(5, store.Count(Container, ArchivePaths.InvocationsPrefix + "/"));

        var path = ArchivePaths.InvocationPath(Now.AddHours(-1), "inv-0");
        using var doc = JsonDocument.Parse((await store.ReadAsync(Container, path, default))!);
        Assert.Equal("included", doc.RootElement.GetProperty("contentStatus").GetString());
        Assert.Equal("m-inv-0", doc.RootElement.GetProperty("invocation").GetProperty("content")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Second_run_is_idempotent_and_advances_the_watermark()
    {
        var (runner, api, store, _) = Setup();
        api.Invocations.Add(Inv("inv-a", Now.AddHours(-2)));
        api.Invocations.Add(Inv("inv-b", Now.AddHours(-3)));

        var first = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();
        var detailCallsAfterFirst = api.DetailCalls;
        var second = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(2, first.Archived);
        Assert.Equal(0, second.Archived);
        Assert.Equal(0, second.Listed); // resumes at watermark minus overlap, not at the start of history
        Assert.Equal(detailCallsAfterFirst, api.DetailCalls); // nothing re-fetched
        Assert.Equal(Now, second.WatermarkBefore);
        Assert.Equal(Now, second.WatermarkAfter);
        Assert.NotNull(await store.ReadAsync(ArchivePaths.StateContainer, ArchivePaths.WatermarkPath(Fixtures.Tenant), default));
    }

    [Fact]
    public async Task Overlap_picks_up_an_invocation_that_was_recorded_late()
    {
        var (runner, api, _, time) = Setup();
        api.Invocations.Add(Inv("inv-a", Now.AddHours(-2)));
        await runner.RunAsync(new CollectRequest(), CancellationToken.None);

        // Appears after the first run, but with a timestamp 5 minutes before the watermark.
        api.Invocations.Add(Inv("inv-late", Now.AddMinutes(-5)));
        time.Set(Now.AddHours(1));
        var second = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(1, second.Archived);
    }

    [Fact]
    public async Task A_failed_detail_call_blocks_the_watermark_and_is_retried_next_run()
    {
        var (runner, api, store, _) = Setup();
        api.Invocations.Add(Inv("inv-ok", Now.AddHours(-2)));
        api.Invocations.Add(Inv("inv-bad", Now.AddHours(-3)));
        api.FailDetailFor.Add("inv-bad");

        var first = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(TenantRunStatus.CompletedWithErrors, first.Status);
        Assert.Equal(1, first.Failed);
        var failedWindowStart = Now.AddHours(-24);
        Assert.Equal(failedWindowStart, first.WatermarkAfter); // earlier windows checkpointed; the failed one did not
        using (var saved = JsonDocument.Parse((await store.ReadAsync(ArchivePaths.StateContainer, ArchivePaths.WatermarkPath(Fixtures.Tenant), default))!))
            Assert.Equal(failedWindowStart, DateTimeOffset.Parse(saved.RootElement.GetProperty("lastWindowEndUtc").GetString()!));

        api.FailDetailFor.Clear();
        var second = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(TenantRunStatus.Completed, second.Status);
        Assert.Equal(1, second.Archived); // only the one that failed
        Assert.Equal(1, second.AlreadyArchived);
        Assert.Equal(Now, second.WatermarkAfter);
    }

    [Fact]
    public async Task Tenant_setting_without_content_archives_metadata_only_and_skips_detail_calls()
    {
        var (runner, api, store, _) = Setup();
        api.Settings[Fixtures.Tenant] = new ComplianceSettings(Fixtures.Tenant, true, true, false, "disabled");
        api.Invocations.Add(Inv("inv-a", Now.AddHours(-2)));

        var r = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(1, r.Archived);
        Assert.Equal(1, r.MetadataOnly);
        Assert.Equal(0, api.DetailCalls);
        using var doc = JsonDocument.Parse((await store.ReadAsync(Container, ArchivePaths.InvocationPath(Now.AddHours(-2), "inv-a"), default))!);
        Assert.Equal("disabled_by_tenant_setting", doc.RootElement.GetProperty("contentStatus").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("invocation").GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task Forbidden_content_falls_back_to_metadata_instead_of_failing()
    {
        var (runner, api, store, _) = Setup();
        api.Invocations.Add(Inv("inv-a", Now.AddHours(-2)));
        api.DenyContentFor.Add("inv-a");

        var r = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(TenantRunStatus.Completed, r.Status);
        Assert.Equal(1, r.MetadataOnly);
        using var doc = JsonDocument.Parse((await store.ReadAsync(Container, ArchivePaths.InvocationPath(Now.AddHours(-2), "inv-a"), default))!);
        Assert.Equal("denied", doc.RootElement.GetProperty("contentStatus").GetString());
    }

    [Fact]
    public async Task Unreadable_settings_are_treated_optimistically()
    {
        var (runner, api, _, _) = Setup();
        api.Settings[Fixtures.Tenant] = null; // 403 on settings with this key
        api.Invocations.Add(Inv("inv-a", Now.AddHours(-2)));

        var r = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(1, r.Archived);
        Assert.Equal(0, r.MetadataOnly);
        Assert.Equal(1, api.DetailCalls);
    }

    [Fact]
    public async Task Tenant_with_invocations_view_disabled_is_skipped()
    {
        var (runner, api, store, _) = Setup();
        api.Settings[Fixtures.Tenant] = new ComplianceSettings(Fixtures.Tenant, true, false, false, "disabled");
        api.Invocations.Add(Inv("inv-a", Now.AddHours(-2)));

        var r = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(TenantRunStatus.Skipped, r.Status);
        Assert.Equal(0, api.ListCalls);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Manual_range_does_not_touch_the_watermark()
    {
        var (runner, api, store, _) = Setup();
        api.Invocations.Add(Inv("inv-old", Now.AddDays(-10)));

        var r = (await runner.RunAsync(
            new CollectRequest(From: Now.AddDays(-11), To: Now.AddDays(-9)), CancellationToken.None)).Single();

        Assert.Equal(1, r.Archived);
        Assert.Null(await store.ReadAsync(ArchivePaths.StateContainer, ArchivePaths.WatermarkPath(Fixtures.Tenant), default));
    }

    [Fact]
    public async Task Dry_run_lists_but_writes_and_fetches_nothing()
    {
        var (runner, api, store, _) = Setup();
        api.Invocations.Add(Inv("inv-a", Now.AddHours(-2)));

        var r = (await runner.RunAsync(new CollectRequest(DryRun: true), CancellationToken.None)).Single();

        Assert.Equal(1, r.Listed);
        Assert.Equal(0, r.Archived);
        Assert.Equal(0, api.DetailCalls);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Discovery_honours_exclusions_and_tenant_filter()
    {
        var (runner, api, _, _) = Setup(source: new SourceOptions
        {
            Name = "msp", ApiKeyRef = "MSP", DiscoverTenants = true, ExcludeTenantIds = new() { "tenant_SKIP" },
        });
        api.Tenants.AddRange(new[]
        {
            new TenantInfo(Fixtures.Tenant, "Example Co", "Active"),
            new TenantInfo("tenant_SKIP", "Skipped", "Active"),
            new TenantInfo("tenant_OTHER", "Other", "Active"),
        });

        var all = await runner.RunAsync(new CollectRequest(DryRun: true), CancellationToken.None);
        Assert.Equal(new[] { Fixtures.Tenant, "tenant_OTHER" }, all.Select(r => r.TenantId).ToArray());

        var one = await runner.RunAsync(new CollectRequest(TenantIds: new[] { "tenant_OTHER" }, DryRun: true), CancellationToken.None);
        Assert.Equal("tenant_OTHER", Assert.Single(one).TenantId);
    }

    [Fact]
    public async Task Source_without_tenants_configured_reports_a_failure_instead_of_guessing()
    {
        var (runner, _, _, _) = Setup(source: new SourceOptions { Name = "bad", ApiKeyRef = "K" });

        var r = (await runner.RunAsync(new CollectRequest(), CancellationToken.None)).Single();

        Assert.Equal(TenantRunStatus.Failed, r.Status);
        Assert.Contains("TenantIds or DiscoverTenants", r.Reason);
    }

    [Fact]
    public void Container_names_are_valid_for_azure_blob()
    {
        foreach (var id in new[] { "tenant_AbCdEfGh12345678", "Tenant With Spaces!!", new string('x', 200), "___" })
        {
            var name = ArchivePaths.TenantContainer(id);
            Assert.InRange(name.Length, 3, 63);
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", name);
        }
        Assert.NotEqual(ArchivePaths.TenantContainer(new string('x', 200) + "a"), ArchivePaths.TenantContainer(new string('x', 200) + "b"));
    }

    [Fact]
    public async Task Local_store_is_create_only_and_rejects_path_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "hatzlog-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalArchiveStore(root);
            Assert.True(await store.WriteAsync("c", "a/b.json", "one", overwrite: false, default));
            Assert.False(await store.WriteAsync("c", "a/b.json", "two", overwrite: false, default));
            Assert.Equal("one", await store.ReadAsync("c", "a/b.json", default));
            Assert.True(await store.WriteAsync("c", "a/b.json", "three", overwrite: true, default));
            Assert.Equal("three", await store.ReadAsync("c", "a/b.json", default));
            Assert.Equal(new[] { "a/b.json" }, await store.ListAsync("c", "a/", default).ToListAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("c", "../../etc/passwd", default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

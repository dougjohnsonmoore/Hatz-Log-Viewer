using System.Net;
using System.Text;
using HatzLogCollector.Hatz;

namespace HatzLogCollector.Tests;

public class HatzApiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler Enqueue(HttpStatusCode status, string body = "{}", Action<HttpResponseMessage>? configure = null)
        {
            _responses.Enqueue(_ =>
            {
                var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                configure?.Invoke(r);
                return r;
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private const string PageJson = """
        {"data":[{"invocation_id":"inv-1","session_id":"s-1","timestamp":"2026-10-12T20:42:23.292965+00:00","source":"chat",
        "tenant":{"id":"tenant_TEST0001","name":"Example Co"},"content":null}],
        "pagination":{"has_more":true,"next_cursor":"abc=="}}
        """;

    [Fact]
    public async Task ListInvocations_sends_key_and_filters_and_parses_page()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, PageJson);
        var client = HatzApiClient.Create("https://example.test/v1", "secret-key", innerHandler: handler);

        var page = await client.ListInvocationsAsync(new InvocationQuery(
            new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero),
            "tenant_TEST0001", "cur sor", 50), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("secret-key", request.Headers.GetValues("X-API-Key").Single());
        Assert.Equal(
            "https://example.test/v1/compliance/invocations?limit=50&start=2026-10-12T00%3A00%3A00Z&end=2026-10-13T00%3A00%3A00Z&tenant_id=tenant_TEST0001&cursor=cur%20sor",
            request.RequestUri!.AbsoluteUri);

        var item = Assert.Single(page.Items);
        Assert.Equal("inv-1", item.InvocationId);
        Assert.Equal("tenant_TEST0001", item.TenantId);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 20, 42, 23, TimeSpan.Zero).AddTicks(2929650), item.Timestamp);
        Assert.True(page.HasMore);
        Assert.Equal("abc==", page.NextCursor);
    }

    [Fact]
    public async Task GetInvocation_requests_content_only_when_asked()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK).Enqueue(HttpStatusCode.OK);
        var client = HatzApiClient.Create("https://example.test/v1/", "k", innerHandler: handler);

        await client.GetInvocationAsync("11111111-0000-0000-0000-000000000001", true, CancellationToken.None);
        await client.GetInvocationAsync("11111111-0000-0000-0000-000000000001", false, CancellationToken.None);

        Assert.EndsWith("/compliance/invocations/11111111-0000-0000-0000-000000000001?include_content=true", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.EndsWith("/compliance/invocations/11111111-0000-0000-0000-000000000001", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Settings_returns_null_when_forbidden()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.Forbidden, "{\"detail\":\"no\"}");
        var client = HatzApiClient.Create("https://example.test/v1", "k", innerHandler: handler);
        Assert.Null(await client.GetComplianceSettingsAsync("tenant_TEST0001", CancellationToken.None));
    }

    [Fact]
    public async Task Errors_throw_without_leaking_the_api_key()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, "{\"detail\":\"bad key\"}");
        var client = HatzApiClient.Create("https://example.test/v1", "super-secret", innerHandler: handler);

        var ex = await Assert.ThrowsAsync<HatzApiException>(() => client.ListTenantsAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("super-secret", ex.Message);
    }

    [Fact]
    public async Task ListTenants_pages_until_a_short_page()
    {
        var full = "[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"{{\"id\":\"t{i}\",\"name\":\"n{i}\",\"status\":\"Active\"}}")) + "]";
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.OK, full)
            .Enqueue(HttpStatusCode.OK, "[{\"id\":\"last\",\"name\":\"Last\",\"status\":\"Active\"}]");
        var client = HatzApiClient.Create("https://example.test/v1", "k", innerHandler: handler);

        var tenants = await client.ListTenantsAsync(CancellationToken.None);

        Assert.Equal(1001, tenants.Count);
        Assert.Contains("offset=1000", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task Retry_honours_retry_after_on_429_then_succeeds()
    {
        var handler = new StubHandler()
            .Enqueue((HttpStatusCode)429, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)))
            .Enqueue(HttpStatusCode.OK, "[]");
        var delays = new List<TimeSpan>();
        var http = new HttpClient(new RetryHandler(handler, delay: (d, _) => { delays.Add(d); return Task.CompletedTask; })) { BaseAddress = new Uri("https://example.test/v1/") };

        using var response = await http.GetAsync("admin/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public async Task Retry_gives_up_after_max_attempts_and_returns_the_last_response()
    {
        var handler = new StubHandler();
        for (var i = 0; i < 3; i++) handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var http = new HttpClient(new RetryHandler(handler, maxAttempts: 3, delay: (_, _) => Task.CompletedTask)) { BaseAddress = new Uri("https://example.test/v1/") };

        using var response = await http.GetAsync("admin/tenants");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Retry_does_not_retry_client_errors()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.NotFound);
        var http = new HttpClient(new RetryHandler(handler, delay: (_, _) => Task.CompletedTask)) { BaseAddress = new Uri("https://example.test/v1/") };

        using var response = await http.GetAsync("x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retried_requests_keep_the_api_key_header()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.BadGateway).Enqueue(HttpStatusCode.OK, "[]");
        var client = HatzApiClient.Create("https://example.test/v1", "k-123", innerHandler: handler);

        // Default backoff would sleep ~1s; acceptable for one test and exercises the real path.
        await client.ListTenantsAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("k-123", r.Headers.GetValues("X-API-Key").Single()));
    }
}

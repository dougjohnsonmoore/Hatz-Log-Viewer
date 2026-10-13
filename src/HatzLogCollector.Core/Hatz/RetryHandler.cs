using System.Net;
using Microsoft.Extensions.Logging;

namespace HatzLogCollector.Hatz;

/// <summary>
/// Retries idempotent GETs on 429, 5xx and transient network failures, honouring Retry-After.
/// Hatz does not document rate limits, so this is the safety net for bulk content pulls.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger? _logger;

    public RetryHandler(
        HttpMessageHandler inner,
        int maxAttempts = 6,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null) : base(inner)
    {
        _maxAttempts = maxAttempts;
        _delay = delay ?? Task.Delay;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || request.Content != null)
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            var current = attempt == 1 ? request : Clone(request);
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(current, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _maxAttempts) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _maxAttempts) { }

            TimeSpan wait;
            if (response != null)
            {
                if (!IsTransient(response.StatusCode) || attempt == _maxAttempts)
                    return response;
                wait = RetryAfter(response) ?? Backoff(attempt);
                _logger?.LogWarning("Hatz returned {Status}; retry {Attempt}/{Max} in {Wait:0.0}s",
                    (int)response.StatusCode, attempt, _maxAttempts - 1, wait.TotalSeconds);
                response.Dispose();
            }
            else
            {
                wait = Backoff(attempt);
                _logger?.LogWarning("Hatz request failed in transit; retry {Attempt}/{Max} in {Wait:0.0}s",
                    attempt, _maxAttempts - 1, wait.TotalSeconds);
            }

            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble());
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header == null) return null;
        var wait = header.Delta ?? (header.Date.HasValue ? header.Date.Value - DateTimeOffset.UtcNow : null);
        if (wait == null) return null;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        return wait > TimeSpan.FromSeconds(120) ? TimeSpan.FromSeconds(120) : wait;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}

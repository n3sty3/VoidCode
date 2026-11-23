using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Agent.Providers;

/// <summary>
/// Resilient HTTP execution for LLM calls.
/// Retries 429, 408, 500, 502, 503, 504 and transient network/timeout errors with exponential
/// Backoff + jitter, honoring Retry-After headers and honoring caller cancellation.
/// </summary>
internal sealed class RetryPolicy
{
    internal const int DefaultMaxRetries = 5;
    internal static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<double> _nextRandom;
    private readonly Func<DateTimeOffset> _utcNow;

    internal static readonly RetryPolicy Default = new();

    internal RetryPolicy(
        int maxRetries = DefaultMaxRetries,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<double>? nextRandom = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _baseDelay = baseDelay ?? DefaultBaseDelay;
        _maxDelay = maxDelay ?? DefaultMaxDelay;
        _delayAsync = delayAsync ?? ((d, ct) => Task.Delay(d, ct));
        _nextRandom = nextRandom ?? (() => Random.Shared.NextDouble());
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal static bool IsRetryableStatus(HttpStatusCode code)
    {
        var numeric = (int)code;
        return numeric is 408 or 429 or 500 or 502 or 503 or 504 or 529;
    }

    internal TimeSpan ComputeDelay(int attemptIndex, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter != null)
        {
            var ra = response.Headers.RetryAfter;
            if (ra.Delta.HasValue && ra.Delta.Value > TimeSpan.Zero)
            {
                return ra.Delta.Value <= MaxRetryAfter ? ra.Delta.Value : MaxRetryAfter;
            }
            if (ra.Date.HasValue)
            {
                var diff = ra.Date.Value - _utcNow();
                if (diff > TimeSpan.Zero)
                {
                    return diff <= MaxRetryAfter ? diff : MaxRetryAfter;
                }
            }
        }

        // Also accept plain numeric Retry-After headers when a gateway sends non-standard casing/spacing.
        if (response != null && response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var raw in values)
            {
                if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0)
                {
                    var d = TimeSpan.FromSeconds(secs);
                    return d <= MaxRetryAfter ? d : MaxRetryAfter;
                }
            }
        }

        var exponent = Math.Min(attemptIndex, 10);
        var expMs = _baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(expMs, _maxDelay.TotalMilliseconds);
        // Full/equal jitter in [0.75x, 1.25x] to prevent synchronized retries.
        var jitterFactor = 0.75 + (_nextRandom() * 0.5);
        var finalMs = Math.Clamp(cappedMs * jitterFactor, 1, _maxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(finalMs);
    }

    internal async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> buildRequest,
        HttpCompletionOption completionOption,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            Exception? transientException = null;

            try
            {
                using var req = buildRequest();
                response = await client.SendAsync(req, completionOption, cancellationToken);
                if (!IsRetryableStatus(response.StatusCode) || attempt >= _maxRetries)
                {
                    return response;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                transientException = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < _maxRetries)
            {
                transientException = ex;
            }

            var delay = ComputeDelay(attempt, response);
            var secs = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
            var retryNum = attempt + 1;

            string statusMsg;
            if (response != null)
            {
                var code = (int)response.StatusCode;
                statusMsg = code == 429
                    ? $"Rate limited (HTTP 429) — retrying in {secs}s (attempt {retryNum}/{_maxRetries})..."
                    : $"Provider error (HTTP {code}) — retrying in {secs}s (attempt {retryNum}/{_maxRetries})...";
                LoggingService.LogWarning($"{statusMsg}", nameof(RetryPolicy));
                response.Dispose();
            }
            else
            {
                statusMsg = $"Network error ({transientException?.Message ?? "timeout"}) — retrying in {secs}s (attempt {retryNum}/{_maxRetries})...";
                LoggingService.LogWarning(statusMsg, nameof(RetryPolicy));
            }

            onStatus?.Invoke(statusMsg);
            await _delayAsync(delay, cancellationToken);
        }
    }
}

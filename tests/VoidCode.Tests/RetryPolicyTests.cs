using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Providers;
using Xunit;

namespace VoidCode.Tests;

public class RetryPolicyTests
{
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps;
        public int Calls { get; private set; }

        public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
        {
            _steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(steps);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_steps.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(_steps.Dequeue()(request));
        }
    }

    [Fact]
    public async Task Retries_Http429_HonorsRetryAfterHeader_AndEmitsStatus()
    {
        var delays = new List<TimeSpan>();
        var statuses = new List<string>();
        var handler = new SequenceHandler(
            _ =>
            {
                var r = new HttpResponseMessage((HttpStatusCode)429);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok", Encoding.UTF8, "text/plain")
            });

        using var client = new HttpClient(handler);
        var policy = new RetryPolicy(
            maxRetries: 5,
            delayAsync: (d, _) => { delays.Add(d); return Task.CompletedTask; },
            nextRandom: () => 0.5);

        using var resp = await policy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions"),
            HttpCompletionOption.ResponseHeadersRead,
            s => statuses.Add(s),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(7), delays[0]);
        Assert.Contains(statuses, s => s.Contains("429") && s.Contains("7s"));
    }

    [Fact]
    public async Task Retries_503And529_WithExponentialBackoff_ThenSucceeds()
    {
        var delays = new List<TimeSpan>();
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage((HttpStatusCode)529),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = new HttpClient(handler);
        var policy = new RetryPolicy(
            maxRetries: 5,
            baseDelay: TimeSpan.FromSeconds(1),
            delayAsync: (d, _) => { delays.Add(d); return Task.CompletedTask; },
            nextRandom: () => 0.5); // 0.75 + 0.5*0.5 = 1.0x exact

        using var resp = await policy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/v1/messages"),
            HttpCompletionOption.ResponseContentRead,
            null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[1]);
    }

    [Fact]
    public async Task Retries_TransientNetworkException_AndRecovers()
    {
        var handler = new SequenceHandler(
            _ => throw new HttpRequestException("connection reset"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = new HttpClient(handler);
        var policy = new RetryPolicy(
            maxRetries: 3,
            delayAsync: (_, _) => Task.CompletedTask,
            nextRandom: () => 0.5);

        using var resp = await policy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/v1/models"),
            HttpCompletionOption.ResponseContentRead,
            null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task StopsAfterMaxRetries_AndNeverRetries400()
    {
        var handler503 = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client503 = new HttpClient(handler503);
        var policy = new RetryPolicy(maxRetries: 2, delayAsync: (_, _) => Task.CompletedTask);

        using var resp503 = await policy.SendAsync(
            client503,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"),
            HttpCompletionOption.ResponseContentRead,
            null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp503.StatusCode);
        Assert.Equal(3, handler503.Calls);

        var handler400 = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client400 = new HttpClient(handler400);
        using var resp400 = await policy.SendAsync(
            client400,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"),
            HttpCompletionOption.ResponseContentRead,
            null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, resp400.StatusCode);
        Assert.Equal(1, handler400.Calls);
    }

    [Fact]
    public async Task HonorsCancellationDuringBackoff()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage((HttpStatusCode)429));
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();

        var policy = new RetryPolicy(
            maxRetries: 5,
            delayAsync: (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await policy.SendAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"),
                HttpCompletionOption.ResponseHeadersRead,
                null,
                cts.Token);
        });
    }
}

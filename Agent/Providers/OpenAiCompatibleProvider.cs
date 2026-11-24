using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Agent.Providers;

/// <summary>
/// OpenAI-compatible chat/completions provider (OpenRouter, OpenAI, DeepSeek, Ollama,
/// LM Studio, custom gateways) with RetryPolicy and capability caching.
/// </summary>
internal sealed class OpenAiCompatibleProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly RetryPolicy _retry;
    private readonly ProviderCapabilityCache _capabilities;

    public string Name => "openai-compatible";

    /// <summary>
    /// Ensures Google Gemini base URLs always point to the OpenAI-compatible
    /// `/v1beta/openai` endpoint even if the user pasted only the host or `/v1beta`.
    /// </summary>
    internal static string NormalizeBaseUrl(string? provider, string? baseUrl)
    {
        var raw = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        var isGemini = string.Equals(provider, "gemini", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);

        if (isGemini)
        {
            if (string.IsNullOrEmpty(raw))
                return "https://generativelanguage.googleapis.com/v1beta/openai";
            if (raw.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase)
                && !raw.Contains("/openai", StringComparison.OrdinalIgnoreCase))
            {
                if (raw.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) || raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    return raw + "/openai";
                return raw + "/v1beta/openai";
            }
        }
        return raw;
    }

    internal OpenAiCompatibleProvider(
        HttpClient? http = null,
        RetryPolicy? retry = null,
        ProviderCapabilityCache? capabilities = null)
    {
        _http = http ?? AgentHttp.Client;
        _retry = retry ?? RetryPolicy.Default;
        _capabilities = capabilities ?? ProviderCapabilityCache.Shared;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(request.Provider, request.BaseUrl);
        var isGemini = string.Equals(request.Provider, "gemini", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
        var modelId = (request.Model ?? "").Trim();
        if (isGemini && modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            modelId = modelId["models/".Length..];
        }
        var caps = _capabilities.Get(request.Provider, baseUrl, modelId);
        var includeTools = request.Tools != null && request.Tools.Count > 0 && caps.ToolsSupported;
        var includeUsage = request.Stream && caps.StreamUsageSupported;

        var pendingStatuses = new Queue<string>();

        JsonObject BuildBody(bool withTools, bool withStreamUsage)
        {
            var messagesArray = new JsonArray();
            foreach (var m in request.Messages)
            {
                messagesArray.Add(JsonNode.Parse(m.ToJsonString()));
            }

            var body = new JsonObject
            {
                ["model"] = modelId,
                ["messages"] = messagesArray,
                ["temperature"] = request.Temperature,
                ["max_tokens"] = request.MaxTokens,
                ["stream"] = request.Stream
            };
            if (withTools && request.Tools != null)
            {
                body["tools"] = JsonNode.Parse(request.Tools.ToJsonString());
            }
            if (withStreamUsage)
            {
                body["stream_options"] = new JsonObject { ["include_usage"] = true };
            }
            return body;
        }

        HttpRequestMessage BuildHttpRequest(JsonObject body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, AgentHttp.CombineUrl(baseUrl, "chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            var apiKey = SettingsService.ResolveApiKey(request.ApiKey, request.Provider, baseUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://voidcode.local");
            req.Headers.TryAddWithoutValidation("X-Title", "VoidCode IDE");
            return req;
        }

        HttpResponseMessage? response = null;
        string? networkError = null;
        try
        {
            var body = BuildBody(includeTools, includeUsage);
            response = await _retry.SendAsync(
                _http,
                () => BuildHttpRequest(body),
                HttpCompletionOption.ResponseHeadersRead,
                status => pendingStatuses.Enqueue(status),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Network error calling LLM API ({request.BaseUrl}): {ex.Message}", ex);
            networkError = $"Network error calling LLM API: {ex.Message}";
        }

        while (pendingStatuses.Count > 0)
        {
            yield return new ChatStatusEvent(pendingStatuses.Dequeue());
        }

        if (networkError != null)
        {
            yield return new ChatErrorEvent(networkError);
            yield break;
        }

        // If gateway rejected stream_options or tools with 400/404/422, cache the capability downgrade
        // And retry once without the unsupported option.
        if (response != null && !response.IsSuccessStatusCode && (includeTools || includeUsage))
        {
            var status = (int)response.StatusCode;
            if (status is 400 or 404 or 422)
            {
                var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var rejectedStreamOptions = includeUsage &&
                    (errBody.Contains("stream_options", StringComparison.OrdinalIgnoreCase) ||
                     errBody.Contains("include_usage", StringComparison.OrdinalIgnoreCase));

                bool shouldRetry = false;
                if (rejectedStreamOptions)
                {
                    _capabilities.MarkStreamUsageUnsupported(request.Provider, baseUrl, modelId);
                    includeUsage = false;
                    shouldRetry = true;
                    LoggingService.LogWarning($"Provider rejected stream_options (HTTP {status}); cached and retrying without stream_options.");
                }
                else if (includeTools)
                {
                    _capabilities.MarkToolsUnsupported(request.Provider, baseUrl, modelId);
                    includeTools = false;
                    shouldRetry = true;
                    LoggingService.LogWarning($"Provider rejected tools (HTTP {status}); cached and retrying without function calling. {errBody}");
                }

                if (shouldRetry)
                {
                    response.Dispose();
                    response = null;
                    try
                    {
                        var retryBody = BuildBody(includeTools, includeUsage);
                        response = await _retry.SendAsync(
                            _http,
                            () => BuildHttpRequest(retryBody),
                            HttpCompletionOption.ResponseHeadersRead,
                            statusMsg => pendingStatuses.Enqueue(statusMsg),
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogError($"Network error on capability-downgrade retry ({request.BaseUrl}): {ex.Message}", ex);
                        networkError = $"Network error calling LLM API: {ex.Message}";
                    }

                    while (pendingStatuses.Count > 0)
                    {
                        yield return new ChatStatusEvent(pendingStatuses.Dequeue());
                    }

                    if (networkError != null)
                    {
                        yield return new ChatErrorEvent(networkError);
                        yield break;
                    }
                }
                else
                {
                    LoggingService.LogError($"LLM API returned {status} for model '{request.Model}': {errBody}");
                    response.Dispose();
                    yield return new ChatErrorEvent($"API Error ({status}): {errBody}", status);
                    yield break;
                }
            }
        }

        if (response == null)
        {
            yield return new ChatErrorEvent("Provider returned no response.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errText = await response.Content.ReadAsStringAsync(cancellationToken);
                var statusCode = (int)response.StatusCode;
                LoggingService.LogError($"LLM API returned {statusCode} for model '{request.Model}': {errText}");
                yield return new ChatErrorEvent($"API Error ({statusCode} {response.StatusCode}): {errText}", statusCode);
                yield break;
            }

            LoggingService.LogInfo($"LLM response {response.StatusCode} ({response.Content.Headers.ContentType?.MediaType ?? "no-content-type"}) for model '{request.Model}'.");

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Non-SSE JSON fallback
                if (line.TrimStart().StartsWith("{", StringComparison.Ordinal) && !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var fullJson = line + await reader.ReadToEndAsync(cancellationToken);
                    foreach (var evt in ParseNonStreamJson(fullJson))
                    {
                        yield return evt;
                    }
                    yield break;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var dataStr = line.Substring(5).Trim();
                if (dataStr == "[DONE]") yield break;

                JsonNode? chunkNode;
                try
                {
                    chunkNode = JsonNode.Parse(dataStr);
                }
                catch (Exception ex)
                {
                    LoggingService.Swallowed(ex, "OpenAiCompatibleProvider.ParseSseChunk");
                    continue;
                }

                var usageNode = chunkNode?["usage"];
                if (usageNode != null)
                {
                    var pt = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0;
                    var ct = usageNode["completion_tokens"]?.GetValue<int>() ?? 0;
                    var cached = usageNode["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>()
                        ?? usageNode["prompt_cache_hit_tokens"]?.GetValue<int>()
                        ?? 0;
                    if (pt > 0 || ct > 0)
                    {
                        yield return new ChatUsageEvent(pt, ct, cached);
                    }
                }

                var choice = chunkNode?["choices"]?[0];
                var delta = choice?["delta"];
                var message = choice?["message"];
                if (delta == null && message == null) continue;

                var reasoningDelta = delta?["reasoning_content"]?.GetValue<string>()
                    ?? delta?["reasoning"]?.GetValue<string>()
                    ?? message?["reasoning_content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(reasoningDelta))
                {
                    yield return new ChatReasoningDelta(reasoningDelta);
                }

                var contentDelta = delta?["content"]?.GetValue<string>()
                    ?? message?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(contentDelta))
                {
                    yield return new ChatContentDelta(contentDelta);
                }

                var toolCallsDelta = (delta?["tool_calls"] ?? message?["tool_calls"])?.AsArray();
                if (toolCallsDelta != null)
                {
                    int fallbackIdx = 0;
                    foreach (var tc in toolCallsDelta)
                    {
                        if (tc == null) continue;
                        int idx = tc["index"]?.GetValue<int>() ?? fallbackIdx;
                        fallbackIdx++;
                        var idVal = tc["id"]?.GetValue<string>();
                        var fnNode = tc["function"];
                        var fnName = fnNode?["name"]?.GetValue<string>();
                        var fnArgs = fnNode?["arguments"]?.GetValue<string>();
                        var extraJson = tc["extra_content"]?.ToJsonString();
                        yield return new ChatToolCallDelta(idx, idVal, fnName, fnArgs, extraJson);
                    }
                }
            }
        }
    }

    public async Task<string> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var nonStreamReq = new ChatRequest
        {
            Provider = request.Provider,
            BaseUrl = request.BaseUrl,
            ApiKey = request.ApiKey,
            Model = request.Model,
            Messages = request.Messages,
            Tools = null,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            Stream = false
        };

        var sb = new StringBuilder();
        await foreach (var evt in StreamAsync(nonStreamReq, cancellationToken))
        {
            switch (evt)
            {
                case ChatContentDelta c:
                    sb.Append(c.Text);
                    break;
                case ChatErrorEvent err:
                    throw new InvalidOperationException(err.Message);
            }
        }
        return sb.ToString();
    }

    public async Task<(bool success, string message, List<string> models)> TestConnectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var models = new List<string>();
        try
        {
            var normalizedBase = NormalizeBaseUrl(settings.Provider, settings.BaseUrl);
            var url = AgentHttp.CombineUrl(normalizedBase, "models");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var apiKey = SettingsService.ResolveApiKey(settings.ApiKey, settings.Provider, normalizedBase);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://voidcode.local");
            req.Headers.TryAddWithoutValidation("X-Title", "VoidCode IDE");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)resp.StatusCode}: {body}", models);
            }

            var isGemini = string.Equals(settings.Provider, "gemini", StringComparison.OrdinalIgnoreCase)
                || (settings.BaseUrl ?? "").Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
            var node = JsonNode.Parse(body);
            var dataArr = node?["data"]?.AsArray() ?? node?["models"]?.AsArray();
            if (dataArr != null)
            {
                foreach (var item in dataArr)
                {
                    var id = item?["id"]?.GetValue<string>() ?? item?["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id))
                    {
                        if (isGemini && id.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                            id = id["models/".Length..];
                        models.Add(id);
                    }
                }
            }

            return (true, $"Connected! Found {models.Count} models.", models.Take(120).ToList());
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}", models);
        }
    }

    private static List<ChatEvent> ParseNonStreamJson(string jsonStr)
    {
        var events = new List<ChatEvent>();
        try
        {
            var root = JsonNode.Parse(jsonStr);
            var usage = root?["usage"];
            if (usage != null)
            {
                var pt = usage["prompt_tokens"]?.GetValue<int>() ?? 0;
                var ct = usage["completion_tokens"]?.GetValue<int>() ?? 0;
                if (pt > 0 || ct > 0) events.Add(new ChatUsageEvent(pt, ct));
            }

            var msg = root?["choices"]?[0]?["message"];
            if (msg == null) return events;

            var reasoning = msg["reasoning_content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(reasoning)) events.Add(new ChatReasoningDelta(reasoning));

            var content = msg["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content)) events.Add(new ChatContentDelta(content));

            var tcs = msg["tool_calls"]?.AsArray();
            if (tcs != null)
            {
                int i = 0;
                foreach (var tc in tcs)
                {
                    if (tc == null) continue;
                    events.Add(new ChatToolCallDelta(
                        i++,
                        tc["id"]?.GetValue<string>() ?? $"call_{Guid.NewGuid():N}",
                        tc["function"]?["name"]?.GetValue<string>() ?? "",
                        tc["function"]?["arguments"]?.GetValue<string>() ?? "{}",
                        tc["extra_content"]?.ToJsonString()
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "OpenAiCompatibleProvider.ParseNonStreamJson");
        }
        return events;
    }
}

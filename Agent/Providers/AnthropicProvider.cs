using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Agent.Providers;

/// <summary>
/// Native Anthropic Messages API provider (/v1/messages) with full translation of
/// System prompts, tool definitions, tool_use / tool_result blocks, multimodal base64 images,
/// Extended thinking deltas, and token usage events.
/// </summary>
internal sealed class AnthropicProvider : IChatProvider
{
    internal const string DefaultApiVersion = "2023-06-01";
    internal const string DefaultBaseUrl = "https://api.anthropic.com/v1";

    private readonly HttpClient _http;
    private readonly RetryPolicy _retry;

    public string Name => "anthropic";

    internal AnthropicProvider(HttpClient? http = null, RetryPolicy? retry = null)
    {
        _http = http ?? AgentHttp.Client;
        _retry = retry ?? RetryPolicy.Default;
    }

    internal static string ResolveEndpoint(string? baseUrl, string endpoint)
    {
        var raw = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
        if (!raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            raw += "/v1";
        }
        return AgentHttp.CombineUrl(raw, endpoint);
    }

    internal static JsonObject BuildMessagesPayload(ChatRequest request)
    {
        var systemParts = new List<string>();
        var translated = new List<(string role, JsonArray blocks)>();

        void AppendMessage(string role, JsonArray newBlocks)
        {
            if (newBlocks.Count == 0) return;
            // Anthropic requires strictly alternating user/assistant turns; merge adjacent same-role turns.
            if (translated.Count > 0 && string.Equals(translated[^1].role, role, StringComparison.Ordinal))
            {
                foreach (var block in newBlocks)
                {
                    if (block != null) translated[^1].blocks.Add(JsonNode.Parse(block.ToJsonString()));
                }
            }
            else
            {
                translated.Add((role, newBlocks));
            }
        }

        foreach (var msg in request.Messages)
        {
            var role = msg["role"]?.GetValue<string>() ?? "user";
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                var sysText = ExtractPlainText(msg["content"]);
                if (!string.IsNullOrWhiteSpace(sysText)) systemParts.Add(sysText);
                continue;
            }

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                var toolUseId = msg["tool_call_id"]?.GetValue<string>() ?? $"call_{Guid.NewGuid():N}";
                var contentText = ExtractPlainText(msg["content"]);
                var blocks = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUseId,
                        ["content"] = contentText
                    }
                };
                AppendMessage("user", blocks);
                continue;
            }

            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                var blocks = ConvertContentToBlocks(msg["content"]);
                if (msg["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var tc in toolCalls)
                    {
                        if (tc == null) continue;
                        var id = tc["id"]?.GetValue<string>() ?? $"call_{Guid.NewGuid():N}";
                        var name = tc["function"]?["name"]?.GetValue<string>() ?? "";
                        var argsStr = tc["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                        JsonNode inputObj;
                        try
                        {
                            inputObj = JsonNode.Parse(string.IsNullOrWhiteSpace(argsStr) ? "{}" : argsStr) as JsonObject
                                ?? new JsonObject();
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Swallowed(ex, "AnthropicProvider.ParseToolCallInput");
                            inputObj = new JsonObject();
                        }

                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = id,
                            ["name"] = name,
                            ["input"] = inputObj
                        });
                    }
                }
                AppendMessage("assistant", blocks);
                continue;
            }

            // Default: user message (supports plain text or OpenAI multimodal parts)
            var userBlocks = ConvertContentToBlocks(msg["content"]);
            AppendMessage("user", userBlocks);
        }

        // Anthropic requires the conversation to begin with a user message.
        if (translated.Count == 0 || translated[0].role != "user")
        {
            translated.Insert(0, ("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Begin." } }));
        }

        var messagesArray = new JsonArray();
        foreach (var (role, blocks) in translated)
        {
            messagesArray.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = blocks
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = Math.Max(1, request.MaxTokens),
            ["temperature"] = request.Temperature,
            ["stream"] = request.Stream,
            ["messages"] = messagesArray
        };

        if (systemParts.Count > 0)
        {
            var joinedSystem = string.Join("\n\n", systemParts);
            // Enable Anthropic prompt caching automatically on real agent prompts (>= 2048 chars / ~500+ tok)
            // while keeping short single-line prompts as plain strings for compatibility.
            if (joinedSystem.Length >= 2048)
            {
                body["system"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = joinedSystem,
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                    }
                };
            }
            else
            {
                body["system"] = joinedSystem;
            }
        }

        if (request.Tools != null && request.Tools.Count > 0)
        {
            var anthropicTools = new JsonArray();
            foreach (var t in request.Tools)
            {
                var fn = t?["function"];
                if (fn == null) continue;
                var name = fn["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var schema = fn["parameters"] != null
                    ? JsonNode.Parse(fn["parameters"]!.ToJsonString())
                    : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

                anthropicTools.Add(new JsonObject
                {
                    ["name"] = name,
                    ["description"] = fn["description"]?.GetValue<string>() ?? "",
                    ["input_schema"] = schema
                });
            }
            if (anthropicTools.Count > 0)
            {
                if (anthropicTools[^1] is JsonObject lastTool)
                {
                    lastTool["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                }
                body["tools"] = anthropicTools;
            }
        }

        return body;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildMessagesPayload(request);
        var url = ResolveEndpoint(request.BaseUrl, "messages");
        var pendingStatuses = new Queue<string>();

        HttpRequestMessage BuildHttpRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            var apiKey = SettingsService.ResolveApiKey(request.ApiKey, request.Provider ?? "anthropic", request.BaseUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }
            req.Headers.TryAddWithoutValidation("anthropic-version", DefaultApiVersion);
            return req;
        }

        HttpResponseMessage? response = null;
        string? networkError = null;
        try
        {
            response = await _retry.SendAsync(
                _http,
                BuildHttpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                s => pendingStatuses.Enqueue(s),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Network error calling Anthropic API ({url}): {ex.Message}", ex);
            networkError = $"Network error calling Anthropic API: {ex.Message}";
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

        if (response == null)
        {
            yield return new ChatErrorEvent("Anthropic API returned no response.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errText = await response.Content.ReadAsStringAsync(cancellationToken);
                var statusCode = (int)response.StatusCode;
                LoggingService.LogError($"Anthropic API returned {statusCode} for model '{request.Model}': {errText}");
                yield return new ChatErrorEvent($"Anthropic API Error ({statusCode} {response.StatusCode}): {errText}", statusCode);
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Maps Anthropic content_block index -> dense tool call index for ChatToolCallDelta.
            var blockToToolIndex = new Dictionary<int, int>();
            int nextToolIndex = 0;
            int inputTokens = 0;
            int outputTokens = 0;
            int cachedReadTokens = 0;
            int cacheCreationTokens = 0;
            bool usageSeen = false;

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
                if (string.IsNullOrEmpty(dataStr) || dataStr == "[DONE]") continue;

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(dataStr);
                }
                catch (Exception ex)
                {
                    LoggingService.Swallowed(ex, "AnthropicProvider.ParseSseChunk");
                    continue;
                }

                var eventType = node?["type"]?.GetValue<string>() ?? "";
                switch (eventType)
                {
                    case "message_start":
                    {
                        var usage = node?["message"]?["usage"];
                        if (usage != null)
                        {
                            inputTokens = usage["input_tokens"]?.GetValue<int>() ?? inputTokens;
                            outputTokens = usage["output_tokens"]?.GetValue<int>() ?? outputTokens;
                            cachedReadTokens = usage["cache_read_input_tokens"]?.GetValue<int>() ?? cachedReadTokens;
                            cacheCreationTokens = usage["cache_creation_input_tokens"]?.GetValue<int>() ?? cacheCreationTokens;
                            usageSeen = true;
                        }
                        break;
                    }
                    case "content_block_start":
                    {
                        var blockIdx = node?["index"]?.GetValue<int>() ?? 0;
                        var block = node?["content_block"];
                        var blockType = block?["type"]?.GetValue<string>() ?? "";
                        if (blockType == "tool_use")
                        {
                            var toolIdx = nextToolIndex++;
                            blockToToolIndex[blockIdx] = toolIdx;
                            var id = block?["id"]?.GetValue<string>() ?? $"call_{Guid.NewGuid():N}";
                            var name = block?["name"]?.GetValue<string>() ?? "";
                            yield return new ChatToolCallDelta(toolIdx, id, name, null);
                        }
                        else if (blockType == "text")
                        {
                            var initialText = block?["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(initialText)) yield return new ChatContentDelta(initialText);
                        }
                        break;
                    }
                    case "content_block_delta":
                    {
                        var blockIdx = node?["index"]?.GetValue<int>() ?? 0;
                        var delta = node?["delta"];
                        var deltaType = delta?["type"]?.GetValue<string>() ?? "";
                        if (deltaType == "text_delta")
                        {
                            var text = delta?["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(text)) yield return new ChatContentDelta(text);
                        }
                        else if (deltaType == "thinking_delta")
                        {
                            var thinking = delta?["thinking"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(thinking)) yield return new ChatReasoningDelta(thinking);
                        }
                        else if (deltaType == "input_json_delta" && blockToToolIndex.TryGetValue(blockIdx, out var toolIdx))
                        {
                            var partial = delta?["partial_json"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(partial)) yield return new ChatToolCallDelta(toolIdx, null, null, partial);
                        }
                        break;
                    }
                    case "message_delta":
                    {
                        var usage = node?["usage"];
                        if (usage != null)
                        {
                            outputTokens = usage["output_tokens"]?.GetValue<int>() ?? outputTokens;
                            usageSeen = true;
                        }
                        break;
                    }
                    case "error":
                    {
                        var errMsg = node?["error"]?["message"]?.GetValue<string>() ?? dataStr;
                        yield return new ChatErrorEvent($"Anthropic stream error: {errMsg}");
                        yield break;
                    }
                }
            }

            if (usageSeen && (inputTokens > 0 || outputTokens > 0 || cachedReadTokens > 0))
            {
                yield return new ChatUsageEvent(inputTokens, outputTokens, cachedReadTokens, cacheCreationTokens);
            }
        }
    }

    public async Task<string> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var nonStream = new ChatRequest
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
        await foreach (var evt in StreamAsync(nonStream, cancellationToken))
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
            var url = ResolveEndpoint(settings.BaseUrl, "models");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var apiKey = SettingsService.ResolveApiKey(settings.ApiKey, settings.Provider ?? "anthropic", settings.BaseUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }
            req.Headers.TryAddWithoutValidation("anthropic-version", DefaultApiVersion);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)resp.StatusCode}: {body}", models);
            }

            var node = JsonNode.Parse(body);
            if (node?["data"] is JsonArray dataArr)
            {
                foreach (var item in dataArr)
                {
                    var id = item?["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id)) models.Add(id);
                }
            }

            return (true, $"Connected to Anthropic! Found {models.Count} models.", models.Take(120).ToList());
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
            if (root == null) return events;

            var usage = root["usage"];
            if (usage != null)
            {
                var pt = usage["input_tokens"]?.GetValue<int>() ?? 0;
                var ct = usage["output_tokens"]?.GetValue<int>() ?? 0;
                if (pt > 0 || ct > 0) events.Add(new ChatUsageEvent(pt, ct));
            }

            if (root["content"] is JsonArray blocks)
            {
                int toolIdx = 0;
                foreach (var block in blocks)
                {
                    var type = block?["type"]?.GetValue<string>() ?? "";
                    if (type == "text")
                    {
                        var text = block?["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text)) events.Add(new ChatContentDelta(text));
                    }
                    else if (type == "thinking")
                    {
                        var thinking = block?["thinking"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(thinking)) events.Add(new ChatReasoningDelta(thinking));
                    }
                    else if (type == "tool_use")
                    {
                        var id = block?["id"]?.GetValue<string>() ?? $"call_{Guid.NewGuid():N}";
                        var name = block?["name"]?.GetValue<string>() ?? "";
                        var inputJson = block?["input"]?.ToJsonString() ?? "{}";
                        events.Add(new ChatToolCallDelta(toolIdx++, id, name, inputJson));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "AnthropicProvider.ParseNonStreamJson");
        }
        return events;
    }

    private static string ExtractPlainText(JsonNode? content)
    {
        if (content is JsonValue val && val.TryGetValue<string>(out var s)) return s ?? string.Empty;
        if (content is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var part in arr)
            {
                var text = part?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text);
                }
            }
            return sb.ToString();
        }
        return content?.ToJsonString() ?? string.Empty;
    }

    private static JsonArray ConvertContentToBlocks(JsonNode? content)
    {
        var blocks = new JsonArray();
        if (content is JsonValue val && val.TryGetValue<string>(out var text))
        {
            if (!string.IsNullOrEmpty(text))
            {
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }
            return blocks;
        }

        if (content is JsonArray parts)
        {
            foreach (var part in parts)
            {
                var type = part?["type"]?.GetValue<string>() ?? "";
                if (type == "text")
                {
                    var t = part?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(t))
                    {
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = t });
                    }
                }
                else if (type == "image_url")
                {
                    var url = part?["image_url"]?["url"]?.GetValue<string>() ?? "";
                    if (TryParseDataUri(url, out var mediaType, out var base64Data))
                    {
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = mediaType,
                                ["data"] = base64Data
                            }
                        });
                    }
                }
            }
        }

        return blocks;
    }

    private static bool TryParseDataUri(string uri, out string mediaType, out string base64Data)
    {
        mediaType = "image/png";
        base64Data = "";
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        var comma = uri.IndexOf(',');
        if (comma <= 5) return false;
        var header = uri.Substring(5, comma - 5);
        var semi = header.IndexOf(';');
        if (semi > 0) mediaType = header.Substring(0, semi);
        base64Data = uri.Substring(comma + 1);
        return !string.IsNullOrEmpty(base64Data);
    }
}

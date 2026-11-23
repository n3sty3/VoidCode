using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Providers;
using VoidCode.Agent.Tools;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

public class ProviderContractTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, int, HttpResponseMessage> _responder;
        public List<(Uri? Uri, Dictionary<string, string> Headers, string Body)> Recorded { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            Recorded.Add((request.RequestUri, headers, body));
            return _responder(request, body, Recorded.Count);
        }
    }

    [Fact]
    public async Task OpenAiProvider_StreamsContentReasoningToolCallsAndUsage()
    {
        var sse = string.Join("\n", new[]
        {
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"plan\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"world\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\"}}]}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.cs\\\"}\"}}]}}]}",
            "data: {\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":45}}",
            "data: [DONE]"
        });

        var handler = new RecordingHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });

        using var http = new HttpClient(handler);
        var cache = new ProviderCapabilityCache();
        var provider = new OpenAiCompatibleProvider(http, new RetryPolicy(0), cache);

        var req = new ChatRequest
        {
            Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            ApiKey = "sk-test",
            Model = "deepseek/deepseek-chat",
            Messages = new List<JsonObject> { new() { ["role"] = "user", ["content"] = "hi" } },
            Tools = ToolCatalog.Definitions(planOnly: true)
        };

        var events = new List<ChatEvent>();
        await foreach (var evt in provider.StreamAsync(req)) events.Add(evt);

        Assert.Equal("plan", string.Concat(events.OfType<ChatReasoningDelta>().Select(e => e.Text)));
        Assert.Equal("Hello world", string.Concat(events.OfType<ChatContentDelta>().Select(e => e.Text)));

        var toolDeltas = events.OfType<ChatToolCallDelta>().ToList();
        Assert.Equal("call_1", toolDeltas[0].Id);
        Assert.Equal("read_file", toolDeltas[0].Name);
        Assert.Equal("{\"path\":\"a.cs\"}", string.Concat(toolDeltas.Select(d => d.ArgumentsDelta)));

        var usage = Assert.Single(events.OfType<ChatUsageEvent>());
        Assert.Equal(120, usage.PromptTokens);
        Assert.Equal(45, usage.CompletionTokens);
    }

    [Fact]
    public async Task OpenAiProvider_CachesToolsUnsupported_AndOmitsToolsOnNextTurn()
    {
        var handler = new RecordingHandler((_, body, _) =>
        {
            if (body.Contains("\"tools\"", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"tools not supported\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var cache = new ProviderCapabilityCache();
        var provider = new OpenAiCompatibleProvider(http, new RetryPolicy(0), cache);

        ChatRequest MakeReq() => new()
        {
            Provider = "custom",
            BaseUrl = "https://gateway.local/v1",
            Model = "tiny-model",
            Messages = new List<JsonObject> { new() { ["role"] = "user", ["content"] = "hi" } },
            Tools = ToolCatalog.Definitions(planOnly: true)
        };

        // First call: sends tools -> 400 -> caches ToolsSupported=false -> retries without tools -> succeeds.
        var firstEvents = new List<ChatEvent>();
        await foreach (var evt in provider.StreamAsync(MakeReq())) firstEvents.Add(evt);
        Assert.Equal(2, handler.Recorded.Count);
        Assert.False(cache.Get("custom", "https://gateway.local/v1", "tiny-model").ToolsSupported);
        Assert.Contains(firstEvents.OfType<ChatContentDelta>(), c => c.Text == "ok");

        // Second call: reads cache and never sends tools at all (only 1 HTTP request).
        var secondEvents = new List<ChatEvent>();
        await foreach (var evt in provider.StreamAsync(MakeReq())) secondEvents.Add(evt);
        Assert.Equal(3, handler.Recorded.Count);
        Assert.DoesNotContain("\"tools\"", handler.Recorded[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicProvider_TranslatesMessagesToolsImages_AndParsesSse()
    {
        var sse = string.Join("\n", new[]
        {
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":210,\"output_tokens\":0}}}",
            "event: content_block_start",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"checking file\"}}",
            "event: content_block_start",
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Done.\"}}",
            "event: content_block_start",
            "data: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_99\",\"name\":\"read_file\"}}",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"Program.cs\\\"}\"}}",
            "event: message_delta",
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":33}}",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}"
        });

        var handler = new RecordingHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });

        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, new RetryPolicy(0));

        var req = new ChatRequest
        {
            Provider = "anthropic",
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "sk-ant-test",
            Model = "claude-sonnet-4-20250514",
            Tools = ToolCatalog.Definitions(planOnly: true),
            Messages = new List<JsonObject>
            {
                new() { ["role"] = "system", ["content"] = "SYSTEM RULES" },
                new()
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "Look at this screenshot" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" }
                        }
                    }
                },
                new()
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "toolu_1",
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{\"path\":\"a.cs\"}" }
                        }
                    }
                },
                new() { ["role"] = "tool", ["tool_call_id"] = "toolu_1", ["content"] = "class A {}" }
            }
        };

        var events = new List<ChatEvent>();
        await foreach (var evt in provider.StreamAsync(req)) events.Add(evt);

        // Verify request wire format
        Assert.Single(handler.Recorded);
        var sent = handler.Recorded[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri?.ToString());
        Assert.Equal("sk-ant-test", sent.Headers["x-api-key"]);
        Assert.Equal(AnthropicProvider.DefaultApiVersion, sent.Headers["anthropic-version"]);

        var payload = JsonNode.Parse(sent.Body)!.AsObject();
        Assert.Equal("SYSTEM RULES", payload["system"]?.GetValue<string>());
        Assert.True(payload["tools"] is JsonArray tools && tools.Count > 0 && tools[0]?["input_schema"] != null);

        var msgs = payload["messages"]!.AsArray();
        Assert.Equal(3, msgs.Count); // user (with image), assistant (tool_use), user (tool_result)
        Assert.Equal("image", msgs[0]?["content"]?[1]?["type"]?.GetValue<string>());
        Assert.Equal("tool_use", msgs[1]?["content"]?[0]?["type"]?.GetValue<string>());
        Assert.Equal("tool_result", msgs[2]?["content"]?[0]?["type"]?.GetValue<string>());

        // Verify parsed events
        Assert.Equal("checking file", string.Concat(events.OfType<ChatReasoningDelta>().Select(e => e.Text)));
        Assert.Equal("Done.", string.Concat(events.OfType<ChatContentDelta>().Select(e => e.Text)));
        var tcs = events.OfType<ChatToolCallDelta>().ToList();
        Assert.Equal("toolu_99", tcs[0].Id);
        Assert.Equal("read_file", tcs[0].Name);
        Assert.Equal("{\"path\":\"Program.cs\"}", string.Concat(tcs.Select(t => t.ArgumentsDelta)));
        var usage = Assert.Single(events.OfType<ChatUsageEvent>());
        Assert.Equal(210, usage.PromptTokens);
        Assert.Equal(33, usage.CompletionTokens);
    }

    [Fact]
    public async Task AnthropicProvider_TestConnection_UsesAnthropicHeadersAndParsesModels()
    {
        var handler = new RecordingHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"claude-sonnet-4-20250514\"}]}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, new RetryPolicy(0));

        var (ok, _, models) = await provider.TestConnectionAsync(new AppSettings
        {
            Provider = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            ApiKey = "sk-ant-123"
        });

        Assert.True(ok);
        Assert.Contains("claude-sonnet-4-20250514", models);
        Assert.Equal("sk-ant-123", handler.Recorded[0].Headers["x-api-key"]);
    }

    [Fact]
    public void ProviderFactory_SelectsAnthropicByProviderOrUrl()
    {
        Assert.IsType<AnthropicProvider>(ProviderFactory.Create(new AppSettings { Provider = "anthropic", BaseUrl = "https://proxy.internal" }));
        Assert.IsType<AnthropicProvider>(ProviderFactory.Create(new AppSettings { Provider = "custom", BaseUrl = "https://api.anthropic.com/v1" }));
        Assert.IsType<OpenAiCompatibleProvider>(ProviderFactory.Create(new AppSettings { Provider = "openrouter", BaseUrl = "https://openrouter.ai/api/v1" }));
        Assert.IsType<OpenAiCompatibleProvider>(ProviderFactory.Create(new AppSettings { Provider = "gemini", BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai" }));
        var geminiCaps = ProviderCapabilityCache.Shared.Get("gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-pro");
        Assert.Equal(200_000, geminiCaps.ContextWindow);
        Assert.True(geminiCaps.StreamUsageSupported);
    }

    [Fact]
    public void GeminiBaseUrl_IsNormalizedToOpenAiEndpoint()
    {
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai",
            OpenAiCompatibleProvider.NormalizeBaseUrl("gemini", ""));
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai",
            OpenAiCompatibleProvider.NormalizeBaseUrl("gemini", "https://generativelanguage.googleapis.com"));
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai",
            OpenAiCompatibleProvider.NormalizeBaseUrl("custom", "https://generativelanguage.googleapis.com/v1beta/"));
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai",
            OpenAiCompatibleProvider.NormalizeBaseUrl("gemini", "https://generativelanguage.googleapis.com/v1beta/openai/"));
    }

    [Fact]
    public void ContextBuilder_ExtractsInlineClipboardImageAndStripsBase64FromPrompt()
    {
        var images = new List<string>();
        var raw = "[Attached Image: data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA]\n\nWhat is in this screenshot?";
        var enriched = VoidCode.Agent.ContextBuilder.EnrichPromptWithReferencedFiles(raw, System.IO.Path.GetTempPath(), images);

        Assert.Single(images);
        Assert.Equal("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA", images[0]);
        Assert.Contains("[Clipboard Screenshot Attached]", enriched);
        Assert.DoesNotContain("iVBORw0KGgoAAAANSUhEUgAAAAUA", enriched);
    }

    [Fact]
    public void ResolveApiKey_PrefersExplicitKeyAndFallsBackToMatchingEnvVar()
    {
        var fakeEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GEMINI_API_KEY"] = "AIza-from-env",
            ["OPENAI_API_KEY"] = "sk-openai-env",
            ["ANTHROPIC_API_KEY"] = "sk-ant-env",
            ["GROQ_API_KEY"] = "gsk-env"
        };
        string? Read(string k) => fakeEnv.TryGetValue(k, out var v) ? v : null;

        Assert.Equal("explicit-key", SettingsService.ResolveApiKey("explicit-key", "gemini", null, Read));
        Assert.Equal("AIza-from-env", SettingsService.ResolveApiKey("", "gemini", null, Read));
        Assert.Equal("AIza-from-env", SettingsService.ResolveApiKey("", "custom", "https://generativelanguage.googleapis.com/v1beta/openai", Read));
        Assert.Equal("sk-openai-env", SettingsService.ResolveApiKey("", "openai", null, Read));
        Assert.Equal("sk-ant-env", SettingsService.ResolveApiKey(null, "anthropic", null, Read));
        Assert.Equal("gsk-env", SettingsService.ResolveApiKey("", "groq", null, Read));
    }

    [Fact]
    public async Task UpdateCheckService_DetectsNewerGitHubReleaseAndSetupExeAsset()
    {
        Assert.True(UpdateCheckService.IsNewerVersion("1.0.0", "v1.1.0"));
        Assert.True(UpdateCheckService.IsNewerVersion("1.0.0", "1.0.1-rc.1"));
        Assert.False(UpdateCheckService.IsNewerVersion("1.0.0", "v1.0.0"));
        Assert.False(UpdateCheckService.IsNewerVersion("1.2.0", "v1.1.9"));

        const string releaseJson = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/VoidCode/VoidCode/releases/tag/v1.2.0",
          "assets": [
            {
              "name": "VoidCode-win-x64.zip",
              "browser_download_url": "https://github.com/VoidCode/VoidCode/releases/download/v1.2.0/VoidCode-win-x64.zip"
            },
            {
              "name": "VoidCode-Setup-1.2.0-win-x64.exe",
              "browser_download_url": "https://github.com/VoidCode/VoidCode/releases/download/v1.2.0/VoidCode-Setup-1.2.0-win-x64.exe"
            }
          ]
        }
        """;

        var handler = new RecordingHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);

        var res = await UpdateCheckService.CheckForUpdatesAsync("VoidCode/VoidCode", silent: false, http: http, currentVersionOverride: "1.0.0");

        Assert.True(res.Checked);
        Assert.True(res.UpdateAvailable);
        Assert.Equal("1.0.0", res.CurrentVersion);
        Assert.Equal("1.2.0", res.LatestVersion);
        Assert.Equal("https://github.com/VoidCode/VoidCode/releases/download/v1.2.0/VoidCode-Setup-1.2.0-win-x64.exe", res.DownloadUrl);
    }
}

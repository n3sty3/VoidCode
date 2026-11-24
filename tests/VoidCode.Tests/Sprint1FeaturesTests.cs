using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Agent.Providers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

public class Sprint1FeaturesTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    [Fact]
    public void CostEstimator_ComputesAnthropicPromptCacheSavings()
    {
        // Claude Sonnet 4: $3/M input, $15/M output, 10% rate on cache read (90% savings)
        var est = CostEstimator.Estimate(
            provider: "anthropic",
            baseUrl: "https://api.anthropic.com/v1",
            model: "claude-sonnet-4-20250514",
            promptTokens: 2000,
            completionTokens: 1000,
            cachedInputTokens: 20_000,
            cacheCreationTokens: 0);

        Assert.False(est.IsLocal);
        Assert.True(est.EstimatedCostUsd > 0.02);
        Assert.True(est.CacheSavingsUsd > 0.05);
        Assert.StartsWith("$", est.FormattedCost);
        Assert.StartsWith("$", est.FormattedSavings);
    }

    [Fact]
    public void CostEstimator_FlagsLocalOllamaAsZeroCost()
    {
        var est = CostEstimator.Estimate("ollama", "http://localhost:11434/v1", "qwen2.5-coder:7b", 5000, 1200);
        Assert.True(est.IsLocal);
        Assert.Equal(0.0, est.EstimatedCostUsd);
        Assert.Equal("$0 (Local)", est.FormattedCost);
    }

    [Fact]
    public void AnthropicProvider_EnablesEphemeralPromptCachingForFullSystemPrompts()
    {
        var longSystem = new string('A', 2400);
        var req = new ChatRequest
        {
            Provider = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-sonnet-4-20250514",
            Messages = new List<JsonObject>
            {
                new() { ["role"] = "system", ["content"] = longSystem },
                new() { ["role"] = "user", ["content"] = "Hello" }
            },
            Tools = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "read_file",
                        ["description"] = "Read a file",
                        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    }
                }
            }
        };

        var payload = AnthropicProvider.BuildMessagesPayload(req);
        var sysArr = Assert.IsType<JsonArray>(payload["system"]);
        Assert.Equal("ephemeral", sysArr[0]?["cache_control"]?["type"]?.GetValue<string>());

        var toolsArr = Assert.IsType<JsonArray>(payload["tools"]);
        Assert.Equal("ephemeral", toolsArr[^1]?["cache_control"]?["type"]?.GetValue<string>());
    }

    [Fact]
    public void AutocompleteService_SanitizesFencesAndPrefixOverlap()
    {
        var raw = "```csharp\nreturn x + y;\n```";
        var cleaned = AutocompleteService.SanitizeCompletion(raw, "public int Add(int x, int y) {\n    ", "\n}");
        Assert.Equal("return x + y;", cleaned);

        var echoed = AutocompleteService.SanitizeCompletion("Console.WriteLine(\"hi\");", "Console.", "");
        Assert.Equal("WriteLine(\"hi\");", echoed);
    }

    [Fact]
    public void AutocompleteService_ResolvesDedicatedFastLocalModelOverride()
    {
        var mainSettings = new AppSettings
        {
            Provider = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-sonnet-4",
            AutocompleteBaseUrl = "http://localhost:11434/v1",
            AutocompleteModel = "qwen2.5-coder:3b"
        };

        var effective = AutocompleteService.ResolveEffectiveSettings(mainSettings);
        Assert.Equal("ollama", effective.Provider);
        Assert.Equal("http://localhost:11434/v1", effective.BaseUrl);
        Assert.Equal("qwen2.5-coder:3b", effective.Model);
    }

    [Fact]
    public async Task LocalModelDiscovery_ParsesOllamaAndLmStudioResponses()
    {
        using var http = new HttpClient(new StubHandler(req =>
        {
            var uri = req.RequestUri!.AbsoluteUri;
            if (uri.Contains("11434"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"models\":[{\"name\":\"qwen2.5-coder:3b\"},{\"name\":\"llama3.1:8b\"}]}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"deepseek-coder-v2\"}]}", Encoding.UTF8, "application/json")
            };
        }));

        var report = await LocalModelDiscoveryService.DiscoverAsync(http);
        Assert.True(report.OllamaRunning);
        Assert.True(report.LmStudioRunning);
        Assert.Equal(3, report.Models.Count);
        Assert.Contains(report.Models, m => m.Provider == "ollama" && m.Model == "qwen2.5-coder:3b" && m.IsFastCoder);
        Assert.Contains(report.Models, m => m.Provider == "lmstudio" && m.Model == "deepseek-coder-v2" && m.IsFastCoder);
    }
}

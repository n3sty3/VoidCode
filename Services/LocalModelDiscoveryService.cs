using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

public sealed class DiscoveredLocalModel
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("isFastCoder")]
    public bool IsFastCoder { get; init; }
}

public sealed class LocalDiscoveryReport
{
    [JsonPropertyName("ollamaRunning")]
    public bool OllamaRunning { get; init; }

    [JsonPropertyName("lmStudioRunning")]
    public bool LmStudioRunning { get; init; }

    [JsonPropertyName("models")]
    public List<DiscoveredLocalModel> Models { get; init; } = new();

    [JsonPropertyName("detectedEnvProviders")]
    public List<string> DetectedEnvProviders { get; init; } = new();
}

/// <summary>
/// Fast, non-blocking local LLM discovery for Ollama (11434) and LM Studio (1234),
/// plus zero-config environment variable provider detection (without ever returning key values).
/// </summary>
public static class LocalModelDiscoveryService
{
    private static readonly HttpClient ProbeClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(850)
    };

    private static readonly (string provider, string[] envVars)[] CloudEnvProviders =
    {
        ("gemini", new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" }),
        ("anthropic", new[] { "ANTHROPIC_API_KEY" }),
        ("openai", new[] { "OPENAI_API_KEY" }),
        ("openrouter", new[] { "OPENROUTER_API_KEY" }),
        ("groq", new[] { "GROQ_API_KEY" }),
        ("xai", new[] { "XAI_API_KEY" }),
        ("deepseek", new[] { "DEEPSEEK_API_KEY" })
    };

    public static async Task<LocalDiscoveryReport> DiscoverAsync(
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        var client = http ?? ProbeClient;
        var ollamaTask = ProbeOllamaAsync(client, cancellationToken);
        var lmStudioTask = ProbeLmStudioAsync(client, cancellationToken);

        await Task.WhenAll(ollamaTask, lmStudioTask);
        var (ollamaOk, ollamaModels) = ollamaTask.Result;
        var (lmOk, lmModels) = lmStudioTask.Result;

        var models = new List<DiscoveredLocalModel>(ollamaModels.Count + lmModels.Count);
        models.AddRange(ollamaModels);
        models.AddRange(lmModels);

        var envProviders = new List<string>();
        foreach (var (provider, vars) in CloudEnvProviders)
        {
            foreach (var v in vars)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                {
                    envProviders.Add(provider);
                    break;
                }
            }
        }

        return new LocalDiscoveryReport
        {
            OllamaRunning = ollamaOk,
            LmStudioRunning = lmOk,
            Models = models,
            DetectedEnvProviders = envProviders
        };
    }

    internal static bool IsFastCodingModel(string modelName)
    {
        var m = (modelName ?? "").ToLowerInvariant();
        return m.Contains("coder", StringComparison.Ordinal)
            || m.Contains("codellama", StringComparison.Ordinal)
            || m.Contains("starcoder", StringComparison.Ordinal)
            || m.Contains("codestral", StringComparison.Ordinal)
            || m.Contains("qwen2.5", StringComparison.Ordinal)
            || m.Contains("qwen3", StringComparison.Ordinal)
            || m.Contains(":1.5b", StringComparison.Ordinal)
            || m.Contains(":3b", StringComparison.Ordinal)
            || m.Contains(":7b", StringComparison.Ordinal);
    }

    private static async Task<(bool running, List<DiscoveredLocalModel> models)> ProbeOllamaAsync(
        HttpClient client,
        CancellationToken ct)
    {
        var list = new List<DiscoveredLocalModel>();
        try
        {
            using var resp = await client.GetAsync("http://localhost:11434/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return (false, list);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(body);
            if (root?["models"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var name = item?["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new DiscoveredLocalModel
                    {
                        Provider = "ollama",
                        BaseUrl = "http://localhost:11434/v1",
                        Model = name,
                        IsFastCoder = IsFastCodingModel(name)
                    });
                }
            }
            return (true, list);
        }
        catch (Exception)
        {
            return (false, list);
        }
    }

    private static async Task<(bool running, List<DiscoveredLocalModel> models)> ProbeLmStudioAsync(
        HttpClient client,
        CancellationToken ct)
    {
        var list = new List<DiscoveredLocalModel>();
        try
        {
            using var resp = await client.GetAsync("http://localhost:1234/v1/models", ct);
            if (!resp.IsSuccessStatusCode) return (false, list);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(body);
            if (root?["data"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var id = item?["id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    list.Add(new DiscoveredLocalModel
                    {
                        Provider = "lmstudio",
                        BaseUrl = "http://localhost:1234/v1",
                        Model = id,
                        IsFastCoder = IsFastCodingModel(id)
                    });
                }
            }
            return (true, list);
        }
        catch (Exception)
        {
            return (false, list);
        }
    }
}

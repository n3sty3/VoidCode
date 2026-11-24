using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Agent.Providers;

/// <summary>
/// Unified streaming/completion contract across LLM providers plus a per-model
/// Capability cache so rejected options (tools, stream_options) are remembered instead of retried
/// On every turn.
/// </summary>
internal sealed class ChatRequest
{
    internal required string Provider { get; init; }
    internal required string BaseUrl { get; init; }
    internal string ApiKey { get; init; } = "";
    internal required string Model { get; init; }
    internal required List<JsonObject> Messages { get; init; }
    internal JsonArray? Tools { get; init; }
    internal double Temperature { get; init; } = 0.2;
    internal int MaxTokens { get; init; } = 8192;
    internal bool Stream { get; init; } = true;
}

internal abstract record ChatEvent;
internal sealed record ChatContentDelta(string Text) : ChatEvent;
internal sealed record ChatReasoningDelta(string Text) : ChatEvent;
internal sealed record ChatToolCallDelta(
    int Index,
    string? Id,
    string? Name,
    string? ArgumentsDelta,
    string? ExtraContentJson = null) : ChatEvent;
internal sealed record ChatUsageEvent(
    int PromptTokens,
    int CompletionTokens,
    int CachedInputTokens = 0,
    int CacheCreationTokens = 0) : ChatEvent;
internal sealed record ChatStatusEvent(string Status) : ChatEvent;
internal sealed record ChatErrorEvent(string Message, int? StatusCode = null) : ChatEvent;

internal sealed class ProviderCapabilities
{
    internal bool ToolsSupported { get; set; } = true;
    internal bool StreamUsageSupported { get; set; } = true;
    internal int ContextWindow { get; set; } = 32768;
}

internal sealed class ProviderCapabilityCache
{
    internal static readonly ProviderCapabilityCache Shared = new();

    private readonly ConcurrentDictionary<string, ProviderCapabilities> _entries = new(StringComparer.OrdinalIgnoreCase);

    internal static string BuildKey(string provider, string baseUrl, string model)
        => $"{provider.Trim()}|{baseUrl.Trim().TrimEnd('/')}|{model.Trim()}";

    internal ProviderCapabilities Get(string provider, string baseUrl, string model)
    {
        var key = BuildKey(provider, baseUrl, model);
        return _entries.GetOrAdd(key, _ => new ProviderCapabilities
        {
            ToolsSupported = true,
            StreamUsageSupported = SupportsStreamUsageByDefault(provider, baseUrl),
            ContextWindow = InferContextWindow(model, provider)
        });
    }

    internal ProviderCapabilities Get(AppSettings settings)
        => Get(settings.Provider ?? "", settings.BaseUrl ?? "", settings.Model ?? "");

    internal void MarkToolsUnsupported(string provider, string baseUrl, string model)
    {
        Get(provider, baseUrl, model).ToolsSupported = false;
    }

    internal void MarkStreamUsageUnsupported(string provider, string baseUrl, string model)
    {
        Get(provider, baseUrl, model).StreamUsageSupported = false;
    }

    internal void Clear() => _entries.Clear();

    private static bool SupportsStreamUsageByDefault(string provider, string baseUrl)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        var u = (baseUrl ?? "").Trim().ToLowerInvariant();
        return p is "openrouter" or "openai" or "deepseek" or "anthropic" or "gemini" or "xai" or "groq"
            || u.Contains("openrouter.ai", StringComparison.Ordinal)
            || u.Contains("api.openai.com", StringComparison.Ordinal)
            || u.Contains("api.deepseek.com", StringComparison.Ordinal)
            || u.Contains("api.anthropic.com", StringComparison.Ordinal)
            || u.Contains("generativelanguage.googleapis.com", StringComparison.Ordinal)
            || u.Contains("api.x.ai", StringComparison.Ordinal)
            || u.Contains("api.groq.com", StringComparison.Ordinal);
    }

    internal static int InferContextWindow(string? model, string? provider)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        var p = (provider ?? "").Trim().ToLowerInvariant();

        if (m.Contains("claude", StringComparison.Ordinal) || p == "anthropic")
            return 200_000;
        if (m.Contains("gemini", StringComparison.Ordinal) || p == "gemini")
            return 200_000;
        if (m.Contains("grok", StringComparison.Ordinal) || p == "xai")
            return 128_000;
        if (m.Contains("gpt-4.1", StringComparison.Ordinal) || m.Contains("gpt-5", StringComparison.Ordinal))
            return 200_000;
        if (m.Contains("gpt-4o", StringComparison.Ordinal) || m.Contains("gpt-4-turbo", StringComparison.Ordinal) || m.Contains("o1", StringComparison.Ordinal) || m.Contains("o3", StringComparison.Ordinal) || m.Contains("o4", StringComparison.Ordinal))
            return 128_000;
        if (m.Contains("deepseek", StringComparison.Ordinal))
            return 64_000;
        if (m.Contains("qwen-2.5", StringComparison.Ordinal) || m.Contains("qwen2.5", StringComparison.Ordinal) || m.Contains("qwen3", StringComparison.Ordinal))
            return p is "ollama" or "lmstudio" ? 32_768 : 128_000;
        if (m.Contains("llama-3", StringComparison.Ordinal) || m.Contains("llama3", StringComparison.Ordinal))
            return p is "ollama" or "lmstudio" ? 32_768 : 128_000;
        if (p is "ollama" or "lmstudio")
            return 32_768;

        return 64_000;
    }
}

internal interface IChatProvider
{
    string Name { get; }

    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);

    Task<string> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);

    Task<(bool success, string message, List<string> models)> TestConnectionAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

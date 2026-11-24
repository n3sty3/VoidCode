using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Providers;

namespace VoidCode.Services;

public sealed class InlineCompleteRequest
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("prefix")]
    public string? Prefix { get; init; }

    [JsonPropertyName("suffix")]
    public string? Suffix { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("column")]
    public int Column { get; init; }
}

public sealed class InlineCompleteResponse
{
    [JsonPropertyName("completion")]
    public string Completion { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("cached")]
    public bool Cached { get; init; }

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; init; }
}

/// <summary>
/// Fast Fill-in-the-Middle (FIM) inline ghost-text autocomplete engine for Monaco.
/// Supports a dedicated low-latency/local autocomplete model slot (e.g., Ollama qwen2.5-coder:3b,
/// Groq, or Gemini Flash) independent from the main VoidAgent model, with in-memory LRU caching.
/// </summary>
public static class AutocompleteService
{
    private const int MaxPrefixChars = 3200;
    private const int MaxSuffixChars = 1000;
    private const int MaxCacheEntries = 64;

    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _cacheOrder = new();

    private const string FimSystemPrompt =
        "You are VoidCode Tab Autocomplete, an ultra-fast Fill-in-the-Middle (FIM) code completion engine.\n" +
        "Return ONLY the exact raw code that should be inserted at <CURSOR/>.\n" +
        "Rules:\n" +
        "- Output raw code ONLY — never wrap in markdown fences (```), never explain, never repeat text already in <PREFIX> or <SUFFIX>.\n" +
        "- Prefer concise, syntactically complete 1 to 6 line continuations matching the file's indentation and style.\n" +
        "- If no sensible continuation exists, return an empty string.";

    public static AppSettings ResolveEffectiveSettings(AppSettings baseSettings)
    {
        var customModel = (baseSettings.AutocompleteModel ?? "").Trim();
        var customBaseUrl = (baseSettings.AutocompleteBaseUrl ?? "").Trim();

        if (string.IsNullOrEmpty(customModel) && string.IsNullOrEmpty(customBaseUrl))
        {
            return baseSettings;
        }

        var effectiveBaseUrl = !string.IsNullOrEmpty(customBaseUrl) ? customBaseUrl : baseSettings.BaseUrl;
        var effectiveProvider = InferProviderFromUrl(effectiveBaseUrl, baseSettings.Provider);

        return new AppSettings
        {
            Provider = effectiveProvider,
            BaseUrl = effectiveBaseUrl,
            ApiKey = string.Equals(effectiveProvider, baseSettings.Provider, StringComparison.OrdinalIgnoreCase)
                ? baseSettings.ApiKey
                : SettingsService.ResolveApiKey("", effectiveProvider, effectiveBaseUrl),
            Model = !string.IsNullOrEmpty(customModel) ? customModel : baseSettings.Model,
            Temperature = 0.05,
            MaxTokens = 160
        };
    }

    public static async Task<InlineCompleteResponse> CompleteAsync(
        InlineCompleteRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!settings.AutocompleteEnabled)
        {
            return new InlineCompleteResponse();
        }

        var effective = ResolveEffectiveSettings(settings);
        if (string.IsNullOrWhiteSpace(effective.BaseUrl) || string.IsNullOrWhiteSpace(effective.Model))
        {
            return new InlineCompleteResponse();
        }

        var isLocal = VoidCode.Agent.CostEstimator.IsLocalEndpoint(effective.Provider, effective.BaseUrl);
        var resolvedKey = SettingsService.ResolveApiKey(effective.ApiKey, effective.Provider, effective.BaseUrl);
        if (!isLocal && string.IsNullOrWhiteSpace(resolvedKey))
        {
            return new InlineCompleteResponse();
        }

        var rawPrefix = request.Prefix ?? "";
        var rawSuffix = request.Suffix ?? "";
        if (string.IsNullOrWhiteSpace(rawPrefix))
        {
            return new InlineCompleteResponse();
        }

        var prefix = rawPrefix.Length > MaxPrefixChars ? rawPrefix[^MaxPrefixChars..] : rawPrefix;
        var suffix = rawSuffix.Length > MaxSuffixChars ? rawSuffix[..MaxSuffixChars] : rawSuffix;

        var cacheKey = BuildCacheKey(effective.Model, request.Path ?? "", prefix, suffix);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return new InlineCompleteResponse
            {
                Completion = cached,
                Model = effective.Model,
                Cached = true,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }

        var lang = string.IsNullOrWhiteSpace(request.Language) ? "plaintext" : request.Language;
        var fileLabel = string.IsNullOrWhiteSpace(request.Path) ? "untitled" : request.Path;

        var userPrompt =
            $"File: {fileLabel} ({lang})\n" +
            $"<PREFIX>\n{prefix}\n</PREFIX><CURSOR/><SUFFIX>\n{suffix}\n</SUFFIX>";

        var chatReq = new ChatRequest
        {
            Provider = effective.Provider,
            BaseUrl = effective.BaseUrl,
            ApiKey = resolvedKey,
            Model = effective.Model,
            Temperature = 0.05,
            MaxTokens = 160,
            Stream = false,
            Messages = new List<JsonObject>
            {
                new() { ["role"] = "system", ["content"] = FimSystemPrompt },
                new() { ["role"] = "user", ["content"] = userPrompt }
            }
        };

        try
        {
            var provider = ProviderFactory.Create(effective);
            var raw = await provider.CompleteAsync(chatReq, cancellationToken);
            var cleaned = SanitizeCompletion(raw, prefix, suffix);
            if (!string.IsNullOrEmpty(cleaned))
            {
                RememberCache(cacheKey, cleaned);
            }

            return new InlineCompleteResponse
            {
                Completion = cleaned,
                Model = effective.Model,
                Cached = false,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            return new InlineCompleteResponse { Model = effective.Model, LatencyMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "AutocompleteService.CompleteAsync");
            return new InlineCompleteResponse { Model = effective.Model, LatencyMs = sw.ElapsedMilliseconds };
        }
    }

    internal static string SanitizeCompletion(string? raw, string prefix, string suffix)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var text = raw.Replace("\r\n", "\n");

        // Strip accidental fenced code blocks (```lang ... ```)
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3];
            }
            text = trimmed.TrimEnd('\n');
        }

        // Remove accidental <CURSOR/> echo
        text = text.Replace("<CURSOR/>", "", StringComparison.Ordinal);

        // Trim duplicated tail of current line prefix if model repeated the partial identifier
        var lastLineStart = prefix.LastIndexOf('\n');
        var currentLinePrefix = lastLineStart >= 0 ? prefix[(lastLineStart + 1)..] : prefix;
        if (!string.IsNullOrEmpty(currentLinePrefix) && text.StartsWith(currentLinePrefix, StringComparison.Ordinal))
        {
            text = text[currentLinePrefix.Length..];
        }

        // Trim duplicated suffix head if model echoed the closing token right after cursor
        var suffixFirstLineEnd = suffix.IndexOf('\n');
        var suffixHead = (suffixFirstLineEnd >= 0 ? suffix[..suffixFirstLineEnd] : suffix).Trim();
        if (suffixHead.Length >= 2 && text.TrimEnd().EndsWith(suffixHead, StringComparison.Ordinal))
        {
            var idx = text.LastIndexOf(suffixHead, StringComparison.Ordinal);
            if (idx > 0) text = text[..idx].TrimEnd(' ', '\t');
        }

        return string.IsNullOrWhiteSpace(text) ? "" : text;
    }

    private static string BuildCacheKey(string model, string path, string prefix, string suffix)
    {
        var pTail = prefix.Length > 400 ? prefix[^400..] : prefix;
        var sHead = suffix.Length > 160 ? suffix[..160] : suffix;
        return $"{model}|{path}|{pTail}|{sHead}";
    }

    private static void RememberCache(string key, string value)
    {
        if (_cache.TryAdd(key, value))
        {
            _cacheOrder.Enqueue(key);
            while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var oldest))
            {
                _cache.TryRemove(oldest, out _);
            }
        }
    }

    private static string InferProviderFromUrl(string? baseUrl, string fallbackProvider)
    {
        var u = (baseUrl ?? "").Trim().ToLowerInvariant();
        if (u.Contains(":11434", StringComparison.Ordinal)) return "ollama";
        if (u.Contains(":1234", StringComparison.Ordinal)) return "lmstudio";
        if (u.Contains("api.anthropic.com", StringComparison.Ordinal)) return "anthropic";
        if (u.Contains("generativelanguage.googleapis.com", StringComparison.Ordinal)) return "gemini";
        if (u.Contains("api.groq.com", StringComparison.Ordinal)) return "groq";
        if (u.Contains("api.openai.com", StringComparison.Ordinal)) return "openai";
        if (u.Contains("openrouter.ai", StringComparison.Ordinal)) return "openrouter";
        return fallbackProvider;
    }
}

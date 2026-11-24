using System;
using System.Globalization;

namespace VoidCode.Agent;

/// <summary>
/// Estimates per-turn USD API cost and prompt-cache savings for BYOK cloud models,
/// and flags local models (Ollama / LM Studio / localhost) as $0.00 Local.
/// </summary>
public sealed record CostEstimate(
    bool IsLocal,
    double EstimatedCostUsd,
    double CacheSavingsUsd,
    int CachedInputTokens,
    string FormattedCost,
    string FormattedSavings);

public static class CostEstimator
{
    // USD per 1,000,000 tokens: (InputPerMillion, OutputPerMillion, CachedInputMultiplier)
    private sealed record ModelRates(double InputPerM, double OutputPerM, double CachedMult);

    public static bool IsLocalEndpoint(string? provider, string? baseUrl)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        if (p is "ollama" or "lmstudio") return true;
        var u = (baseUrl ?? "").Trim().ToLowerInvariant();
        return u.Contains("localhost", StringComparison.Ordinal)
            || u.Contains("127.0.0.1", StringComparison.Ordinal)
            || u.Contains("::1", StringComparison.Ordinal);
    }

    public static CostEstimate Estimate(
        string? provider,
        string? baseUrl,
        string? model,
        int promptTokens,
        int completionTokens,
        int cachedInputTokens = 0,
        int cacheCreationTokens = 0)
    {
        int safePrompt = Math.Max(0, promptTokens);
        int safeCompletion = Math.Max(0, completionTokens);
        int safeCached = Math.Max(0, cachedInputTokens);
        int safeCreation = Math.Max(0, cacheCreationTokens);

        if (IsLocalEndpoint(provider, baseUrl))
        {
            return new CostEstimate(true, 0.0, 0.0, safeCached, "$0 (Local)", "");
        }

        var rates = ResolveRates(provider, model);
        if (rates == null)
        {
            return new CostEstimate(false, 0.0, 0.0, safeCached, "", "");
        }

        var isAnthropic = string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase)
            || (model ?? "").Contains("claude", StringComparison.OrdinalIgnoreCase);

        // On Anthropic, input_tokens excludes cache_read_input_tokens & cache_creation_input_tokens.
        // On OpenAI/Gemini/DeepSeek, prompt_tokens includes cached_tokens.
        int uncachedInput = isAnthropic
            ? safePrompt
            : Math.Max(0, safePrompt - safeCached);

        double inputCost = (uncachedInput / 1_000_000.0) * rates.InputPerM;
        double cachedReadCost = (safeCached / 1_000_000.0) * rates.InputPerM * rates.CachedMult;
        double cacheWriteCost = isAnthropic ? (safeCreation / 1_000_000.0) * rates.InputPerM * 1.25 : 0.0;
        double outputCost = (safeCompletion / 1_000_000.0) * rates.OutputPerM;

        double total = Math.Round(inputCost + cachedReadCost + cacheWriteCost + outputCost, 6);
        double fullCachedWithoutDiscount = (safeCached / 1_000_000.0) * rates.InputPerM;
        double savings = Math.Round(Math.Max(0.0, fullCachedWithoutDiscount - cachedReadCost), 6);

        return new CostEstimate(
            IsLocal: false,
            EstimatedCostUsd: total,
            CacheSavingsUsd: savings,
            CachedInputTokens: safeCached,
            FormattedCost: FormatUsd(total),
            FormattedSavings: savings >= 0.0001 ? FormatUsd(savings) : "");
    }

    public static string FormatUsd(double usd)
    {
        if (usd <= 0) return "$0.00";
        if (usd < 0.001) return "<$0.001";
        if (usd < 0.01) return "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture);
        if (usd < 1.0) return "$" + usd.ToString("0.000", CultureInfo.InvariantCulture);
        return "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static ModelRates? ResolveRates(string? provider, string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(m)) return null;

        // Anthropic Claude
        if (m.Contains("claude-opus-4", StringComparison.Ordinal) || m.Contains("claude-3-opus", StringComparison.Ordinal))
            return new ModelRates(15.0, 75.0, 0.10);
        if (m.Contains("claude-sonnet-4", StringComparison.Ordinal) || m.Contains("claude-3-7-sonnet", StringComparison.Ordinal) || m.Contains("claude-3-5-sonnet", StringComparison.Ordinal))
            return new ModelRates(3.0, 15.0, 0.10);
        if (m.Contains("claude", StringComparison.Ordinal) && m.Contains("haiku", StringComparison.Ordinal))
            return new ModelRates(0.80, 4.0, 0.10);

        // Google Gemini
        if (m.Contains("gemini-2.5-pro", StringComparison.Ordinal) || m.Contains("gemini-2.0-pro", StringComparison.Ordinal))
            return new ModelRates(1.25, 10.0, 0.25);
        if (m.Contains("gemini-2.5-flash", StringComparison.Ordinal) || m.Contains("gemini-2.0-flash", StringComparison.Ordinal))
            return new ModelRates(0.15, 0.60, 0.25);

        // OpenAI
        if (m.Contains("gpt-4.1-nano", StringComparison.Ordinal))
            return new ModelRates(0.10, 0.40, 0.25);
        if (m.Contains("gpt-4.1-mini", StringComparison.Ordinal) || m.Contains("gpt-4o-mini", StringComparison.Ordinal))
            return new ModelRates(0.40, 1.60, 0.25);
        if (m.Contains("gpt-4.1", StringComparison.Ordinal))
            return new ModelRates(2.0, 8.0, 0.25);
        if (m.Contains("gpt-4o", StringComparison.Ordinal))
            return new ModelRates(2.50, 10.0, 0.50);
        if (m.Contains("o4-mini", StringComparison.Ordinal) || m.Contains("o3-mini", StringComparison.Ordinal))
            return new ModelRates(1.10, 4.40, 0.25);
        if (m.Contains("o3", StringComparison.Ordinal))
            return new ModelRates(2.0, 8.0, 0.25);

        // DeepSeek
        if (m.Contains("deepseek-r1", StringComparison.Ordinal) || m.Contains("deepseek-reasoner", StringComparison.Ordinal))
            return new ModelRates(0.55, 2.19, 0.25);
        if (m.Contains("deepseek", StringComparison.Ordinal))
            return new ModelRates(0.27, 1.10, 0.25);

        // xAI Grok
        if (m.Contains("grok-3-mini", StringComparison.Ordinal))
            return new ModelRates(0.30, 0.50, 0.25);
        if (m.Contains("grok", StringComparison.Ordinal))
            return new ModelRates(3.0, 15.0, 0.25);

        // Groq hosted open models
        if (string.Equals(provider, "groq", StringComparison.OrdinalIgnoreCase))
            return new ModelRates(0.59, 0.79, 0.50);

        return null;
    }
}

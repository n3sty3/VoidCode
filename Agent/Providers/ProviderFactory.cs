using System;
using VoidCode.Services;

namespace VoidCode.Agent.Providers;

/// <summary>
/// Selects the concrete chat provider from AppSettings (with an internal test seam
/// So unit tests can inject a fake provider without touching the network).
/// </summary>
internal static class ProviderFactory
{
    internal static Func<AppSettings, IChatProvider>? OverrideForTests { get; set; }

    internal static bool IsAnthropic(AppSettings settings)
    {
        if (string.Equals(settings.Provider, "anthropic", StringComparison.OrdinalIgnoreCase))
            return true;
        var url = settings.BaseUrl ?? "";
        return url.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static IChatProvider Create(AppSettings settings)
    {
        if (OverrideForTests != null)
            return OverrideForTests(settings);

        return IsAnthropic(settings)
            ? new AnthropicProvider()
            : new OpenAiCompatibleProvider();
    }
}

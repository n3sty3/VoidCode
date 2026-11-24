using System;
using System.Threading;
using VoidCode.Services;

namespace VoidCode.Agent.Providers;

/// <summary>
/// Selects the concrete chat provider from AppSettings (with an AsyncLocal test seam
/// so parallel xUnit tests can inject a fake provider without racing each other).
/// </summary>
internal static class ProviderFactory
{
    private static readonly AsyncLocal<Func<AppSettings, IChatProvider>?> OverrideSlot = new();

    internal static Func<AppSettings, IChatProvider>? OverrideForTests
    {
        get => OverrideSlot.Value;
        set => OverrideSlot.Value = value;
    }

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

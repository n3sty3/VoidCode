using System;
using System.Diagnostics;

namespace VoidCode.Services;

// Extracted from MainWindow so the window keeps shrinking, and so the scheme
// Allow-list lives in one testable place. Only http, https and mailto may leave the app - a
// File:// or shell: link from the page is refused.
public static class UriLauncher
{
    public static bool OpenExternal(string? rawUri)
    {
        try
        {
            if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps &&
                !string.Equals(uri.Scheme, "mailto", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Could not open external URI: {ex.Message}", nameof(UriLauncher));
            return false;
        }
    }
}
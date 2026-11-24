using System;
using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;
using VoidCode.Services;

namespace VoidCode.Native;

/// <summary>
/// Wires <see cref="CspPolicy"/> onto a Windows WebView2 instance.
/// Kept in the Windows host project so <c>VoidCode.Core</c> has zero WebView2 dependency.
/// </summary>
internal static class WebView2Csp
{
    internal static void AttachTo(CoreWebView2 core, string wwwrootRoot)
    {
        core.AddWebResourceRequestedFilter(CspPolicy.DocumentUri, CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                if (!string.Equals(e.Request.Uri, CspPolicy.DocumentUri, StringComparison.OrdinalIgnoreCase)) return;

                var html = File.ReadAllText(Path.Combine(wwwrootRoot, "index.html"), Encoding.UTF8);
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
                e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", CspPolicy.DocumentResponseHeaders());
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"CSP report-only header could not be applied: {ex.Message}", nameof(WebView2Csp));
            }
        };
    }
}

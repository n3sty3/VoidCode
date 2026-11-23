using System;
using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace VoidCode.Services;

// Trusted Types, report-only first.
//
// The virtual-host folder mapping cannot attach response headers, and a report-only policy
// Cannot be delivered with <meta> (Chromium ignores it - verified), so the document request is
// Answered here with the file plus the report-only policy. If anything fails, e.Response stays
// Unset and WebView2 falls back to the mapping: the failure mode is "no policy", never a
// Blank window.
public static class CspPolicy
{
    public const string DocumentUri = "http://app.local/index.html";

    // The app's own sink policy is voidcode-html (created in core/markdown.js). `dompurify` is
    // Allowlisted because the vendored sanitizer registers that policy name itself.
    public const string ReportOnlyValue =
        "require-trusted-types-for 'script'; trusted-types voidcode-html dompurify";

    /// <summary>The response headers served with index.html.</summary>
    public static string DocumentResponseHeaders() =>
        "Content-Type: text/html; charset=utf-8\r\n" +
        "Content-Security-Policy-Report-Only: " + ReportOnlyValue + "\r\n" +
        "Cache-Control: no-store";

    /// <summary>Wires the report-only document response onto an initialized WebView2 core.</summary>
    public static void AttachTo(CoreWebView2 core, string wwwrootRoot)
    {
        core.AddWebResourceRequestedFilter(DocumentUri, CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                if (!string.Equals(e.Request.Uri, DocumentUri, StringComparison.OrdinalIgnoreCase)) return;

                var html = File.ReadAllText(Path.Combine(wwwrootRoot, "index.html"), Encoding.UTF8);
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
                e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", DocumentResponseHeaders());
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"CSP report-only header could not be applied: {ex.Message}", nameof(CspPolicy));
            }
        };
    }
}

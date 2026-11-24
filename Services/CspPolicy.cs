using System;

namespace VoidCode.Services;

// Trusted Types, report-only first.
//
// The virtual-host folder mapping cannot attach response headers, and a report-only policy
// Cannot be delivered with <meta> (Chromium ignores it - verified), so the document request is
// Answered by the host with the file plus the report-only policy.
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
}

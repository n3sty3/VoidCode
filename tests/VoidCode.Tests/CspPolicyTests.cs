using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// Index.html is served with a report-only Trusted Types policy. These tests pin
// The header contract (the WebView2 wiring itself cannot be exercised without a WebView2 runtime).
public class CspPolicyTests
{
    [Fact]
    public void DocumentResponseHeaders_DeclareAReportOnlyPolicy()
    {
        var headers = CspPolicy.DocumentResponseHeaders();

        Assert.Contains("Content-Type: text/html; charset=utf-8", headers);
        Assert.Contains("Content-Security-Policy-Report-Only: ", headers);
        // Report-only, never the enforcing header name on its own
        Assert.DoesNotContain("\r\nContent-Security-Policy: ", headers);
    }

    [Fact]
    public void ReportOnlyValue_RequiresTrustedTypesForScripts()
    {
        Assert.Contains("require-trusted-types-for 'script'", CspPolicy.ReportOnlyValue);
    }

    [Fact]
    public void ReportOnlyValue_AllowlistsOnlyOurPolicyAndDompurify()
    {
        Assert.Contains("trusted-types voidcode-html dompurify", CspPolicy.ReportOnlyValue);

        // Least privilege: no wildcard and no `default` policy
        Assert.DoesNotContain("*", CspPolicy.ReportOnlyValue);
        Assert.DoesNotContain("default", CspPolicy.ReportOnlyValue);
    }

    [Fact]
    public void DocumentUri_IsTheMappedTopLevelDocument()
    {
        Assert.Equal("http://app.local/index.html", CspPolicy.DocumentUri);
        Assert.True(BridgeProtocol.IsTrustedSource(CspPolicy.DocumentUri));
    }
}

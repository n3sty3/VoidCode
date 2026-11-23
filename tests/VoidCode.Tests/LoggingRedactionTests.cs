using System;
using System.IO;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The log file is the support bundle users hand over, so a credential must never
// Reach it. Redaction happens on the way in - in LoggingService.Redact and therefore in every
// Entry WriteEntry persists.
[Collection("Environment variables")]
public class LoggingRedactionTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string? _previousDir;

    public LoggingRedactionTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vc-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _previousDir = Environment.GetEnvironmentVariable("VOIDCODE_LOG_DIR");
        Environment.SetEnvironmentVariable("VOIDCODE_LOG_DIR", _sandbox);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VOIDCODE_LOG_DIR", _previousDir);
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); }
        catch (Exception ex) { LoggingService.Swallowed(ex, "LoggingRedactionTests.cleanup"); }
    }

    [Fact]
    public void SkKeysAreMasked()
    {
        var redacted = LoggingService.Redact("provider rejected sk-proj-abcdef123456 for model x");

        Assert.Contains("sk-***", redacted);
        Assert.DoesNotContain("abcdef123456", redacted);
    }

    [Fact]
    public void AuthorizationHeadersAreMasked()
    {
        var redacted = LoggingService.Redact("request headers: Authorization: Bearer abcdefghijklmnop");

        Assert.Contains("Authorization: ***", redacted);
        Assert.DoesNotContain("abcdefghijklmnop", redacted);
    }

    [Fact]
    public void BearerTokensAreMaskedOnTheirOwn()
    {
        var redacted = LoggingService.Redact("retrying with Bearer zzzzzzzzzzzzzzzz");

        Assert.Contains("Bearer ***", redacted);
        Assert.DoesNotContain("zzzzzzzzzzzzzzzz", redacted);
    }

    [Fact]
    public void ApiKeyAssignmentsAreMasked()
    {
        foreach (var text in new[] { "api_key=topsecretvalue1", "x-api-key: topsecretvalue2", "access_token=topsecretvalue3" })
        {
            var redacted = LoggingService.Redact(text);
            Assert.Contains("***", redacted);
            Assert.DoesNotContain("topsecret", redacted);
        }
    }

    [Fact]
    public void RegisteredSecretsAreMasked()
    {
        LoggingService.RegisterSecret("registered-secret-value-9f8e7d");

        var redacted = LoggingService.Redact("echo: registered-secret-value-9f8e7d");

        Assert.DoesNotContain("registered-secret-value-9f8e7d", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void OrdinaryTextSurvives()
    {
        const string text = "the token is expired; retry in 5s after the folder is trusted";

        Assert.Equal(text, LoggingService.Redact(text));
        Assert.Equal(string.Empty, LoggingService.Redact(null));
    }

    [Fact]
    public void WrittenEntriesAreRedactedOnDisk()
    {
        LoggingService.LogInfo("provider rejected sk-live-9f8e7d6c5b4a with Authorization: Bearer qqqqqqqqqqqqqq");

        var log = File.ReadAllText(LoggingService.LogFilePath);
        Assert.Contains("***", log);
        Assert.DoesNotContain("sk-live-9f8e7d6c5b4a", log);
        Assert.DoesNotContain("qqqqqqqqqqqqqq", log);
    }

    [Fact]
    public void ExceptionMessagesAreRedactedOnDisk()
    {
        LoggingService.LogError("request failed", new InvalidOperationException("bad key sk-abcdef123456"), "test");

        var log = File.ReadAllText(LoggingService.LogFilePath);
        Assert.DoesNotContain("sk-abcdef123456", log);
        Assert.Contains("sk-***", log);
    }
}
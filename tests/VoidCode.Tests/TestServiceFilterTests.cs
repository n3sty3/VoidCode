using System;
using System.IO;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

public class TestServiceFilterTests
{
    [Fact]
    public void ApplyTestFilter_SupportsDotnetPytestGoAndCargo()
    {
        Assert.Contains("--filter \"CalculatorTests.AddsNumbers\"",
            TestService.ApplyTestFilter("dotnet test", "", "test", "CalculatorTests.AddsNumbers"));
        Assert.Contains("-k \"test_login\"",
            TestService.ApplyTestFilter("pytest", "", "-v", "test_login"));
        Assert.Equal("test -json -run \"^TestParse$\" ./...",
            TestService.ApplyTestFilter("go test", "", "test -json ./...", "^TestParse$"));
        Assert.Equal("test \"parses_header\"",
            TestService.ApplyTestFilter("cargo test", "", "test", "parses_header"));
    }

    [Fact]
    public void ApplyTestFilter_DetectsNpmTestFrameworkFlags()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vc_test_filter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "package.json"), "{\"scripts\":{\"test\":\"vitest run\"}}");
            var vitestArgs = TestService.ApplyTestFilter("npm test", tmp, "/c npm test", "renders header");
            Assert.Contains("-- -t \"renders header\"", vitestArgs);

            File.WriteAllText(Path.Combine(tmp, "package.json"), "{\"scripts\":{\"test\":\"playwright test\"}}");
            var pwArgs = TestService.ApplyTestFilter("npm test", tmp, "/c npm test", "opens tab");
            Assert.Contains("-- -g \"opens tab\"", pwArgs);

            File.WriteAllText(Path.Combine(tmp, "package.json"), "{\"scripts\":{\"test\":\"node --test\"}}");
            var nodeArgs = TestService.ApplyTestFilter("npm test", tmp, "/c npm test", "parses config");
            Assert.Contains("-- --test-name-pattern=\"parses config\"", nodeArgs);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ApplyTestFilter_SanitizesShellMetacharacters()
    {
        var result = TestService.ApplyTestFilter("dotnet test", "", "test", "Foo; rm -rf / & \"$(evil)\"");
        Assert.DoesNotContain(";", result);
        Assert.DoesNotContain("&", result);
        Assert.DoesNotContain("$", result);
    }
}

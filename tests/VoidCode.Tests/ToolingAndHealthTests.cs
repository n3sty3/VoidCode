using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("StorageRoot")]
public class ToolingAndHealthTests
{
    [Fact]
    public void CapabilitiesService_ReportsFoundAndMissingAccurately_FromFakePath()
    {
        var fakeBin = Path.Combine(Path.GetTempPath(), "voidcode-fakepath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeBin);
        try
        {
            foreach (var f in new[] { "node.exe", "npm.cmd", "git.exe", "rg.exe", "pyright-langserver.cmd" })
            {
                File.WriteAllText(Path.Combine(fakeBin, f), "");
            }

            var settings = new AppSettings
            {
                Provider = "anthropic",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-4-20250514",
                ApiKeyConfigured = true
            };

            var report = CapabilitiesService.Inspect(fakeBin, settings, CapabilitiesService.FromPathString(fakeBin));

            Assert.True(report.Debug.Node);
            Assert.False(report.Debug.Dotnet);
            Assert.False(report.Debug.Python);

            Assert.True(report.Git.Found);
            Assert.True(report.Ripgrep.Found);
            Assert.True(report.Provider.Configured);
            Assert.Equal(200_000, report.Provider.ContextWindow);

            var pyLsp = Assert.Single(report.Lsp, l => l.Language == "python");
            Assert.True(pyLsp.Found);

            var csLsp = Assert.Single(report.Lsp, l => l.Language == "csharp");
            Assert.False(csLsp.Found);
            Assert.False(csLsp.PrerequisiteFound);
            Assert.Contains("csharp-ls", csLsp.InstallCommand);

            var tsLsp = Assert.Single(report.Lsp, l => l.Language == "typescript");
            Assert.False(tsLsp.Found);
            Assert.True(tsLsp.PrerequisiteFound); // npm.cmd was created in fakeBin
        }
        finally
        {
            try { Directory.Delete(fakeBin, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public async Task LspInstallService_PlansAllSixLanguages_StreamsProgress_AndFindsManagedBinaries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "voidcode-lspinst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        using var _ = StorageService.UseRoot(tempRoot);
        try
        {
            foreach (var lang in LspInstallService.SupportedLanguages)
            {
                var plan = LspInstallService.GetInstallPlan(lang);
                Assert.NotNull(plan);
                Assert.False(string.IsNullOrWhiteSpace(plan!.DisplayCommand));
            }

            var progress = new List<string>();
            var okResult = await LspInstallService.InstallAsync(
                "typescript",
                line => progress.Add(line),
                CancellationToken.None,
                (plan, emit, _) =>
                {
                    emit?.Invoke("added 2 packages");
                    return Task.FromResult((0, "ok"));
                });

            Assert.True(okResult.Success);
            Assert.Contains(progress, l => l.Contains("typescript-language-server"));
            Assert.Contains(progress, l => l.Contains("added 2 packages"));

            var failResult = await LspInstallService.InstallAsync(
                "csharp",
                null,
                CancellationToken.None,
                (_, _, _) => Task.FromResult((1, "dotnet missing")));
            Assert.False(failResult.Success);

            // Prove LspService.FindInPath discovers a binary dropped into managed tools\bin
            var managedExe = Path.Combine(LspInstallService.ToolsRoot, "bin", "voidcode-fake-lsp.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(managedExe)!);
            File.WriteAllText(managedExe, "");
            Assert.Equal(managedExe, LspService.FindInPath("voidcode-fake-lsp.exe"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void RipgrepSearchService_BuildsArgs_ParsesJsonMatches_AndFallsBackCleanly()
    {
        var temp = Path.Combine(Path.GetTempPath(), "voidcode-rg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp, ".voidcodeignore"), "*.generated.cs\n");
            File.WriteAllText(Path.Combine(temp, "Program.cs"), "public class Widget {}\n");

            var args = RipgrepSearchService.BuildArguments(
                temp, "Widget", isRegex: false, matchCase: true, matchWholeWord: true, fileFilter: "*.cs", excludeFilter: "bin/**");

            Assert.Contains("--json", args);
            Assert.Contains("--no-require-git", args);
            Assert.Contains("--fixed-strings", args);
            Assert.Contains("--case-sensitive", args);
            Assert.Contains("--word-regexp", args);
            Assert.Contains("--ignore-file", args);
            Assert.Contains("*.cs", args);
            Assert.Contains("!bin/**", args);

            // Parse a real ripgrep --json match frame
            var sampleLine = JsonSerializer.Serialize(new
            {
                type = "match",
                data = new
                {
                    path = new { text = Path.Combine(temp, "Program.cs") },
                    lines = new { text = "public class Widget {}\n" },
                    line_number = 1,
                    submatches = new[] { new { start = 13, end = 19 } }
                }
            });

            var byFile = new Dictionary<string, SearchFileResult>(StringComparer.OrdinalIgnoreCase);
            var resp = new SearchWorkspaceResponse { Query = "Widget" };
            RipgrepSearchService.ParseJsonMatchLine(sampleLine, temp, byFile, resp, 500);

            Assert.Equal(1, resp.TotalMatches);
            var file = Assert.Single(byFile.Values);
            Assert.Equal("Program.cs", file.RelativePath);
            Assert.Equal(1, file.Matches[0].Line);
            Assert.Equal(14, file.Matches[0].Column); // 1-based
            Assert.Equal(6, file.Matches[0].Length);

            // When rg is absent, FileSystemService transparently falls back to its C# walker
            var prevOverride = RipgrepSearchService.ExecutableOverrideForTests;
            RipgrepSearchService.ExecutableOverrideForTests = () => null;
            try
            {
                var fallback = FileSystemService.SearchWorkspaceStructured(temp, "Widget");
                Assert.Equal(1, fallback.TotalMatches);
                Assert.Single(fallback.Files);
            }
            finally
            {
                RipgrepSearchService.ExecutableOverrideForTests = prevOverride;
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public async Task BridgeRoute_GetCapabilities_ReturnsStructuredReport()
    {
        var events = new List<(string Action, JsonNode Payload)>();
        var router = new BridgeRouter();
        var settings = new AppSettings { Provider = "openrouter", Model = "deepseek/deepseek-chat" };
        var handlers = new SettingsAgentHandlers(
            () => Environment.CurrentDirectory,
            () => settings,
            updated => settings = updated,
            _ => { },
            (action, payload) => events.Add((action, JsonSerializer.SerializeToNode(payload)!)),
            _ => { });
        handlers.Register(router);

        await router.TryHandleAsync(SettingsAgentRoutes.GetCapabilities, "cap_1", new JsonObject());

        var frame = Assert.Single(events, e => e.Action == "capabilities_response");
        var json = frame.Payload.ToJsonString();
        Assert.Contains("\"debug\":", json);
        Assert.Contains("\"lsp\":", json);
        Assert.Contains("\"ripgrep\":", json);
        Assert.Contains("\"runtimes\":", json);
    }
}

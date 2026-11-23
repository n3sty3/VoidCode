using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using VoidCode.Agent.Providers;

namespace VoidCode.Services;

/// <summary>
/// Honest runtime/tooling capability report powering the Health screen and UI gates.
/// Uses an injectable executable lookup seam so unit tests can verify found/missing states against
/// A fake PATH without touching the host machine.
/// </summary>
public sealed class BinaryCapability
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("installHint")]
    public string? InstallHint { get; set; }
}

public sealed class LspCapability
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("installCommand")]
    public string? InstallCommand { get; set; }

    [JsonPropertyName("prerequisite")]
    public string? Prerequisite { get; set; }

    [JsonPropertyName("prerequisiteFound")]
    public bool PrerequisiteFound { get; set; }
}

public sealed class DebugCapabilities
{
    [JsonPropertyName("node")]
    public bool Node { get; set; }

    [JsonPropertyName("dotnet")]
    public bool Dotnet { get; set; }

    [JsonPropertyName("python")]
    public bool Python { get; set; }
}

public sealed class ProviderCapabilityInfo
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("toolsSupported")]
    public bool ToolsSupported { get; set; }

    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; set; }
}

public sealed class CapabilitiesReport
{
    [JsonPropertyName("debug")]
    public DebugCapabilities Debug { get; set; } = new();

    [JsonPropertyName("lsp")]
    public List<LspCapability> Lsp { get; set; } = new();

    [JsonPropertyName("git")]
    public BinaryCapability Git { get; set; } = new();

    [JsonPropertyName("ripgrep")]
    public BinaryCapability Ripgrep { get; set; } = new();

    [JsonPropertyName("runtimes")]
    public List<BinaryCapability> Runtimes { get; set; } = new();

    [JsonPropertyName("shells")]
    public List<BinaryCapability> Shells { get; set; } = new();

    [JsonPropertyName("provider")]
    public ProviderCapabilityInfo Provider { get; set; } = new();

    [JsonPropertyName("workspaceTrust")]
    public string WorkspaceTrust { get; set; } = "unknown";
}

public static class CapabilitiesService
{
    public static CapabilitiesReport Inspect(
        string? workspacePath,
        AppSettings settings,
        Func<string, string?>? findExe = null)
    {
        var finder = findExe ?? DefaultFindExecutable;

        string? FindAny(params string[] names)
        {
            foreach (var n in names)
            {
                var found = finder(n);
                if (!string.IsNullOrEmpty(found)) return found;
            }
            return null;
        }

        var nodePath = FindAny("node.exe", "node");
        var npmPath = FindAny("npm.cmd", "npm.exe", "npm");
        var dotnetPath = FindAny("dotnet.exe", "dotnet");
        var pythonPath = FindAny("python.exe", "python3.exe", "python");
        var goPath = FindAny("go.exe", "go");
        var rustcPath = FindAny("rustc.exe", "rustc");
        var rustupPath = FindAny("rustup.exe", "rustup");
        var wingetPath = FindAny("winget.exe", "winget");
        var gitPath = FindAny("git.exe", "git");
        var rgPath = findExe != null
            ? FindAny("rg.exe", "rg")
            : RipgrepSearchService.FindExecutable();

        var runtimes = new List<BinaryCapability>
        {
            new() { Name = "Node.js", Found = nodePath != null, Path = nodePath, InstallHint = "winget install OpenJS.NodeJS.LTS" },
            new() { Name = "npm", Found = npmPath != null, Path = npmPath, InstallHint = "Included with Node.js" },
            new() { Name = ".NET SDK", Found = dotnetPath != null, Path = dotnetPath, InstallHint = "winget install Microsoft.DotNet.SDK.10" },
            new() { Name = "Python", Found = pythonPath != null, Path = pythonPath, InstallHint = "winget install Python.Python.3.12" },
            new() { Name = "Go", Found = goPath != null, Path = goPath, InstallHint = "winget install GoLang.Go" },
            new() { Name = "Rust (rustc)", Found = rustcPath != null, Path = rustcPath, InstallHint = "https://rustup.rs" }
        };

        if (!string.IsNullOrEmpty(workspacePath) && Directory.Exists(workspacePath))
        {
            foreach (var venvDir in new[] { ".venv", "venv" })
            {
                var venvPy = Path.Combine(workspacePath, venvDir, "Scripts", "python.exe");
                if (File.Exists(venvPy))
                {
                    runtimes.Insert(0, new BinaryCapability { Name = $"Python ({venvDir})", Found = true, Path = venvPy });
                    break;
                }
            }
        }

        var shells = new List<BinaryCapability>
        {
            new() { Name = "PowerShell 7 (pwsh)", Found = FindAny("pwsh.exe", "pwsh") != null, Path = FindAny("pwsh.exe", "pwsh"), InstallHint = "winget install Microsoft.PowerShell" },
            new() { Name = "Windows PowerShell", Found = FindAny("powershell.exe") != null, Path = FindAny("powershell.exe") },
            new() { Name = "Command Prompt (cmd)", Found = FindAny("cmd.exe") != null, Path = FindAny("cmd.exe") }
        };

        bool PrereqAvailable(string prereq) => prereq switch
        {
            "npm" => npmPath != null,
            "dotnet" => dotnetPath != null,
            "go" => goPath != null,
            "rustup" => rustupPath != null,
            "winget" => wingetPath != null,
            _ => FindAny(prereq + ".exe", prereq) != null
        };

        var lspSpecs = new (string lang, string[] bins)[]
        {
            ("typescript", new[] { "typescript-language-server.cmd", "typescript-language-server" }),
            ("python", new[] { "pyright-langserver.cmd", "pyright-langserver", "pylsp.exe", "pylsp", "ruff.exe", "ruff" }),
            ("csharp", new[] { "csharp-ls.exe", "csharp-ls", "OmniSharp.exe", "omnisharp" }),
            ("go", new[] { "gopls.exe", "gopls" }),
            ("rust", new[] { "rust-analyzer.exe", "rust-analyzer" }),
            ("cpp", new[] { "clangd.exe", "clangd" })
        };

        var lsps = new List<LspCapability>();
        foreach (var (lang, bins) in lspSpecs)
        {
            var foundPath = FindAny(bins);
            var plan = LspInstallService.GetInstallPlan(lang);
            lsps.Add(new LspCapability
            {
                Language = lang,
                ServerName = plan?.ServerName ?? bins[0],
                Found = foundPath != null,
                Path = foundPath,
                InstallCommand = plan?.DisplayCommand,
                Prerequisite = plan?.Prerequisite,
                PrerequisiteFound = plan != null && PrereqAvailable(plan.Prerequisite)
            });
        }

        var caps = ProviderCapabilityCache.Shared.Get(settings);
        var isLocal = (settings.BaseUrl ?? "").Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || (settings.BaseUrl ?? "").Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        var providerConfigured = !string.IsNullOrWhiteSpace(settings.BaseUrl)
            && !string.IsNullOrWhiteSpace(settings.Model)
            && (!string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKeyConfigured || isLocal);

        var trustState = !string.IsNullOrEmpty(workspacePath)
            ? WorkspaceTrustService.GetState(workspacePath).ToString().ToLowerInvariant()
            : WorkspaceTrustService.CurrentState.ToString().ToLowerInvariant();

        var netcoredbgPath = FindAny("netcoredbg.exe", "netcoredbg");
        var pythonDebugpyReady = findExe == null && pythonPath != null && DebugService.IsPythonDebugpyInstalled(pythonPath);

        return new CapabilitiesReport
        {
            Debug = new DebugCapabilities
            {
                Node = nodePath != null,
                Dotnet = netcoredbgPath != null,
                Python = pythonDebugpyReady
            },
            Lsp = lsps,
            Git = new BinaryCapability
            {
                Name = "Git",
                Found = gitPath != null,
                Path = gitPath,
                InstallHint = "winget install Git.Git"
            },
            Ripgrep = new BinaryCapability
            {
                Name = "ripgrep (rg)",
                Found = rgPath != null,
                Path = rgPath,
                InstallHint = "winget install BurntSushi.ripgrep.MSVC"
            },
            Runtimes = runtimes,
            Shells = shells,
            Provider = new ProviderCapabilityInfo
            {
                Configured = providerConfigured,
                Provider = settings.Provider ?? "",
                Model = settings.Model ?? "",
                BaseUrl = settings.BaseUrl ?? "",
                ToolsSupported = caps.ToolsSupported,
                ContextWindow = caps.ContextWindow
            },
            WorkspaceTrust = trustState
        };
    }

    public static Func<string, string?> FromPathString(string pathEnv)
    {
        var dirs = (pathEnv ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return exeName =>
        {
            foreach (var dir in dirs)
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(full)) return full;
                }
                catch (Exception ex) { LoggingService.Swallowed(ex, "CapabilitiesService.FromPathString"); }
            }
            return null;
        };
    }

    internal static string? DefaultFindExecutable(string exeName) => LspService.FindInPath(exeName);
}

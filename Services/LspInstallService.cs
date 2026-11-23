using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;

namespace VoidCode.Services;

/// <summary>
/// Managed language-server installer targeting VoidCode's local tools directory
/// (under StorageService.Root\tools so tests stay isolated) with transparent command previews and
/// Streamed install progress.
/// </summary>
public sealed class LspInstallPlan
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = "";

    [JsonPropertyName("serverName")]
    public string ServerName { get; init; } = "";

    [JsonPropertyName("prerequisite")]
    public string Prerequisite { get; init; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "";

    [JsonPropertyName("displayCommand")]
    public string DisplayCommand { get; init; } = "";

    [JsonPropertyName("envVars")]
    public Dictionary<string, string> EnvVars { get; init; } = new();
}

public sealed class LspInstallResult
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    [JsonPropertyName("output")]
    public string Output { get; init; } = "";
}

public static class LspInstallService
{
    public static string ToolsRoot => Path.Combine(StorageService.Root, "tools");

    public static IReadOnlyList<string> ManagedBinDirectories => new[]
    {
        Path.Combine(ToolsRoot, "bin"),
        Path.Combine(ToolsRoot, "node_modules", ".bin"),
        ToolsRoot
    };

    public static string BuildEffectivePath(string? currentPath = null)
    {
        var existing = currentPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var prefix = string.Join(Path.PathSeparator, ManagedBinDirectories);
        return string.IsNullOrEmpty(existing) ? prefix : $"{prefix}{Path.PathSeparator}{existing}";
    }

    public static IReadOnlyList<string> SupportedLanguages { get; } = new[]
    {
        "typescript", "python", "csharp", "go", "rust", "cpp"
    };

    public static LspInstallPlan? GetInstallPlan(string language)
    {
        var lang = LspService.NormalizeLanguage(language);
        var toolsRoot = ToolsRoot;
        var binDir = Path.Combine(toolsRoot, "bin");

        return lang switch
        {
            "typescript" or "javascript" => new LspInstallPlan
            {
                Language = "typescript",
                ServerName = "typescript-language-server",
                Prerequisite = "npm",
                FileName = "npm.cmd",
                Arguments = $"install --prefix \"{toolsRoot}\" --no-audit --no-fund typescript typescript-language-server",
                DisplayCommand = $"npm install --prefix \"{toolsRoot}\" typescript typescript-language-server"
            },
            "python" => new LspInstallPlan
            {
                Language = "python",
                ServerName = "pyright",
                Prerequisite = "npm",
                FileName = "npm.cmd",
                Arguments = $"install --prefix \"{toolsRoot}\" --no-audit --no-fund pyright",
                DisplayCommand = $"npm install --prefix \"{toolsRoot}\" pyright"
            },
            "csharp" => new LspInstallPlan
            {
                Language = "csharp",
                ServerName = "csharp-ls",
                Prerequisite = "dotnet",
                FileName = "dotnet",
                Arguments = $"tool update --tool-path \"{binDir}\" csharp-ls",
                DisplayCommand = $"dotnet tool install --tool-path \"{binDir}\" csharp-ls"
            },
            "go" => new LspInstallPlan
            {
                Language = "go",
                ServerName = "gopls",
                Prerequisite = "go",
                FileName = "go",
                Arguments = "install golang.org/x/tools/gopls@latest",
                DisplayCommand = $"GOBIN=\"{binDir}\" go install golang.org/x/tools/gopls@latest",
                EnvVars = new Dictionary<string, string> { ["GOBIN"] = binDir }
            },
            "rust" => new LspInstallPlan
            {
                Language = "rust",
                ServerName = "rust-analyzer",
                Prerequisite = "rustup",
                FileName = "rustup",
                Arguments = "component add rust-analyzer",
                DisplayCommand = "rustup component add rust-analyzer"
            },
            "cpp" => new LspInstallPlan
            {
                Language = "cpp",
                ServerName = "clangd",
                Prerequisite = "winget",
                FileName = "winget",
                Arguments = "install --id LLVM.LLVM -e --accept-source-agreements --accept-package-agreements",
                DisplayCommand = "winget install --id LLVM.LLVM -e"
            },
            _ => null
        };
    }

    public static async Task<LspInstallResult> InstallAsync(
        string language,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        Func<LspInstallPlan, Action<string>?, CancellationToken, Task<(int exitCode, string output)>>? runnerOverride = null)
    {
        var plan = GetInstallPlan(language);
        if (plan == null)
        {
            return new LspInstallResult
            {
                Language = language,
                Success = false,
                Message = $"No managed language-server installer exists for '{language}'."
            };
        }

        Directory.CreateDirectory(ToolsRoot);
        Directory.CreateDirectory(Path.Combine(ToolsRoot, "bin"));

        onProgress?.Invoke($"Running: {plan.DisplayCommand}");

        try
        {
            var (exitCode, output) = runnerOverride != null
                ? await runnerOverride(plan, onProgress, cancellationToken)
                : await RunInstallerProcessAsync(plan, onProgress, cancellationToken);

            if (exitCode == 0)
            {
                LspService.ClearUnavailable(plan.Language);
                if (plan.Language == "typescript") LspService.ClearUnavailable("javascript");
                onProgress?.Invoke($"Installed {plan.ServerName}.");
                return new LspInstallResult
                {
                    Language = plan.Language,
                    Success = true,
                    Message = $"Installed {plan.ServerName} cleanly. Restarting language server...",
                    Command = plan.DisplayCommand,
                    Output = output
                };
            }

            return new LspInstallResult
            {
                Language = plan.Language,
                Success = false,
                Message = $"Install exited with code {exitCode}. Check that '{plan.Prerequisite}' is available on PATH.",
                Command = plan.DisplayCommand,
                Output = output
            };
        }
        catch (OperationCanceledException)
        {
            return new LspInstallResult
            {
                Language = plan.Language,
                Success = false,
                Message = "Language server installation was cancelled.",
                Command = plan.DisplayCommand
            };
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"LSP install failed for {plan.Language}: {ex.Message}", ex, nameof(LspInstallService));
            return new LspInstallResult
            {
                Language = plan.Language,
                Success = false,
                Message = $"Could not start '{plan.Prerequisite}': {ex.Message}",
                Command = plan.DisplayCommand
            };
        }
    }

    private static async Task<(int exitCode, string output)> RunInstallerProcessAsync(
        LspInstallPlan plan,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (plan.Prerequisite == "npm")
        {
            var pkgJson = Path.Combine(ToolsRoot, "package.json");
            if (!File.Exists(pkgJson))
            {
                File.WriteAllText(pkgJson, "{\n  \"name\": \"voidcode-lsp-tools\",\n  \"private\": true\n}\n", Encoding.UTF8);
            }
        }

        // Resolve full executable path on PATH so Windows CreateProcessW finds .cmd wrappers
        // And %~dp0 inside npm.cmd resolves to the Node.js installation directory instead of WorkingDirectory.
        var resolvedExe = LspService.FindInPath(plan.FileName)
            ?? LspService.FindInPath(plan.FileName + ".exe")
            ?? LspService.FindInPath(plan.FileName + ".cmd");

        if (resolvedExe == null && plan.Language == "rust")
        {
            return await DownloadRustAnalyzerBinaryAsync(onProgress, cancellationToken);
        }

        if (resolvedExe == null)
        {
            var msg = $"Prerequisite '{plan.Prerequisite}' was not found on PATH. Install {plan.Prerequisite} first, then click Install.";
            onProgress?.Invoke(msg);
            return (-1, msg);
        }

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            Arguments = plan.Arguments,
            WorkingDirectory = ToolsRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PATH"] = BuildEffectivePath();
        foreach (var kvp in plan.EnvVars)
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            return (-1, $"Failed to start {resolvedExe}");
        }
        try { proc.StandardInput.Close(); } catch (Exception) { /* stdin closed best-effort */ }

        var output = new StringBuilder();
        using var cancelReg = cancellationToken.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception ex) { LoggingService.Swallowed(ex, "LspInstallService.CancelKill"); }
        });

        async Task DrainAsync(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                lock (output)
                {
                    if (output.Length < 16000) output.AppendLine(line);
                }
                onProgress?.Invoke(line);
            }
        }

        await Task.WhenAll(DrainAsync(proc.StandardOutput), DrainAsync(proc.StandardError));
        await proc.WaitForExitAsync(cancellationToken);
        return (proc.ExitCode, output.ToString());
    }

    private static async Task<(int exitCode, string output)> DownloadRustAnalyzerBinaryAsync(
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        const string url = "https://github.com/rust-lang/rust-analyzer/releases/latest/download/rust-analyzer-x86_64-pc-windows-msvc.gz";
        var destPath = Path.Combine(ToolsRoot, "bin", "rust-analyzer.exe");
        onProgress?.Invoke($"rustup not found on PATH — downloading standalone rust-analyzer binary from GitHub Releases...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("VoidCode-IDE/1.0.0");
            using var resp = await AgentHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                var err = $"GitHub download returned HTTP {(int)resp.StatusCode}.";
                onProgress?.Invoke(err);
                return ((int)resp.StatusCode, err);
            }
            await using var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            await using var gz = new GZipStream(netStream, CompressionMode.Decompress);
            await using (var outFile = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await gz.CopyToAsync(outFile, cancellationToken);
            }
            onProgress?.Invoke($"Saved rust-analyzer.exe to {destPath}");
            return (0, $"Downloaded rust-analyzer to {destPath}");
        }
        catch (Exception ex)
        {
            var err = $"Failed to download rust-analyzer: {ex.Message}";
            onProgress?.Invoke(err);
            return (-1, err);
        }
    }
}

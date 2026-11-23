using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

public class CommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public string? UpdatedWorkingDir { get; set; }
}

/// <summary>Result of a direct (shell-free) program spawn.</summary>
public sealed record SpawnResult(int ExitCode, string StdOut, string StdErr);

public class ProjectRunConfig
{
    public string Command { get; set; } = "";
    public string Label { get; set; } = "Run";
    public string RuntimeType { get; set; } = "general";
    public bool IsAvailable { get; set; } = true;
    public List<RunAlternative> Alternatives { get; set; } = new();
}

public class RunAlternative
{
    public string Label { get; set; } = "";
    public string Command { get; set; } = "";
}

public static class TerminalService
{
    private const string CwdMarker = "__VOIDCODE_CWD__:";
    private static Process? _activeProcess;
    private static string? _sessionWorkingDir;

    public static string GetEffectiveWorkingDir(string? workspaceFallback)
    {
        if (!string.IsNullOrEmpty(_sessionWorkingDir) && Directory.Exists(_sessionWorkingDir))
        {
            return _sessionWorkingDir;
        }
        if (!string.IsNullOrEmpty(workspaceFallback) && Directory.Exists(workspaceFallback))
        {
            _sessionWorkingDir = workspaceFallback;
            return workspaceFallback;
        }
        return Environment.CurrentDirectory;
    }

    public static void ResetWorkingDir(string? workspaceDir)
    {
        if (!string.IsNullOrEmpty(workspaceDir) && Directory.Exists(workspaceDir))
        {
            _sessionWorkingDir = workspaceDir;
        }
    }

    public static void KillActiveProcess()
    {
        try
        {
            if (_activeProcess != null && !_activeProcess.HasExited)
            {
                _activeProcess.Kill(true);
            }
        }
        catch (Exception) { /* process already exited */ }
    }

    public static async Task<CommandResult> ExecuteCommandAsync(
        string command,
        string? workingDir = null,
        Action<string, bool>? onLineOutput = null,
        int timeoutMs = 120000,
        bool trackWorkingDir = true)
    {
        var effectiveDir = trackWorkingDir
            ? GetEffectiveWorkingDir(workingDir)
            : (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory);

        // Wrap script in UTF-8 output encoding + optional working directory tracker
        var scriptBuilder = new StringBuilder();
        scriptBuilder.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;");
        scriptBuilder.AppendLine(command);
        if (trackWorkingDir)
        {
            scriptBuilder.AppendLine($"$__vcExit = $LASTEXITCODE; Write-Output (\"{CwdMarker}\" + (Get-Location).Path); if ($__vcExit -ne $null) {{ exit $__vcExit }}");
        }

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(scriptBuilder.ToString()));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = effectiveDir
        };

        using var process = new Process { StartInfo = psi };
        _activeProcess = process;

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        string? detectedCwd = null;

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith(CwdMarker, StringComparison.Ordinal))
            {
                var candidate = e.Data.Substring(CwdMarker.Length).Trim();
                if (Directory.Exists(candidate))
                {
                    detectedCwd = candidate;
                    if (trackWorkingDir) _sessionWorkingDir = candidate;
                }
                return;
            }

            outputBuilder.AppendLine(e.Data);
            onLineOutput?.Invoke(e.Data, false);
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            // Suppress PowerShell CLIXML progress noise
            if (e.Data.StartsWith("#< CLIXML", StringComparison.Ordinal) ||
                e.Data.Contains("Objs Version=\"1.1.0.1\"", StringComparison.Ordinal))
            {
                return;
            }

            errorBuilder.AppendLine(e.Data);
            onLineOutput?.Invoke(e.Data, true);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token);

            return new CommandResult
            {
                ExitCode = process.ExitCode,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                UpdatedWorkingDir = detectedCwd
            };
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception) { /* process already exited */ }
            return new CommandResult
            {
                ExitCode = -1,
                Output = outputBuilder.ToString(),
                Error = $"Execution timed out or killed after {timeoutMs / 1000} seconds.",
                UpdatedWorkingDir = detectedCwd
            };
        }
        catch (Exception ex)
        {
            return new CommandResult
            {
                ExitCode = -1,
                Output = outputBuilder.ToString(),
                Error = $"Error executing command: {ex.Message}",
                UpdatedWorkingDir = detectedCwd
            };
        }
        finally
        {
            if (_activeProcess == process) _activeProcess = null;
        }
    }

    /// <summary>
    /// Runs a program directly — no shell, no PowerShell, no cmd wrapper — so the
    /// Custom VoidShell can execute git/npm/node itself. Arguments are handed over
    /// As a list, never as a parsed command line.
    /// </summary>
    public static async Task<SpawnResult> SpawnAsync(
        string file,
        string[] args,
        string? workingDir,
        string? stdin = null,
        int timeoutMs = 120000)
    {
        var resolved = ResolveExecutable(file);
        if (resolved == null)
        {
            return new SpawnResult(127, "", $"command not found: {file}");
        }

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
                ? workingDir
                : Environment.CurrentDirectory
        };

        // .cmd/.bat are OS shims, not programs: they need cmd.exe to be interpreted.
        // Everything else (git.exe, node.exe, …) is spawned directly.
        if (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c \"\"{resolved}\" {string.Join(' ', args.Select(QuoteArg))}\"";
        }
        else
        {
            psi.FileName = resolved;
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        _activeProcess = process;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token);
            return new SpawnResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception) { /* process already exited */ }
            return new SpawnResult(-1, stdout.ToString(), "cancelled by user");
        }
        catch (Exception ex)
        {
            return new SpawnResult(-1, stdout.ToString(), stderr + ex.Message);
        }
        finally
        {
            if (_activeProcess == process) _activeProcess = null;
        }
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string? ResolveExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (name.Contains('\\') || name.Contains('/'))
        {
            var full = Path.GetFullPath(name);
            return File.Exists(full) ? full : null;
        }

        var extensions = new[] { "", ".exe", ".cmd", ".bat", ".com" };
        var searchPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in searchPath)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception) { /* unusable directory entry */ }
            }
        }

        return null;
    }

    public static string ResolveRunCommand(string filePath, string? workspaceDir)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        switch (ext)
        {
            case ".py":
            {
                var pyExe = FindExecutable("python.exe", new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
                    @"C:\Python312",
                    @"C:\Python311"
                });
                return pyExe != null ? $"& \"{pyExe}\" \"{filePath}\"" : $"python \"{filePath}\"";
            }

            case ".js":
            case ".mjs":
            case ".cjs":
            {
                var nodeExe = FindExecutable("node.exe", new[]
                {
                    @"C:\Program Files\nodejs",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs")
                });
                return nodeExe != null ? $"& \"{nodeExe}\" \"{filePath}\"" : $"node \"{filePath}\"";
            }

            case ".ts":
                return $"npx tsx \"{filePath}\"";

            case ".cs":
                return "dotnet run";

            case ".rs":
                return "cargo run";

            case ".go":
                return $"go run \"{filePath}\"";

            case ".ps1":
                return $"& \"{filePath}\"";

            case ".bat":
            case ".cmd":
                return $"cmd.exe /c \"\\\"{filePath}\\\"\"";

            case ".cpp":
            case ".c":
            {
                var outExe = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(filePath) + "_vc.exe");
                var compiler = ext == ".cpp" ? "g++" : "gcc";
                return $"{compiler} \"{filePath}\" -O2 -o \"{outExe}\"; if ($LASTEXITCODE -eq 0) {{ & \"{outExe}\" }}";
            }

            default:
                return $"& \"{filePath}\"";
        }
    }

    private static string? FindExecutable(string exeName, IEnumerable<string> extraSearchRoots)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { /* unusable PATH entry */ }
        }

        foreach (var root in extraSearchRoots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var direct = Path.Combine(root, exeName);
                if (File.Exists(direct)) return direct;

                var found = Directory.GetFiles(root, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found;
            }
            catch (Exception) { /* root not searchable */ }
        }

        return null;
    }

    public static ProjectRunConfig DetectProjectRunConfig(string? workspaceDir, string? activeFilePath)
    {
        var config = new ProjectRunConfig();

        if (!string.IsNullOrEmpty(workspaceDir) && Directory.Exists(workspaceDir))
        {
            // 1. .NET Detection (.csproj or .sln)
            var csprojFiles = Directory.GetFiles(workspaceDir, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0 || Directory.GetFiles(workspaceDir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
            {
                var projName = csprojFiles.Length > 0 ? Path.GetFileNameWithoutExtension(csprojFiles[0]) : "Project";
                config.RuntimeType = "dotnet";
                config.Label = $"dotnet run ({projName})";
                config.Command = "dotnet run";
                config.Alternatives.Add(new RunAlternative { Label = "dotnet build", Command = "dotnet build" });
                config.Alternatives.Add(new RunAlternative { Label = "dotnet test", Command = "dotnet test" });
                config.Alternatives.Add(new RunAlternative { Label = "dotnet watch run", Command = "dotnet watch run" });
                return config;
            }

            // 2. Node.js Detection (package.json)
            var pkgJson = Path.Combine(workspaceDir, "package.json");
            if (File.Exists(pkgJson))
            {
                config.RuntimeType = "node";
                string script = "start";
                try
                {
                    var text = File.ReadAllText(pkgJson);
                    if (text.Contains("\"dev\"")) script = "run dev";
                    else if (text.Contains("\"start\"")) script = "start";
                    else if (text.Contains("\"test\"")) script = "test";
                }
                catch (Exception ex) { LoggingService.Swallowed(ex, "TerminalService.DetectRunConfig"); }

                config.Label = $"npm {script}";
                config.Command = $"npm {script}";
                config.Alternatives.Add(new RunAlternative { Label = "npm start", Command = "npm start" });
                config.Alternatives.Add(new RunAlternative { Label = "npm run dev", Command = "npm run dev" });
                config.Alternatives.Add(new RunAlternative { Label = "npm test", Command = "npm test" });
                config.Alternatives.Add(new RunAlternative { Label = "npm run build", Command = "npm run build" });
                config.Alternatives.Add(new RunAlternative { Label = "npm install", Command = "npm install" });
                return config;
            }

            // 3. Rust Detection (Cargo.toml)
            if (File.Exists(Path.Combine(workspaceDir, "Cargo.toml")))
            {
                config.RuntimeType = "rust";
                config.Label = "cargo run";
                config.Command = "cargo run";
                config.Alternatives.Add(new RunAlternative { Label = "cargo build", Command = "cargo build" });
                config.Alternatives.Add(new RunAlternative { Label = "cargo test", Command = "cargo test" });
                return config;
            }

            // 4. Go Detection (go.mod)
            if (File.Exists(Path.Combine(workspaceDir, "go.mod")))
            {
                config.RuntimeType = "go";
                config.Label = "go run .";
                config.Command = "go run .";
                config.Alternatives.Add(new RunAlternative { Label = "go build", Command = "go build" });
                config.Alternatives.Add(new RunAlternative { Label = "go test", Command = "go test ./..." });
                return config;
            }

            // 5. Python Detection
            var pyFiles = Directory.GetFiles(workspaceDir, "*.py", SearchOption.TopDirectoryOnly);
            if (pyFiles.Length > 0 || File.Exists(Path.Combine(workspaceDir, "requirements.txt")) || File.Exists(Path.Combine(workspaceDir, "pyproject.toml")))
            {
                string entryPy = "main.py";
                if (!File.Exists(Path.Combine(workspaceDir, entryPy)))
                {
                    var candidates = new[] { "app.py", "run.py", "server.py", "index.py" };
                    var found = candidates.FirstOrDefault(c => File.Exists(Path.Combine(workspaceDir, c)));
                    entryPy = found ?? (pyFiles.Length > 0 ? Path.GetFileName(pyFiles[0]) : "main.py");
                }

                config.RuntimeType = "python";
                config.Label = $"python {entryPy}";
                config.Command = $"python \"{entryPy}\"";
                config.Alternatives.Add(new RunAlternative { Label = $"python {entryPy}", Command = $"python \"{entryPy}\"" });
                if (File.Exists(Path.Combine(workspaceDir, "requirements.txt")))
                {
                    config.Alternatives.Add(new RunAlternative { Label = "pip install -r requirements.txt", Command = "pip install -r requirements.txt" });
                }
                return config;
            }

            // 6. Web Preview (index.html in root)
            if (File.Exists(Path.Combine(workspaceDir, "index.html")))
            {
                config.RuntimeType = "web";
                config.Label = "Live Preview (index.html)";
                config.Command = "__PREVIEW__";
                config.Alternatives.Add(new RunAlternative { Label = "Live Preview", Command = "__PREVIEW__" });
                return config;
            }
        }

        // 7. Fallback to active file
        if (!string.IsNullOrEmpty(activeFilePath) && File.Exists(activeFilePath))
        {
            var fileName = Path.GetFileName(activeFilePath);
            var ext = Path.GetExtension(activeFilePath).ToLowerInvariant();
            config.RuntimeType = ext.TrimStart('.');
            config.Label = $"Run {fileName}";
            config.Command = ResolveRunCommand(activeFilePath, workspaceDir);
            config.Alternatives.Add(new RunAlternative { Label = $"Run {fileName}", Command = config.Command });
            return config;
        }

        config.Label = "Run (PowerShell)";
        config.Command = "";
        config.IsAvailable = false;
        return config;
    }
}

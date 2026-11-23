using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

/// <summary>
/// Reads .voidcode/tasks.json and .vscode/tasks.json (JSONC-tolerant), executes
/// Tasks in trusted workspaces, and parses compiler/linter lines via built-in problem matchers
/// ($tsc, $msCompile, $gcc, $python) into structured editor diagnostics.
/// </summary>
public sealed class WorkspaceTaskDefinition
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "shell";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("problemMatcher")]
    public List<string> ProblemMatchers { get; set; } = new();

    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; } = "";
}

public sealed class TaskProblemDiagnostic
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("line")]
    public int Line { get; set; } = 1;

    [JsonPropertyName("column")]
    public int Column { get; set; } = 1;

    [JsonPropertyName("severity")]
    public int Severity { get; set; } = 1; // 1=Error, 2=Warning, 3=Info

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "task";
}

public sealed class TaskRunResult
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("problems")]
    public List<TaskProblemDiagnostic> Problems { get; set; } = new();
}

public static class TaskRunnerService
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // $msCompile: path\File.cs(12,5): error CS1002: ; expected [proj.csproj]
    private static readonly Regex MsCompileRegex = new(
        @"^\s*(?<file>[^(\r\n]+?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning|info)\s+(?<code>[A-Za-z0-9_]+)?\s*:\s*(?<msg>.+?)(?:\s+\[[^\]]+\])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // $tsc: src/app.ts(14,3): error TS2322: Type 'string' is not assignable...
    // Or   src/app.ts:14:3 - error TS2322: ...
    private static readonly Regex TscColonRegex = new(
        @"^\s*(?<file>[^:\r\n]+?):(?<line>\d+):(?<col>\d+)\s*-\s*(?<sev>error|warning)\s+(?<code>TS\d+)\s*:\s*(?<msg>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // $gcc / cargo / go: path/file.c:42:10: error: expected ';'
    private static readonly Regex GccRegex = new(
        @"^\s*(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?<line>\d+)(?::(?<col>\d+))?\s*:\s*(?<sev>fatal error|error|warning|note)\s*:\s*(?<msg>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<WorkspaceTaskDefinition> LoadTasks(string workspacePath)
    {
        var tasks = new List<WorkspaceTaskDefinition>();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return tasks;

        var candidates = new[]
        {
            Path.Combine(workspacePath, ".voidcode", "tasks.json"),
            Path.Combine(workspacePath, ".vscode", "tasks.json")
        };

        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in candidates)
        {
            if (!File.Exists(file) || !PathPolicy.IsInsideWorkspace(workspacePath, file)) continue;
            try
            {
                var raw = File.ReadAllText(file, Encoding.UTF8);
                using var doc = JsonDocument.Parse(raw, JsoncOptions);
                if (!doc.RootElement.TryGetProperty("tasks", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;

                var relSource = Path.GetRelativePath(workspacePath, file);
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var label = item.TryGetProperty("label", out var l) ? l.GetString() : null;
                    var command = item.TryGetProperty("command", out var c) ? c.GetString() : null;
                    if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(command)) continue;
                    if (!seenLabels.Add(label)) continue;

                    var def = new WorkspaceTaskDefinition
                    {
                        Label = label,
                        Type = item.TryGetProperty("type", out var t) ? (t.GetString() ?? "shell") : "shell",
                        Command = command,
                        SourceFile = relSource
                    };

                    if (item.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in argsEl.EnumerateArray())
                        {
                            if (a.ValueKind == JsonValueKind.String) def.Args.Add(a.GetString() ?? "");
                            else if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                                def.Args.Add(v.GetString() ?? "");
                        }
                    }

                    if (item.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Object &&
                        optEl.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                    {
                        def.Cwd = cwdEl.GetString();
                    }

                    if (item.TryGetProperty("group", out var grpEl))
                    {
                        if (grpEl.ValueKind == JsonValueKind.String) def.Group = grpEl.GetString();
                        else if (grpEl.ValueKind == JsonValueKind.Object && grpEl.TryGetProperty("kind", out var k))
                            def.Group = k.GetString();
                    }

                    if (item.TryGetProperty("problemMatcher", out var pmEl))
                    {
                        if (pmEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pmEl.GetString()))
                            def.ProblemMatchers.Add(pmEl.GetString()!);
                        else if (pmEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var pm in pmEl.EnumerateArray())
                            {
                                if (pm.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pm.GetString()))
                                    def.ProblemMatchers.Add(pm.GetString()!);
                            }
                        }
                    }

                    tasks.Add(def);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, $"TaskRunnerService.LoadTasks({file})");
            }
        }

        return tasks;
    }

    public static string SubstituteVariables(string input, string workspacePath, string? activeFile = null)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var result = input.Replace("${workspaceFolder}", workspacePath, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(activeFile))
        {
            result = result
                .Replace("${file}", activeFile, StringComparison.OrdinalIgnoreCase)
                .Replace("${fileBasename}", Path.GetFileName(activeFile), StringComparison.OrdinalIgnoreCase)
                .Replace("${fileDirname}", Path.GetDirectoryName(activeFile) ?? workspacePath, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public static List<TaskProblemDiagnostic> ParseProblems(
        string output,
        string workspacePath,
        IReadOnlyCollection<string>? matchers = null)
    {
        var problems = new List<TaskProblemDiagnostic>();
        if (string.IsNullOrWhiteSpace(output)) return problems;

        var useAll = matchers == null || matchers.Count == 0;
        bool wantMs = useAll || matchers!.Any(m => m.Equals("$msCompile", StringComparison.OrdinalIgnoreCase));
        bool wantTsc = useAll || matchers!.Any(m => m.Equals("$tsc", StringComparison.OrdinalIgnoreCase));
        bool wantGcc = useAll || matchers!.Any(m => m.Equals("$gcc", StringComparison.OrdinalIgnoreCase));

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            TaskProblemDiagnostic? diag = null;

            if (wantTsc) diag = TryMatch(TscColonRegex, rawLine, workspacePath, "tsc");
            if (diag == null && (wantMs || wantTsc)) diag = TryMatch(MsCompileRegex, rawLine, workspacePath, wantTsc && !wantMs ? "tsc" : "msbuild");
            if (diag == null && wantGcc) diag = TryMatch(GccRegex, rawLine, workspacePath, "compiler");

            if (diag != null) problems.Add(diag);
            if (problems.Count >= 200) break;
        }

        return problems;
    }

    public static async Task<TaskRunResult> RunTaskAsync(
        string workspacePath,
        WorkspaceTaskDefinition task,
        string? activeFile = null,
        Action<string, bool>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceTrustService.AllowsRunCommand(workspacePath))
        {
            return new TaskRunResult
            {
                Label = task.Label,
                Success = false,
                ExitCode = -1,
                Error = "Tasks are disabled until this folder is trusted."
            };
        }

        var cwd = string.IsNullOrWhiteSpace(task.Cwd)
            ? workspacePath
            : SubstituteVariables(task.Cwd, workspacePath, activeFile);
        if (!Directory.Exists(cwd)) cwd = workspacePath;

        var cmd = SubstituteVariables(task.Command, workspacePath, activeFile);
        var args = task.Args.Select(a => SubstituteVariables(a, workspacePath, activeFile)).ToList();
        var fullCommand = args.Count > 0 ? $"{cmd} {string.Join(" ", args)}" : cmd;

        var captured = new StringBuilder();
        onOutput?.Invoke($"\n[Task: {task.Label}] {fullCommand}\n", false);

        var exec = await TerminalService.ExecuteCommandAsync(
            fullCommand,
            cwd,
            (line, isErr) =>
            {
                lock (captured)
                {
                    if (captured.Length < 120_000) captured.AppendLine(line);
                }
                onOutput?.Invoke(line + "\n", isErr);
            },
            timeoutMs: 300_000);

        var combined = captured.Length > 0 ? captured.ToString() : $"{exec.Output}\n{exec.Error}";
        var problems = ParseProblems(combined, workspacePath, task.ProblemMatchers);

        return new TaskRunResult
        {
            Label = task.Label,
            Success = exec.ExitCode == 0,
            ExitCode = exec.ExitCode,
            Problems = problems
        };
    }

    private static TaskProblemDiagnostic? TryMatch(Regex regex, string line, string workspacePath, string source)
    {
        var m = regex.Match(line);
        if (!m.Success) return null;

        var rawFile = m.Groups["file"].Value.Trim();
        if (string.IsNullOrEmpty(rawFile)) return null;
        var fullPath = Path.IsPathRooted(rawFile)
            ? rawFile
            : Path.GetFullPath(Path.Combine(workspacePath, rawFile));

        var lineNum = int.TryParse(m.Groups["line"].Value, out var l) ? Math.Max(1, l) : 1;
        var colNum = int.TryParse(m.Groups["col"].Value, out var c) ? Math.Max(1, c) : 1;
        var sevText = m.Groups["sev"].Value.ToLowerInvariant();
        var severity = sevText.Contains("error", StringComparison.Ordinal) ? 1
            : (sevText.Contains("warn", StringComparison.Ordinal) ? 2 : 3);

        return new TaskProblemDiagnostic
        {
            Path = fullPath,
            Line = lineNum,
            Column = colNum,
            Severity = severity,
            Code = m.Groups["code"].Success ? m.Groups["code"].Value.Trim() : "",
            Message = m.Groups["msg"].Value.Trim(),
            Source = source
        };
    }
}

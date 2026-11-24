using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Services;

namespace VoidCode.Agent.Tools;

/// <summary>
/// Runs one tool call and returns its text output plus, for the mutating file tools, the
/// Checkpoint id and path that the turn reports. Moved verbatim out of AgentEngine; the per-tool
/// <c>ITool</c> split is the follow-up that will replace this switch.
/// </summary>
internal static class ToolExecutor
{
    internal static async Task<(string output, string? checkpointId, string? modifiedFilePath)> ExecuteAsync(
        string name,
        string argsJson,
        string workspacePath,
        Action<string, object> emitEvent,
        Action<(string path, string dataUri, string mime)>? onImage = null,
        TurnTransaction? turn = null,
        AppSettings? settings = null,
        bool autoVerify = false,
        CancellationToken cancellationToken = default)
    {
        JsonNode? argsNode = null;
        try
        {
            argsNode = JsonNode.Parse(argsJson);
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "AgentEngine.ParseToolArguments"); }

        switch (name)
        {
            case "read_file":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                int? startLine = argsNode?["start_line"]?.GetValue<int>();
                int? endLine = argsNode?["end_line"]?.GetValue<int>();
                var fullPath = ResolvePath(workspacePath, relPath);
                if (!File.Exists(fullPath)) return ($"Error: File '{relPath}' does not exist.", null, null);

                // Images and binaries are not text; hand the agent a precise next step instead of garbage.
                var guard = FileSystemService.DescribeNonTextFile(fullPath);
                if (guard.handled) return (guard.message, null, null);

                var text = FileSystemService.ReadFileWithLineNumbers(fullPath, startLine, endLine);
                text = ToolOutputPolicy.Truncate(text, ToolOutputPolicy.ReadFileMaxChars, "use start_line/end_line to read more");
                return (ToolOutputPolicy.WrapUntrusted($"file:{relPath}", text), null, null);
            }

            case "view_image":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                var fullPath = ResolvePath(workspacePath, relPath);
                if (!File.Exists(fullPath)) return ($"Error: Image '{relPath}' does not exist.", null, null);
                if (!FileSystemService.IsImageFile(fullPath))
                    return ($"Error: '{relPath}' is not a supported image (png, jpg, jpeg, gif, webp, bmp, ico).", null, null);

                var dataUri = FileSystemService.GetImageDataUri(fullPath, out var sizeBytes);
                var mime = "image/png";
                var semi = dataUri.IndexOf(';');
                if (semi > 5) mime = dataUri.Substring(5, semi - 5);

                onImage?.Invoke((fullPath, dataUri, mime));
                emitEvent("agent_image", new { path = fullPath, dataUri, sizeBytes, mime });
                return ($"Loaded image '{relPath}' ({sizeBytes:N0} bytes). It is now visible to you in the next turn.", null, null);
            }

            case "write_file":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                var content = argsNode?["content"]?.GetValue<string>() ?? "";
                var fullPath = ResolvePath(workspacePath, relPath);

                turn?.SnapshotBeforeMutation(fullPath, File.Exists(fullPath) ? "edit" : "create");
                var cp = FileSystemService.CreateCheckpoint(fullPath, "Wrote");
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await Task.Run(() => FileSystemService.WriteFile(fullPath, content));

                var writeMsg = $"Successfully wrote {content.Length} chars ({content.Split('\n').Length} lines) to '{relPath}'.";
                writeMsg += AppendPostEditDiagnostics(fullPath, workspacePath, autoVerify);
                return (writeMsg, cp.IsRestorable ? cp.Id : null, fullPath);
            }

            case "patch_file":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                var search = argsNode?["search"]?.GetValue<string>() ?? "";
                var replace = argsNode?["replace"]?.GetValue<string>() ?? "";
                var fullPath = ResolvePath(workspacePath, relPath);

                if (File.Exists(fullPath)) turn?.SnapshotBeforeMutation(fullPath, "edit");
                var (success, message, cp) = FileSystemService.PatchFileSmart(fullPath, search, replace);
                if (success) message += AppendPostEditDiagnostics(fullPath, workspacePath, autoVerify);
                return (message, cp?.IsRestorable == true ? cp.Id : null, success ? fullPath : null);
            }

            case "multi_patch_file":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                var fullPath = ResolvePath(workspacePath, relPath);
                var edits = UnifiedDiffService.ParseMultiPatchEdits(argsNode);
                if (edits.Count == 0)
                    return ("Error: 'edits' must be a non-empty array of { search, replace } objects.", null, null);

                if (File.Exists(fullPath)) turn?.SnapshotBeforeMutation(fullPath, "edit");
                var (success, message, cp, _, _) = FileSystemService.MultiPatchFileSmart(fullPath, edits);
                if (success) message += AppendPostEditDiagnostics(fullPath, workspacePath, autoVerify);
                return (message, cp?.IsRestorable == true ? cp.Id : null, success ? fullPath : null);
            }

            case "delete_file":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                var fullPath = ResolvePath(workspacePath, relPath);
                if (!File.Exists(fullPath)) return ($"Error: File '{relPath}' does not exist.", null, null);

                turn?.SnapshotBeforeMutation(fullPath, "delete");
                var cp = FileSystemService.CreateCheckpoint(fullPath, "Deleted");
                File.Delete(fullPath);
                var undoNote = cp.IsRestorable ? $"undo checkpoint {cp.Id} created" : "file exceeded the inline checkpoint limit";
                return ($"Deleted '{relPath}' ({undoNote}).", cp.IsRestorable ? cp.Id : null, null);
            }

            case "move_file":
            {
                var sourceRel = argsNode?["source_path"]?.GetValue<string>() ?? "";
                var destRel = argsNode?["destination_path"]?.GetValue<string>() ?? "";
                var srcFull = ResolvePath(workspacePath, sourceRel);
                var dstFull = ResolvePath(workspacePath, destRel);

                if (!File.Exists(srcFull) && !Directory.Exists(srcFull))
                {
                    return ($"Error: Source '{sourceRel}' does not exist.", null, null);
                }

                var dstDir = Path.GetDirectoryName(dstFull);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                if (File.Exists(srcFull))
                {
                    turn?.SnapshotBeforeMutation(srcFull, "move_from");
                    turn?.SnapshotBeforeMutation(dstFull, "move_to");
                    File.Move(srcFull, dstFull, true);
                    return ($"Moved file '{sourceRel}' to '{destRel}'.", null, dstFull);
                }
                else
                {
                    turn?.RecordIrreversible("move_file", $"Moved directory {sourceRel} -> {destRel}");
                    Directory.Move(srcFull, dstFull);
                    return ($"Moved directory '{sourceRel}' to '{destRel}'.", null, null);
                }
            }

            case "list_dir":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                var fullPath = ResolvePath(workspacePath, relPath);
                if (!Directory.Exists(fullPath)) return ($"Error: Directory '{relPath}' not found.", null, null);

                var entries = Directory.GetFileSystemEntries(fullPath);
                var sb = new StringBuilder();
                sb.AppendLine($"Contents of {fullPath} ({entries.Length} entries):");
                foreach (var entry in entries)
                {
                    if (Directory.Exists(entry))
                    {
                        sb.AppendLine($"  [DIR]  {Path.GetFileName(entry)}/");
                    }
                    else
                    {
                        var fi = new FileInfo(entry);
                        sb.AppendLine($"  [FILE] {fi.Name} ({fi.Length:N0} B)");
                    }
                }
                return (sb.ToString(), null, null);
            }

            case "search_code":
            {
                var pattern = argsNode?["pattern"]?.GetValue<string>() ?? "";
                var fileFilter = argsNode?["file_filter"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(pattern)) return ("Error: Empty search pattern.", null, null);
                var res = FileSystemService.SearchCode(workspacePath, pattern, fileFilter);
                return (ToolOutputPolicy.Truncate(res, ToolOutputPolicy.SearchMaxChars, "narrow pattern or file_filter"), null, null);
            }

            case "search_codebase":
            {
                var query = argsNode?["query"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(query)) return ("Error: Empty search query.", null, null);
                var results = await CodebaseIndexService.SearchAsync(workspacePath, query, 6);
                if (results.Count == 0) return ($"No codebase index matches found for query: '{query}'", null, null);

                var sb = new StringBuilder();
                sb.AppendLine($"Codebase index results for '{query}' ({results.Count} matches):");
                foreach (var r in results)
                {
                    var symText = r.Symbols.Count > 0 ? $" [Symbols: {string.Join(", ", r.Symbols)}]" : "";
                    sb.AppendLine($"--- {r.FilePath}:{r.StartLine}-{r.EndLine}{symText} ---");
                    sb.AppendLine(r.Preview);
                    sb.AppendLine();
                }
                return (sb.ToString(), null, null);
            }

            case "run_command":
            {
                var cmd = argsNode?["command"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(cmd)) return ("Error: No command provided.", null, null);
                // Restricted mode never runs workspace commands.
                if (!WorkspaceTrustService.AllowsRunCommand(workspacePath)) return ("Error: run_command is disabled because this folder is not trusted. Ask the user to trust the folder first.", null, null);

                emitEvent("terminal_stream", new { text = $"\nPS {workspacePath}> {cmd}\n", isError = false });
                turn?.RecordIrreversible("run_command", cmd);

                var result = await TerminalService.ExecuteCommandAsync(
                    cmd,
                    workspacePath,
                    (line, isErr) => emitEvent("terminal_stream", new { text = line + "\n", isError = isErr })
                );

                var combined = $"Exit code: {result.ExitCode}\nOutput:\n{result.Output}\n{(string.IsNullOrEmpty(result.Error) ? "" : "Stderr:\n" + result.Error)}";
                combined = ToolOutputPolicy.Truncate(combined, ToolOutputPolicy.CommandMaxChars, "command output truncated");
                return (combined, null, null);
            }

            case "git_status_diff":
            {
                var fileTarget = argsNode?["path"]?.GetValue<string>() ?? "";
                var cmd = string.IsNullOrWhiteSpace(fileTarget)
                    ? "git status --short --branch; Write-Output '--- DIFF STAT ---'; git diff --stat"
                    : $"git status --short --branch; Write-Output '--- DIFF ---'; git diff -- \"{fileTarget}\"";

                var result = await TerminalService.ExecuteCommandAsync(cmd, workspacePath, null, 30000, false);
                var combined = $"Exit code: {result.ExitCode}\n{result.Output}\n{result.Error}".Trim();
                combined = ToolOutputPolicy.Truncate(combined, ToolOutputPolicy.GitDiffMaxChars, "git diff truncated");
                return (combined, null, null);
            }

            case "get_diagnostics":
            {
                string diagCmd;
                if (Directory.GetFiles(workspacePath, "*.csproj", SearchOption.TopDirectoryOnly).Any() ||
                    Directory.GetFiles(workspacePath, "*.sln", SearchOption.TopDirectoryOnly).Any())
                {
                    diagCmd = "dotnet build --no-restore /nologo /clp:NoSummary";
                }
                else if (File.Exists(Path.Combine(workspacePath, "Cargo.toml")))
                {
                    diagCmd = "cargo check --message-format=short";
                }
                else if (File.Exists(Path.Combine(workspacePath, "tsconfig.json")))
                {
                    diagCmd = "npx tsc --noEmit";
                }
                else
                {
                    diagCmd = "git status --short";
                }

                emitEvent("terminal_stream", new { text = $"\n[Diagnostics] PS {workspacePath}> {diagCmd}\n", isError = false });
                turn?.RecordIrreversible("get_diagnostics", diagCmd);
                var res = await TerminalService.ExecuteCommandAsync(
                    diagCmd,
                    workspacePath,
                    (line, isErr) => emitEvent("terminal_stream", new { text = line + "\n", isError = isErr }),
                    60000,
                    false
                );
                var diagOutput = $"Command: {diagCmd}\nExitCode: {res.ExitCode}\n{res.Output}\n{res.Error}".Trim();
                return (ToolOutputPolicy.Truncate(diagOutput, ToolOutputPolicy.DiagnosticsMaxChars, "diagnostics truncated"), null, null);
            }

            case "inspect_binary":
            {
                var relPath = argsNode?["path"]?.GetValue<string>() ?? "";
                var fullPath = ResolvePath(workspacePath, relPath);
                return (FileSystemService.InspectBinary(fullPath), null, null);
            }

            case "web_fetch":
            {
                var url = argsNode?["url"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(url)) return ("Error: URL is required.", null, null);
                try
                {
                    turn?.RecordIrreversible("web_fetch", url);
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VoidCode/2.0");
                    using var resp = await AgentHttp.Client.SendAsync(req);
                    var raw = await resp.Content.ReadAsStringAsync();
                    var stripped = Regex.Replace(raw, "<(script|style)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    stripped = Regex.Replace(stripped, "<[^>]+>", " ");
                    stripped = Regex.Replace(stripped, "\\s+", " ").Trim();
                    stripped = ToolOutputPolicy.Truncate(stripped, ToolOutputPolicy.WebFetchMaxChars, "web page truncated");
                    return (ToolOutputPolicy.WrapUntrusted($"web:{url}", $"Status {(int)resp.StatusCode} from {url}:\n{stripped}"), null, null);
                }
                catch (Exception ex)
                {
                    return ($"Failed to fetch URL '{url}': {ex.Message}", null, null);
                }
            }

            case "save_memory":
            {
                var content = argsNode?["content"]?.GetValue<string>() ?? "";
                var scope = argsNode?["scope"]?.GetValue<string>() ?? "project";
                var category = argsNode?["category"]?.GetValue<string>() ?? "fact";
                var res = AgentMemoryService.Save(workspacePath, scope, category, content);
                return (res.Message, null, null);
            }

            case "delete_memory":
            {
                var id = argsNode?["id"]?.GetValue<string>() ?? "";
                var res = AgentMemoryService.Delete(workspacePath, id);
                return (res.Message, null, null);
            }

            case "glob_files":
            {
                var pattern = argsNode?["pattern"]?.GetValue<string>() ?? "**/*";
                var files = await Task.Run(() => FileSystemService.GlobFiles(workspacePath, pattern, 150), cancellationToken);
                if (files.Count == 0) return ($"No files matched pattern '{pattern}'.", null, null);
                var body = string.Join("\n", files);
                return ($"Matched {files.Count} file{(files.Count == 1 ? "" : "s")} for '{pattern}':\n{body}", null, null);
            }

            case "get_lsp_diagnostics":
            {
                var relPath = argsNode?["path"]?.GetValue<string>();
                var target = string.IsNullOrWhiteSpace(relPath) ? null : ResolvePath(workspacePath, relPath);
                var diagText = LspService.FormatCachedDiagnostics(target, workspacePath);
                return (diagText, null, null);
            }

            case "delegate_research":
            {
                var question = argsNode?["question"]?.GetValue<string>() ?? "";
                if (settings == null)
                    return ("Error: Model settings unavailable for research subagent.", null, null);
                var brief = await ResearchSubagent.RunAsync(question, workspacePath, settings, emitEvent, cancellationToken);
                return (ToolOutputPolicy.Truncate(brief, ToolOutputPolicy.ReadFileMaxChars, "research summary clipped"), null, null);
            }

            default:
                return ($"Error: Unknown tool '{name}'.", null, null);
        }
    }

    private static string AppendPostEditDiagnostics(string fullPath, string workspacePath, bool autoVerify)
    {
        if (!autoVerify) return "";
        try
        {
            var diags = LspService.GetCachedDiagnostics(fullPath);
            if (diags.Count == 0) return "";
            var formatted = LspService.FormatCachedDiagnostics(fullPath, workspacePath, 10);
            return $"\n\n[Live LSP Diagnostics for edited file]\n{formatted}";
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "ToolExecutor.AppendPostEditDiagnostics");
            return "";
        }
    }

    internal static string ResolvePath(string workspace, string relOrAbs)
    {
        if (string.IsNullOrWhiteSpace(relOrAbs)) return workspace;
        if (Path.IsPathRooted(relOrAbs)) return relOrAbs;
        return Path.Combine(workspace, relOrAbs);
    }

    internal static bool AccessesOutsideWorkspace(string toolName, string argumentsJson, string workspacePath)
    {
        if (toolName is not ("read_file" or "list_dir" or "inspect_binary")) return false;
        try
        {
            var args = JsonNode.Parse(argumentsJson);
            var rawPath = args?["path"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(rawPath)) return false;

            // PathPolicy resolves junctions/symlinks and rejects \\?\, UNC, ADS and
            // Trailing dots/spaces - anything it cannot prove is inside the workspace needs review.
            return !PathPolicy.IsInsideWorkspace(workspacePath, ResolvePath(workspacePath, rawPath));
        }
        catch
        {
            // Invalid paths should not bypass the review gate.
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VoidCode.Agent.Tools;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// Everything that turns editor state and [Referenced File: ...] markers into prompt
/// Text, including the bounds that keep one huge file from eating the context window.
/// </summary>
internal static class ContextBuilder
{
    // Bounds: an arbitrarily large file would blow the context window, so the
    // Active file is clipped and the workspace listing is capped. Everything
    // Omitted stays reachable through read_file.
    // Bounds: an arbitrarily large file would blow the context window, so the
    // Active file is clipped and the workspace listing is capped. Everything
    // Omitted stays reachable through read_file.
    internal const int MaxContextFileChars = 20000;
    internal const int MaxWorkspaceFiles = 400;
    internal const int MaxProjectRulesBytes = 16 * 1024;

    private static string SafeString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var s)) return s ?? string.Empty;
        return string.Empty;
    }

    // Accepts either a plain string or an object ({name, path, relativePath}) so
    // Workspace / open-file entries never throw and never crash the whole run.
    // Accepts either a plain string or an object ({name, path, relativePath}) so
    // Workspace / open-file entries never throw and never crash the whole run.
    private static string NodeToPath(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return string.Empty;
            case JsonValue value:
                return value.TryGetValue<string>(out var s) ? (s ?? string.Empty) : string.Empty;
            case JsonObject obj:
                foreach (var key in new[] { "relativePath", "path", "name" })
                {
                    if (obj[key] is JsonValue keyValue && keyValue.TryGetValue<string>(out var str) && !string.IsNullOrEmpty(str))
                        return str;
                }
                return string.Empty;
            default:
                return string.Empty;
        }
    }

    internal static string BuildEditorContextBlock(JsonObject? context)
    {
        if (context == null) return string.Empty;

        var sb = new StringBuilder();
        var activePath = SafeString(context["activePath"]);
        var language = SafeString(context["language"]);
        var selection = SafeString(context["selection"]);
        var content = SafeString(context["content"]);

        sb.AppendLine("=== EDITOR CONTEXT (already read for you, do not ask for it again) ===");

        if (context["openFiles"] is JsonArray openFiles && openFiles.Count > 0)
        {
            sb.AppendLine("Open editors:");
            foreach (var f in openFiles)
            {
                var path = NodeToPath(f);
                if (!string.IsNullOrEmpty(path)) sb.AppendLine($"  - {path}");
            }
        }

        if (context["workspaceFiles"] is JsonArray workspaceFiles && workspaceFiles.Count > 0)
        {
            var shown = Math.Min(workspaceFiles.Count, MaxWorkspaceFiles);
            sb.AppendLine($"Workspace files ({shown} shown - use read_file or search_code for anything else):");
            for (var i = 0; i < shown; i++)
            {
                var path = NodeToPath(workspaceFiles[i]);
                if (!string.IsNullOrEmpty(path)) sb.AppendLine($"  - {path}");
            }
        }

        if (!string.IsNullOrEmpty(activePath))
        {
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(language) ? $"Active file: {activePath}" : $"Active file: {activePath} ({language})");

            if (!string.IsNullOrEmpty(selection))
            {
                sb.AppendLine("Current selection:");
                sb.AppendLine("```");
                sb.AppendLine(selection);
                sb.AppendLine("```");
            }

            if (!string.IsNullOrEmpty(content))
            {
                var clipped = content.Length > MaxContextFileChars
                    ? content[..MaxContextFileChars] + "\n… (truncated - use read_file for the rest)"
                    : content;
                sb.AppendLine("Current file contents:");
                sb.AppendLine("```");
                sb.AppendLine(clipped);
                sb.AppendLine("```");
            }
        }
        else
        {
            sb.AppendLine("No file is open in the editor.");
        }

        sb.AppendLine("=== END EDITOR CONTEXT ===");
        return sb.ToString();
    }

    private static readonly Regex InlineImageRegex = new(
        @"\[Attached Image:\s*(data:image/[a-zA-Z0-9.+-]+;base64,[A-Za-z0-9+/=\r\n]+)\]",
        RegexOptions.Compiled);

    internal static string StripInlineImagePayloads(string text)
        => string.IsNullOrEmpty(text) ? text : InlineImageRegex.Replace(text, "[Clipboard Screenshot Attached]");

    internal static string EnrichPromptWithReferencedFiles(string prompt, string workspacePath, List<string> imageDataUris)
    {
        if (string.IsNullOrEmpty(prompt)) return string.Empty;

        prompt = InlineImageRegex.Replace(prompt, m =>
        {
            var uri = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(uri)) imageDataUris.Add(uri);
            return "[Clipboard Screenshot Attached]";
        });

        var matches = Regex.Matches(prompt, @"\[Referenced File:\s*(.+?)\]");
        if (matches.Count == 0) return prompt;

        var sb = new StringBuilder(prompt);
        sb.AppendLine("\n\n--- ATTACHED FILE CONTENTS ---");
        foreach (Match m in matches)
        {
            var rawPath = m.Groups[1].Value.Trim();
            var fullPath = ToolExecutor.ResolvePath(workspacePath, rawPath);
            if (!File.Exists(fullPath)) continue;

            try
            {
                // Images are not text: collect them as multimodal parts instead of reading bytes as lines.
                if (FileSystemService.IsImageFile(fullPath))
                {
                    imageDataUris.Add(FileSystemService.GetImageDataUri(fullPath, out _));
                    sb.AppendLine($"\n[Image attached: {fullPath}]");
                    continue;
                }

                if (FileSystemService.IsBinaryFile(fullPath))
                {
                    sb.AppendLine($"\n[Binary Inspection of {fullPath}]:");
                    sb.AppendLine(FileSystemService.InspectBinary(fullPath));
                }
                else
                {
                    sb.AppendLine($"\n[Contents of {fullPath}]:");
                    sb.AppendLine(FileSystemService.ReadFileWithLineNumbers(fullPath, 1, 300));
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "AgentEngine.ReadReferencedFile"); }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Loads root AGENTS.md plus .voidcode/rules/*.md (capped at 16 KB total)
    /// So repository-specific conventions are injected into every agent turn.
    /// </summary>
    internal static string LoadProjectRules(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return string.Empty;

        var candidates = new List<(string label, string fullPath)>();

        var agentsMd = Path.Combine(workspacePath, "AGENTS.md");
        if (File.Exists(agentsMd) && PathPolicy.IsInsideWorkspace(workspacePath, agentsMd))
        {
            candidates.Add(("AGENTS.md", agentsMd));
        }

        var rulesDir = Path.Combine(workspacePath, ".voidcode", "rules");
        if (Directory.Exists(rulesDir) && PathPolicy.IsInsideWorkspace(workspacePath, rulesDir))
        {
            try
            {
                var files = Directory.GetFiles(rulesDir, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (PathPolicy.IsInsideWorkspace(workspacePath, file))
                    {
                        candidates.Add(($".voidcode/rules/{Path.GetFileName(file)}", file));
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "ContextBuilder.LoadProjectRules.Enumerate");
            }
        }

        if (candidates.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("=== PROJECT RULES (repository instructions) ===");
        int bytesUsed = 0;

        foreach (var (label, fullPath) in candidates)
        {
            if (bytesUsed >= MaxProjectRulesBytes) break;
            try
            {
                var text = File.ReadAllText(fullPath, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var header = $"\n--- {label} ---\n";
                var remaining = MaxProjectRulesBytes - bytesUsed - Encoding.UTF8.GetByteCount(header);
                if (remaining <= 64) break;

                var textBytes = Encoding.UTF8.GetByteCount(text);
                if (textBytes > remaining)
                {
                    // Trim safely by chars until UTF-8 byte count fits within the 16 KB cap.
                    int approxChars = Math.Max(32, Math.Min(text.Length, remaining - 48));
                    var clipped = text[..approxChars];
                    while (clipped.Length > 0 && Encoding.UTF8.GetByteCount(clipped) > remaining - 48)
                    {
                        clipped = clipped[..Math.Max(0, clipped.Length - 32)];
                    }
                    text = clipped + "\n... [rules truncated at 16 KB cap]";
                }

                sb.Append(header);
                sb.AppendLine(text);
                bytesUsed += Encoding.UTF8.GetByteCount(header) + Encoding.UTF8.GetByteCount(text);
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, $"ContextBuilder.LoadProjectRules({label})");
            }
        }

        if (bytesUsed == 0) return string.Empty;
        sb.AppendLine("=== END PROJECT RULES ===");
        return sb.ToString();
    }
}

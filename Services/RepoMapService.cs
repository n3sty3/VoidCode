using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoidCode.Services;

/// <summary>
/// Builds a compact structural Repo Map (key directories, entry points, and top-level symbols)
/// so VoidAgent starts Turn 1 oriented in the codebase without burning 3-4 exploration turns.
/// </summary>
public static class RepoMapService
{
    private const int MaxFiles = 40;
    private const int MaxChars = 2800;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".rs", ".go", ".java", ".cpp", ".c", ".h", ".hpp", ".rb", ".php", ".swift", ".kt"
    };

    private static readonly Regex SymbolRegex = new(
        @"^\s*(?:export\s+)?(?:public|internal|private|protected)?\s*(?:static\s+)?(?:sealed\s+)?(?:abstract\s+)?(?:async\s+)?(?:class|interface|struct|record|enum|function|def|fn|type|struct)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    public static string BuildCompactMap(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return "";

        try
        {
            var files = FileSystemService.GlobFiles(workspacePath, "**/*", 300)
                .Where(f => CodeExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f.Count(c => c == '/'))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFiles)
                .ToList();

            if (files.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== REPO MAP (KEY FILES & TOP-LEVEL SYMBOLS) ===");

            foreach (var rel in files)
            {
                var full = Path.Combine(workspacePath, rel.Replace('/', Path.DirectorySeparatorChar));
                var symbols = ExtractTopSymbols(full, 6);
                var line = symbols.Count > 0
                    ? $"- {rel}: {string.Join(", ", symbols)}"
                    : $"- {rel}";

                if (sb.Length + line.Length + 2 > MaxChars)
                {
                    sb.AppendLine("... [repo map truncated; use glob_files / search_codebase for full listing]");
                    break;
                }
                sb.AppendLine(line);
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "RepoMapService.BuildCompactMap");
            return "";
        }
    }

    private static List<string> ExtractTopSymbols(string fullPath, int limit)
    {
        var results = new List<string>();
        try
        {
            if (!File.Exists(fullPath)) return results;
            var fi = new FileInfo(fullPath);
            if (fi.Length > 200_000) return results;

            int lineNum = 0;
            foreach (var line in File.ReadLines(fullPath))
            {
                lineNum++;
                if (lineNum > 220 || results.Count >= limit) break;
                var m = SymbolRegex.Match(line);
                if (m.Success)
                {
                    var name = m.Groups[1].Value;
                    if (!results.Contains(name, StringComparer.Ordinal))
                        results.Add(name);
                }
            }
        }
        catch (IOException) { /* ignore unreadable file */ }
        catch (UnauthorizedAccessException) { /* ignore inaccessible file */ }
        return results;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace VoidCode.Services;

public sealed class UnifiedDiffResult
{
    public int Added { get; init; }
    public int Removed { get; init; }
    public string DiffText { get; init; } = "";
    public string TargetPath { get; init; } = "";
}

/// <summary>
/// Pure-C# LCS line-by-line unified diff generator used for both pre-approval previews
/// and finished edit cards inside VoidAgent v2.
/// </summary>
public static class UnifiedDiffService
{
    private const int MaxLinesForFullLcs = 900;
    private const int MaxDiffChars = 8000;

    public static UnifiedDiffResult Compute(string before, string after, string targetPath = "", int contextLines = 3)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);

        // Trim common prefix and suffix first so even large files diff instantaneously.
        int prefix = 0;
        int minLen = Math.Min(oldLines.Length, newLines.Length);
        while (prefix < minLen && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
            prefix++;

        int suffix = 0;
        while (suffix < (minLen - prefix) &&
               string.Equals(oldLines[oldLines.Length - 1 - suffix], newLines[newLines.Length - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        var midOld = oldLines[prefix..(oldLines.Length - suffix)];
        var midNew = newLines[prefix..(newLines.Length - suffix)];

        if (midOld.Length == 0 && midNew.Length == 0)
            return new UnifiedDiffResult { Added = 0, Removed = 0, DiffText = "", TargetPath = targetPath };

        var ops = new List<(char Op, int OldIdx, int NewIdx, string Line)>();

        if (midOld.Length <= MaxLinesForFullLcs && midNew.Length <= MaxLinesForFullLcs &&
            (long)midOld.Length * midNew.Length <= 250_000)
        {
            BuildLcsOps(midOld, midNew, prefix, ops);
        }
        else
        {
            // Fallback for massive whole-file rewrites: emit bounded removed + added block.
            for (int i = 0; i < midOld.Length; i++)
                ops.Add(('-', prefix + i, -1, midOld[i]));
            for (int j = 0; j < midNew.Length; j++)
                ops.Add(('+', -1, prefix + j, midNew[j]));
        }

        int added = ops.Count(o => o.Op == '+');
        int removed = ops.Count(o => o.Op == '-');

        // Reconstruct compact unified diff with context around edits.
        var fullOps = new List<(char Op, int OldLine, int NewLine, string Line)>();
        int ctxStart = Math.Max(0, prefix - contextLines);
        for (int i = ctxStart; i < prefix; i++)
            fullOps.Add((' ', i + 1, i + 1, oldLines[i]));

        foreach (var o in ops)
            fullOps.Add((o.Op, o.OldIdx >= 0 ? o.OldIdx + 1 : -1, o.NewIdx >= 0 ? o.NewIdx + 1 : -1, o.Line));

        int tailEnd = Math.Min(oldLines.Length, oldLines.Length - suffix + contextLines);
        for (int i = oldLines.Length - suffix; i < tailEnd; i++)
        {
            int newLineNum = (newLines.Length - suffix) + (i - (oldLines.Length - suffix)) + 1;
            fullOps.Add((' ', i + 1, newLineNum, oldLines[i]));
        }

        var sb = new StringBuilder();
        var hunkHeader = $"@@ -{ctxStart + 1},{Math.Max(1, tailEnd - ctxStart)} +{ctxStart + 1},{Math.Max(1, tailEnd - ctxStart - removed + added)} @@";
        sb.AppendLine(hunkHeader);

        // Emit only lines within `contextLines` of a changed line.
        var keep = new bool[fullOps.Count];
        for (int i = 0; i < fullOps.Count; i++)
        {
            if (fullOps[i].Op != ' ')
            {
                int lo = Math.Max(0, i - contextLines);
                int hi = Math.Min(fullOps.Count - 1, i + contextLines);
                for (int k = lo; k <= hi; k++) keep[k] = true;
            }
        }

        bool lastKept = true;
        for (int i = 0; i < fullOps.Count; i++)
        {
            if (!keep[i])
            {
                if (lastKept) sb.AppendLine(" ...");
                lastKept = false;
                continue;
            }
            lastKept = true;
            var item = fullOps[i];
            sb.Append(item.Op).Append(' ').AppendLine(item.Line);
            if (sb.Length > MaxDiffChars)
            {
                sb.AppendLine("... [diff preview truncated]");
                break;
            }
        }

        return new UnifiedDiffResult
        {
            Added = added,
            Removed = removed,
            DiffText = sb.ToString().TrimEnd(),
            TargetPath = targetPath
        };
    }

    public static UnifiedDiffResult? BuildPreviewForTool(string toolName, string argsJson, string workspacePath)
    {
        try
        {
            var args = JsonNode.Parse(argsJson);
            if (args == null) return null;

            var relPath = args["path"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(relPath)) return null;
            var fullPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(workspacePath, relPath);

            if (string.Equals(toolName, "write_file", StringComparison.OrdinalIgnoreCase))
            {
                var before = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
                var after = args["content"]?.GetValue<string>() ?? "";
                return Compute(before, after, relPath);
            }

            if (string.Equals(toolName, "patch_file", StringComparison.OrdinalIgnoreCase))
            {
                var search = args["search"]?.GetValue<string>() ?? "";
                var replace = args["replace"]?.GetValue<string>() ?? "";
                if (File.Exists(fullPath))
                {
                    var before = File.ReadAllText(fullPath);
                    var sim = FileSystemService.SimulateMultiPatch(before, new[] { (search, replace) });
                    if (sim.Success)
                        return Compute(before, sim.UpdatedContent, relPath);
                }
                return Compute(search, replace, relPath);
            }

            if (string.Equals(toolName, "multi_patch_file", StringComparison.OrdinalIgnoreCase))
            {
                var edits = ParseMultiPatchEdits(args);
                if (edits.Count == 0) return null;
                if (File.Exists(fullPath))
                {
                    var before = File.ReadAllText(fullPath);
                    var sim = FileSystemService.SimulateMultiPatch(before, edits);
                    if (sim.Success)
                        return Compute(before, sim.UpdatedContent, relPath);
                }
                var joinedBefore = string.Join("\n---\n", edits.Select(e => e.Search));
                var joinedAfter = string.Join("\n---\n", edits.Select(e => e.Replace));
                return Compute(joinedBefore, joinedAfter, relPath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "UnifiedDiffService.BuildPreviewForTool");
        }
        return null;
    }

    internal static List<(string Search, string Replace)> ParseMultiPatchEdits(JsonNode? args)
    {
        var list = new List<(string, string)>();
        if (args?["edits"] is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var s = obj["search"]?.GetValue<string>() ?? "";
            var r = obj["replace"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(s))
                list.Add((s, r));
        }
        return list;
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Split('\n');
    }

    private static void BuildLcsOps(
        string[] a,
        string[] b,
        int baseOffset,
        List<(char Op, int OldIdx, int NewIdx, string Line)> ops)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                ops.Add((' ', baseOffset + x, baseOffset + y, a[x]));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add(('-', baseOffset + x, -1, a[x]));
                x++;
            }
            else
            {
                ops.Add(('+', -1, baseOffset + y, b[y]));
                y++;
            }
        }

        while (x < n)
        {
            ops.Add(('-', baseOffset + x, -1, a[x]));
            x++;
        }
        while (y < m)
        {
            ops.Add(('+', -1, baseOffset + y, b[y]));
            y++;
        }
    }
}

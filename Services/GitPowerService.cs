using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

public sealed class GitGraphCommit
{
    [JsonPropertyName("graph")]
    public string Graph { get; set; } = "";

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("shortHash")]
    public string ShortHash { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("refs")]
    public List<string> Refs { get; set; } = new();

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";
}

public sealed class GitStashItem
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class GitPowerResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("output")]
    public string? Output { get; set; }
}

public sealed class GitGutterHunk
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "modified";

    [JsonPropertyName("startLine")]
    public int StartLine { get; init; } = 1;

    [JsonPropertyName("endLine")]
    public int EndLine { get; init; } = 1;

    [JsonPropertyName("originalText")]
    public string OriginalText { get; init; } = "";
}

public sealed class GitGutterReport
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("hunks")]
    public List<GitGutterHunk> Hunks { get; init; } = new();
}

/// <summary>
/// Cross-platform Git power operations: repository cloning, commit graph log,
/// commit inspection, stash/pop management, and hunk-level staging/reverting.
/// </summary>
public static class GitPowerService
{
    private static readonly SystemProcessRunner Runner = new();
    private static readonly Regex SafeHashRegex = new("^[0-9a-fA-F]{4,40}$", RegexOptions.Compiled);
    private static readonly Regex SafeStashRefRegex = new(@"^stash@\{\d+\}$", RegexOptions.Compiled);

    public static bool IsValidCloneUrl(string? url)
    {
        var clean = (url ?? "").Trim();
        if (string.IsNullOrEmpty(clean) || clean.StartsWith('-')) return false;
        if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(clean, UriKind.Absolute, out _);
        }
        return Regex.IsMatch(clean, @"^[A-Za-z0-9._-]+@[A-Za-z0-9._-]+:[A-Za-z0-9._/\-]+$");
    }

    public static async Task<GitPowerResult> CloneAsync(string url, string destinationParentDir, string? folderName = null, CancellationToken ct = default)
    {
        if (!IsValidCloneUrl(url))
            return new GitPowerResult { Ok = false, Message = "Invalid Git repository URL. Use https://, ssh://, or git@host:org/repo.git." };

        var parent = string.IsNullOrWhiteSpace(destinationParentDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects")
            : destinationParentDir.Trim();

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex)
        {
            return new GitPowerResult { Ok = false, Message = $"Could not access destination folder: {ex.Message}" };
        }

        var repoName = !string.IsNullOrWhiteSpace(folderName)
            ? folderName.Trim()
            : InferRepoFolderName(url);
        if (string.IsNullOrWhiteSpace(repoName) || repoName.Contains("..", StringComparison.Ordinal) || repoName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new GitPowerResult { Ok = false, Message = "Invalid target folder name." };

        var targetDir = Path.Combine(parent, repoName);
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            return new GitPowerResult { Ok = false, Message = $"Folder '{targetDir}' already exists and is not empty." };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(120_000);
        var res = await Runner.RunAsync("git", new[] { "clone", "--", url.Trim(), targetDir }, parent, null, cts.Token);
        if (res.ExitCode != 0)
        {
            var err = string.IsNullOrWhiteSpace(res.Stderr) ? res.Stdout : res.Stderr;
            return new GitPowerResult { Ok = false, Message = $"git clone failed: {err.Trim()}" };
        }

        return new GitPowerResult
        {
            Ok = true,
            Path = targetDir,
            Message = $"Cloned '{repoName}' to {targetDir}."
        };
    }

    public static async Task<List<GitGraphCommit>> GetCommitGraphAsync(string workspacePath, int maxCount = 60, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return new List<GitGraphCommit>();

        var cap = Math.Clamp(maxCount, 5, 200);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(15_000);

        var res = await Runner.RunAsync(
            "git",
            new[] { "log", "--all", "--graph", "--date=relative", $"--max-count={cap}", "--format=VCSEP%H%x1f%h%x1f%an%x1f%ar%x1f%D%x1f%s" },
            workspacePath,
            null,
            cts.Token);

        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.Stdout))
            return new List<GitGraphCommit>();

        return ParseCommitGraphOutput(res.Stdout);
    }

    internal static List<GitGraphCommit> ParseCommitGraphOutput(string stdout)
    {
        var list = new List<GitGraphCommit>();
        var lines = (stdout ?? "").Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            var markerIdx = line.IndexOf("VCSEP", StringComparison.Ordinal);
            if (markerIdx < 0) continue;
            var graph = line[..markerIdx].TrimEnd();
            var payload = line[(markerIdx + 5)..];
            var fields = payload.Split('\x1f');
            if (fields.Length < 6) continue;

            var refs = fields[4]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => r.Length > 0)
                .ToList();

            list.Add(new GitGraphCommit
            {
                Graph = graph,
                Hash = fields[0].Trim(),
                ShortHash = fields[1].Trim(),
                Author = fields[2].Trim(),
                Date = fields[3].Trim(),
                Refs = refs,
                Subject = fields[5].Trim()
            });
        }
        return list;
    }

    public static async Task<GitPowerResult> ShowCommitAsync(string workspacePath, string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hash) || !SafeHashRegex.IsMatch(hash.Trim()))
            return new GitPowerResult { Ok = false, Message = "Invalid commit hash." };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(15_000);
        var res = await Runner.RunAsync("git", new[] { "show", "--stat", "--patch", "--no-color", hash.Trim() }, workspacePath, null, cts.Token);
        if (res.ExitCode != 0)
            return new GitPowerResult { Ok = false, Message = res.Stderr.Trim() };

        var text = res.Stdout;
        if (text.Length > 45_000) text = text[..45_000] + "\n... [commit diff truncated]";
        return new GitPowerResult { Ok = true, Output = text };
    }

    public static async Task<List<GitStashItem>> ListStashesAsync(string workspacePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return new List<GitStashItem>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(8_000);
        var res = await Runner.RunAsync("git", new[] { "stash", "list", "--format=%gd%x1f%gs" }, workspacePath, null, cts.Token);
        var list = new List<GitStashItem>();
        if (res.ExitCode != 0) return list;

        foreach (var line in res.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\x1f');
            if (parts.Length >= 2)
                list.Add(new GitStashItem { Ref = parts[0].Trim(), Message = parts[1].Trim() });
        }
        return list;
    }

    public static async Task<GitPowerResult> PushStashAsync(string workspacePath, string? message = null, CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "push", "--include-untracked" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message.Trim());
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(15_000);
        var res = await Runner.RunAsync("git", args, workspacePath, null, cts.Token);
        return new GitPowerResult
        {
            Ok = res.ExitCode == 0,
            Message = (res.ExitCode == 0 ? res.Stdout : res.Stderr).Trim()
        };
    }

    public static async Task<GitPowerResult> PopStashAsync(string workspacePath, string? stashRef = null, CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "pop" };
        if (!string.IsNullOrWhiteSpace(stashRef))
        {
            var clean = stashRef.Trim();
            if (!SafeStashRefRegex.IsMatch(clean))
                return new GitPowerResult { Ok = false, Message = "Invalid stash reference." };
            args.Add(clean);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(15_000);
        var res = await Runner.RunAsync("git", args, workspacePath, null, cts.Token);
        return new GitPowerResult
        {
            Ok = res.ExitCode == 0,
            Message = (res.ExitCode == 0 ? (string.IsNullOrWhiteSpace(res.Stdout) ? "Stash popped." : res.Stdout) : res.Stderr).Trim()
        };
    }

    public static async Task<GitPowerResult> ApplyHunkPatchAsync(string workspacePath, string patchText, bool cached = true, bool reverse = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(patchText))
            return new GitPowerResult { Ok = false, Message = "Patch text is empty." };

        var args = new List<string> { "apply", "--recount", "--whitespace=nowarn" };
        if (cached) args.Add("--cached");
        if (reverse) args.Add("--reverse");
        args.Add("-");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10_000);
        var res = await Runner.RunAsync("git", args, workspacePath, patchText, cts.Token);
        return new GitPowerResult
        {
            Ok = res.ExitCode == 0,
            Message = res.ExitCode == 0 ? "Hunk applied cleanly." : $"git apply failed: {res.Stderr.Trim()}"
        };
    }

    private static readonly Regex GutterHunkHeaderRegex = new(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Compiled);

    public static List<GitGutterHunk> ParseUnifiedDiffGutterHunks(string diffText)
    {
        var hunks = new List<GitGutterHunk>();
        if (string.IsNullOrWhiteSpace(diffText)) return hunks;

        var lines = diffText.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var m = GutterHunkHeaderRegex.Match(lines[i]);
            if (!m.Success)
            {
                i++;
                continue;
            }

            int oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            int newStart = int.Parse(m.Groups[3].Value);
            int newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
            i++;

            var removed = new List<string>();
            while (i < lines.Length && !lines[i].StartsWith("@@ ", StringComparison.Ordinal) && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var l = lines[i];
                if (l.StartsWith("-", StringComparison.Ordinal) && !l.StartsWith("---", StringComparison.Ordinal))
                    removed.Add(l.Length > 1 ? l[1..] : "");
                i++;
            }

            string kind;
            int startLine;
            int endLine;
            if (oldCount == 0 && newCount > 0)
            {
                kind = "added";
                startLine = Math.Max(1, newStart);
                endLine = Math.Max(startLine, newStart + newCount - 1);
            }
            else if (newCount == 0 && oldCount > 0)
            {
                kind = "deleted";
                startLine = Math.Max(1, newStart);
                endLine = startLine;
            }
            else
            {
                kind = "modified";
                startLine = Math.Max(1, newStart);
                endLine = Math.Max(startLine, newStart + Math.Max(1, newCount) - 1);
            }

            hunks.Add(new GitGutterHunk
            {
                Kind = kind,
                StartLine = startLine,
                EndLine = endLine,
                OriginalText = string.Join("\n", removed)
            });
        }
        return hunks;
    }

    public static async Task<GitGutterReport> GetFileHeadDiffHunksAsync(string workspacePath, string filePath, CancellationToken ct = default)
    {
        var report = new GitGutterReport { Path = filePath ?? "" };
        if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(filePath) || !Directory.Exists(workspacePath))
            return report;

        try
        {
            var rel = Path.IsPathRooted(filePath)
                ? Path.GetRelativePath(workspacePath, filePath).Replace('\\', '/')
                : filePath.Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal)) return report;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5_000);
            var res = await Runner.RunAsync("git", new[] { "diff", "--no-color", "-U0", "HEAD", "--", rel }, workspacePath, null, cts.Token);
            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.Stdout))
                return report;

            return new GitGutterReport
            {
                Path = filePath,
                Hunks = ParseUnifiedDiffGutterHunks(res.Stdout)
            };
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "GitPowerService.GetFileHeadDiffHunksAsync");
            return report;
        }
    }

    private static string InferRepoFolderName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var lastSlash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf(':'));
        var seg = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        if (seg.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            seg = seg[..^4];
        return seg;
    }
}

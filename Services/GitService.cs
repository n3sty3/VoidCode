using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VoidCode.Services;

public class GitChangeItem
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = ""; // "M", "A", "D", "R", "??"

    [JsonPropertyName("staged")]
    public bool Staged { get; set; }

    [JsonPropertyName("statusDesc")]
    public string StatusDesc { get; set; } = "";
}

public class GitStatusResult
{
    [JsonPropertyName("isAvailable")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("isRepo")]
    public bool IsRepo { get; set; }

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "";

    [JsonPropertyName("stagedChanges")]
    public List<GitChangeItem> StagedChanges { get; set; } = new();

    [JsonPropertyName("workingChanges")]
    public List<GitChangeItem> WorkingChanges { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class GitCommitItem
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("timeAgo")]
    public string TimeAgo { get; set; } = "";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";
}

public class GitBranchesResult
{
    [JsonPropertyName("current")]
    public string Current { get; set; } = "";

    [JsonPropertyName("branches")]
    public List<string> Branches { get; set; } = new();
}

public class GitHunkLine
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "context";

    [JsonPropertyName("oldLine")]
    public int? OldLine { get; set; }

    [JsonPropertyName("newLine")]
    public int? NewLine { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class GitDiffHunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("header")]
    public string Header { get; set; } = "";

    [JsonPropertyName("section")]
    public string Section { get; set; } = "";

    [JsonPropertyName("oldStart")]
    public int OldStart { get; set; }

    [JsonPropertyName("oldCount")]
    public int OldCount { get; set; }

    [JsonPropertyName("newStart")]
    public int NewStart { get; set; }

    [JsonPropertyName("newCount")]
    public int NewCount { get; set; }

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonPropertyName("lines")]
    public List<GitHunkLine> Lines { get; set; } = new();

    [JsonIgnore]
    public string Patch { get; set; } = "";
}

public class GitHunksResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("staged")]
    public bool Staged { get; set; }

    [JsonPropertyName("isBinary")]
    public bool IsBinary { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("hunks")]
    public List<GitDiffHunk> Hunks { get; set; } = new();
}

public class GitFileDiffResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("staged")]
    public bool Staged { get; set; }

    [JsonPropertyName("originalContent")]
    public string OriginalContent { get; set; } = "";

    [JsonPropertyName("modifiedContent")]
    public string ModifiedContent { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public static class GitService
{
    private static bool? _isGitAvailableCache;
    private static string? _gitPath;

    // (first seam): every git call runs through this, so the porcelain/diff/hunk parsers
    // Can be tested with canned output instead of a real repository and a real process. The composition
    // Root that will inject it does not exist yet; until then a test swaps the static in
    // One start-up call and puts the real runner back on the way out.
    internal static IProcessRunner ProcessRunner { get; set; } = new SystemProcessRunner();

    /// <summary>
    /// Points git at a different runner and forces the availability answer, so a test never falls back
    /// To probing the real machine. Pass no arguments to restore production behaviour.
    /// </summary>
    internal static void UseProcessSeam(IProcessRunner? runner = null, string? gitPath = null)
    {
        ProcessRunner = runner ?? new SystemProcessRunner();
        _gitPath = gitPath;
        _isGitAvailableCache = gitPath == null ? null : true;
    }

    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(?<oldStart>\d+)(?:,(?<oldCount>\d+))?\s+\+(?<newStart>\d+)(?:,(?<newCount>\d+))?\s+@@(?<section>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool CheckGitAvailable()
    {
        if (_isGitAvailableCache.HasValue) return _isGitAvailableCache.Value;

        // Try standard git executable
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var probe = ProcessRunner
                .RunAsync("git", "--version", cancellationToken: cts.Token)
                .GetAwaiter()
                .GetResult();
            if (probe.ExitCode == 0)
            {
                _gitPath = "git";
                _isGitAvailableCache = true;
                return true;
            }
        }
        catch (Exception) { /* git not on PATH - fall through to the common install paths */ }

        // Check common Windows Git paths
        string[] candidates = new[]
        {
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Program Files\Git\bin\git.exe",
            @"C:\Program Files (x86)\Git\cmd\git.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd\git.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _gitPath = candidate;
                _isGitAvailableCache = true;
                return true;
            }
        }

        _isGitAvailableCache = false;
        return false;
    }

    public static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(string workspace, string arguments)
    {
        if (!CheckGitAvailable() || string.IsNullOrEmpty(_gitPath))
        {
            return (-1, "", "Git is not installed on this machine.");
        }

        var result = await ProcessRunner.RunAsync(_gitPath, arguments, workspace);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitArgumentsAsync(
        string workspace,
        IEnumerable<string> arguments,
        string? standardInput = null)
    {
        if (!CheckGitAvailable() || string.IsNullOrEmpty(_gitPath))
            return (-1, "", "Git is not installed on this machine.");

        var result = await ProcessRunner.RunAsync(_gitPath, arguments.ToList(), workspace, standardInput);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    public static async Task<GitStatusResult> GetStatusAsync(string workspace)
    {
        var result = new GitStatusResult();
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
        {
            result.Message = "No workspace folder open.";
            return result;
        }

        if (!CheckGitAvailable())
        {
            result.IsAvailable = false;
            result.Message = "Git is not installed on this system. Install Git for Windows to enable source control.";
            return result;
        }

        result.IsAvailable = true;
        var gitDir = Path.Combine(workspace, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            result.IsRepo = false;
            result.Message = "This workspace is not a Git repository.";
            return result;
        }

        result.IsRepo = true;

        var (exitCode, stdout, stderr) = await RunGitAsync(workspace, "status --porcelain=v1 -b -uall");
        if (exitCode != 0)
        {
            result.Message = $"Git status error: {stderr.Trim()}";
            return result;
        }

        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                var branchPart = line.Substring(3).Trim();
                var dotDotIdx = branchPart.IndexOf("...", StringComparison.Ordinal);
                result.Branch = dotDotIdx > 0 ? branchPart.Substring(0, dotDotIdx) : branchPart;
                continue;
            }

            if (line.Length < 4) continue;
            char indexStatus = line[0];
            char workTreeStatus = line[1];
            string filePath = line.Substring(3).Trim().Trim('"');

            // Staged change
            if (indexStatus != ' ' && indexStatus != '?')
            {
                result.StagedChanges.Add(new GitChangeItem
                {
                    Path = filePath,
                    Status = indexStatus.ToString(),
                    Staged = true,
                    StatusDesc = GetStatusDescription(indexStatus)
                });
            }

            // Working tree change
            if (workTreeStatus != ' ')
            {
                result.WorkingChanges.Add(new GitChangeItem
                {
                    Path = filePath,
                    Status = workTreeStatus == '?' ? "U" : workTreeStatus.ToString(),
                    Staged = false,
                    StatusDesc = GetStatusDescription(workTreeStatus)
                });
            }
        }

        if (string.IsNullOrEmpty(result.Branch)) result.Branch = "HEAD";
        return result;
    }

    private static string GetStatusDescription(char code) => code switch
    {
        'M' => "Modified",
        'A' => "Added",
        'D' => "Deleted",
        'R' => "Renamed",
        'C' => "Copied",
        '?' => "Untracked",
        'U' => "Untracked",
        _ => "Changed"
    };

    public static async Task<(bool success, string message)> StageFileAsync(string workspace, string relativePath)
    {
        var (code, _, err) = await RunGitAsync(workspace, $"add -- \"{relativePath}\"");
        return (code == 0, code == 0 ? "Staged" : err.Trim());
    }

    public static async Task<(bool success, string message)> UnstageFileAsync(string workspace, string relativePath)
    {
        var (code, _, err) = await RunGitAsync(workspace, $"restore --staged -- \"{relativePath}\"");
        return (code == 0, code == 0 ? "Unstaged" : err.Trim());
    }

    public static async Task<(bool success, string message)> StageAllAsync(string workspace)
    {
        var (code, _, err) = await RunGitAsync(workspace, "add -A");
        return (code == 0, code == 0 ? "Staged all changes" : err.Trim());
    }

    public static async Task<(bool success, string message)> UnstageAllAsync(string workspace)
    {
        var (code, _, err) = await RunGitAsync(workspace, "restore --staged .");
        return (code == 0, code == 0 ? "Unstaged all changes" : err.Trim());
    }

    public static async Task<(bool success, string message)> DiscardFileAsync(string workspace, string relativePath)
    {
        var fullPath = Path.Combine(workspace, relativePath);
        var (statusExit, statusOut, _) = await RunGitAsync(workspace, $"status --porcelain=v1 -- \"{relativePath}\"");
        if (statusOut.StartsWith("??"))
        {
            try
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                return (true, "Discarded untracked file");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        var (code, _, err) = await RunGitAsync(workspace, $"restore -- \"{relativePath}\"");
        return (code == 0, code == 0 ? "Discarded changes" : err.Trim());
    }

    public static async Task<(bool success, string message)> CommitAsync(string workspace, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return (false, "Commit message cannot be empty.");
        // Escape quotes
        var safeMsg = message.Replace("\"", "\\\"");
        var (code, stdout, err) = await RunGitAsync(workspace, $"commit -m \"{safeMsg}\"");
        return (code == 0, code == 0 ? stdout.Trim() : err.Trim());
    }

    public static async Task<(bool success, string content, string message)> GetHeadFileContentAsync(string workspace, string relativePath)
    {
        var safeRel = relativePath.Replace('\\', '/');
        var (code, stdout, err) = await RunGitAsync(workspace, $"show HEAD:\"{safeRel}\"");
        return (code == 0, stdout, code == 0 ? "OK" : err.Trim());
    }

    public static async Task<GitFileDiffResult> GetFileDiffAsync(
        string workspace,
        string relativePath,
        bool staged)
    {
        var result = new GitFileDiffResult { Path = relativePath, Staged = staged };
        var normalizedPath = NormalizeRepositoryPath(workspace, relativePath);
        if (normalizedPath == null)
        {
            result.Message = "The selected path is outside the workspace.";
            return result;
        }
        result.Path = normalizedPath;

        var head = await RunGitArgumentsAsync(workspace, new[] { "show", $"HEAD:{normalizedPath}" });
        var index = await RunGitArgumentsAsync(workspace, new[] { "show", $":{normalizedPath}" });
        if (staged)
        {
            if (head.ExitCode != 0 && index.ExitCode != 0)
            {
                result.Message = string.IsNullOrWhiteSpace(index.Stderr) ? head.Stderr.Trim() : index.Stderr.Trim();
                return result;
            }
            result.OriginalContent = head.ExitCode == 0 ? head.Stdout : "";
            result.ModifiedContent = index.ExitCode == 0 ? index.Stdout : "";
        }
        else
        {
            var fullPath = Path.GetFullPath(Path.Combine(workspace, normalizedPath));
            if (index.ExitCode != 0 && !File.Exists(fullPath))
            {
                result.Message = index.Stderr.Trim();
                return result;
            }
            result.OriginalContent = index.ExitCode == 0 ? index.Stdout : "";
            try
            {
                result.ModifiedContent = File.Exists(fullPath) ? FileSystemService.ReadFile(fullPath) : "";
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                return result;
            }
        }

        result.Success = true;
        result.Message = "OK";
        return result;
    }

    public static async Task<GitHunksResult> GetDiffHunksAsync(
        string workspace,
        string relativePath,
        bool staged)
    {
        var result = new GitHunksResult { Path = relativePath, Staged = staged };
        var normalizedPath = NormalizeRepositoryPath(workspace, relativePath);
        if (normalizedPath == null)
        {
            result.Message = "The selected path is outside the workspace.";
            return result;
        }
        result.Path = normalizedPath;

        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-color", "--unified=3" };
        if (staged) arguments.Add("--cached");
        arguments.Add("--");
        arguments.Add(normalizedPath);
        var diff = await RunGitArgumentsAsync(workspace, arguments);
        if (diff.ExitCode != 0)
        {
            result.Message = diff.Stderr.Trim();
            return result;
        }
        if (diff.Stdout.Length > 10 * 1024 * 1024)
        {
            result.Message = "The diff is larger than the 10 MiB hunk-review limit.";
            return result;
        }

        result.Success = true;
        result.IsBinary = diff.Stdout.Contains("GIT binary patch", StringComparison.Ordinal) ||
                          diff.Stdout.Contains("Binary files ", StringComparison.Ordinal);
        result.Hunks = ParseUnifiedDiff(diff.Stdout);
        result.Message = result.IsBinary
            ? "Binary changes can only be staged or unstaged as a whole file."
            : result.Hunks.Count == 0
                ? "No text hunks are available. The diff may be untracked or already changed since refresh."
                : "OK";
        return result;
    }

    public static async Task<(bool Success, string Message)> ApplyHunkAsync(
        string workspace,
        string relativePath,
        string hunkId,
        string action)
    {
        bool staged = action.Equals("unstage", StringComparison.OrdinalIgnoreCase);
        if (action is not ("stage" or "unstage" or "discard"))
            return (false, "Unknown hunk action.");

        var current = await GetDiffHunksAsync(workspace, relativePath, staged);
        if (!current.Success) return (false, current.Message);
        var hunk = current.Hunks.FirstOrDefault(item => item.Id.Equals(hunkId, StringComparison.OrdinalIgnoreCase));
        if (hunk == null)
            return (false, "This hunk is stale because the file changed. Refresh the diff and try again.");

        var arguments = new List<string> { "apply" };
        if (action is "stage" or "unstage") arguments.Add("--cached");
        if (action is "unstage" or "discard") arguments.Add("--reverse");
        arguments.Add("--whitespace=nowarn");
        arguments.Add("-");

        var applied = await RunGitArgumentsAsync(workspace, arguments, hunk.Patch);
        if (applied.ExitCode != 0)
            return (false, string.IsNullOrWhiteSpace(applied.Stderr) ? "Git could not apply the selected hunk." : applied.Stderr.Trim());
        return (true, action switch
        {
            "stage" => "Staged selected hunk",
            "unstage" => "Unstaged selected hunk",
            _ => "Discarded selected hunk"
        });
    }

    public static List<GitDiffHunk> ParseUnifiedDiff(string diff)
    {
        var hunks = new List<GitDiffHunk>();
        if (string.IsNullOrWhiteSpace(diff)) return hunks;

        var normalized = diff.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var preamble = new StringBuilder();
        GitDiffHunk? current = null;
        StringBuilder? patch = null;
        int oldLine = 0;
        int newLine = 0;

        void CompleteCurrent()
        {
            if (current == null || patch == null) return;
            current.Patch = patch.ToString();
            current.Id = CreateHunkId(current.Patch);
            hunks.Add(current);
            current = null;
            patch = null;
        }

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (lineIndex == lines.Length - 1 && line.Length == 0) break;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && current != null)
            {
                CompleteCurrent();
                preamble.Clear();
            }

            var headerMatch = HunkHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                CompleteCurrent();
                current = new GitDiffHunk
                {
                    Header = line,
                    Section = headerMatch.Groups["section"].Value.Trim(),
                    OldStart = int.Parse(headerMatch.Groups["oldStart"].Value),
                    OldCount = ParseHunkCount(headerMatch.Groups["oldCount"]),
                    NewStart = int.Parse(headerMatch.Groups["newStart"].Value),
                    NewCount = ParseHunkCount(headerMatch.Groups["newCount"])
                };
                oldLine = current.OldStart;
                newLine = current.NewStart;
                patch = new StringBuilder(preamble.ToString());
                patch.Append(line).Append('\n');
                continue;
            }

            if (current == null)
            {
                preamble.Append(line).Append('\n');
                continue;
            }

            patch!.Append(line).Append('\n');
            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                current.Additions++;
                current.Lines.Add(new GitHunkLine { Type = "addition", NewLine = newLine++, Text = line[1..] });
            }
            else if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                current.Deletions++;
                current.Lines.Add(new GitHunkLine { Type = "deletion", OldLine = oldLine++, Text = line[1..] });
            }
            else if (line.StartsWith(" ", StringComparison.Ordinal))
            {
                current.Lines.Add(new GitHunkLine
                {
                    Type = "context",
                    OldLine = oldLine++,
                    NewLine = newLine++,
                    Text = line[1..]
                });
            }
            else if (line.StartsWith("\\", StringComparison.Ordinal))
            {
                current.Lines.Add(new GitHunkLine { Type = "meta", Text = line });
            }
        }
        CompleteCurrent();
        return hunks;
    }

    private static int ParseHunkCount(Group group)
    {
        return group.Success && int.TryParse(group.Value, out var count) ? count : 1;
    }

    private static string CreateHunkId(string patch)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patch)))[..16].ToLowerInvariant();
    }

    private static string? NormalizeRepositoryPath(string workspace, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(relativePath)) return null;
        // PathPolicy resolves junctions/symlinks and rejects UNC, ADS and trailing
        // Dots/spaces, so a repo path that escapes the workspace is refused.
        if (!PathPolicy.TryResolveInsideWorkspace(workspace, relativePath, out var resolved)) return null;
        return Path.GetRelativePath(Path.GetFullPath(workspace), resolved).Replace('\\', '/');
    }

    public static async Task<(bool success, string message)> InitRepoAsync(string workspace)
    {
        var (code, stdout, err) = await RunGitAsync(workspace, "init");
        return (code == 0, code == 0 ? "Initialized Git repository" : err.Trim());
    }

    public static async Task<GitBranchesResult> GetBranchesAsync(string workspace)
    {
        var result = new GitBranchesResult();
        var (code, stdout, _) = await RunGitAsync(workspace, "branch -a --no-color");
        if (code != 0) return result;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            bool isCurrent = line.StartsWith("* ");
            var name = isCurrent ? line.Substring(2).Trim() : line;

            // Strip remotes/origin/ prefix if present
            if (name.StartsWith("remotes/origin/HEAD", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("remotes/origin/", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring("remotes/origin/".Length);
            }
            else if (name.StartsWith("remotes/", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring("remotes/".Length);
            }

            if (isCurrent)
            {
                result.Current = name;
            }

            if (!string.IsNullOrEmpty(name) && set.Add(name))
            {
                result.Branches.Add(name);
            }
        }

        if (string.IsNullOrEmpty(result.Current) && result.Branches.Count > 0)
        {
            result.Current = result.Branches[0];
        }

        return result;
    }

    public static async Task<(bool success, string message)> CheckoutBranchAsync(string workspace, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return (false, "Branch name cannot be empty.");
        var safe = branchName.Trim().Replace("\"", "");
        var (code, stdout, err) = await RunGitAsync(workspace, $"checkout \"{safe}\"");
        return (code == 0, code == 0 ? (string.IsNullOrWhiteSpace(stdout) ? $"Switched to {safe}" : stdout.Trim()) : err.Trim());
    }

    public static async Task<(bool success, string message)> CreateBranchAsync(string workspace, string branchName, bool checkout = true)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return (false, "Branch name cannot be empty.");
        var safe = branchName.Trim().Replace("\"", "");
        var arg = checkout ? $"checkout -b \"{safe}\"" : $"branch \"{safe}\"";
        var (code, stdout, err) = await RunGitAsync(workspace, arg);
        return (code == 0, code == 0 ? (string.IsNullOrWhiteSpace(stdout) ? $"Created branch {safe}" : stdout.Trim()) : err.Trim());
    }

    public static async Task<(bool success, string message)> PullAsync(string workspace)
    {
        var (code, stdout, err) = await RunGitAsync(workspace, "pull");
        return (code == 0, code == 0 ? (string.IsNullOrWhiteSpace(stdout) ? "Already up to date." : stdout.Trim()) : err.Trim());
    }

    public static async Task<(bool success, string message)> PushAsync(string workspace)
    {
        var (code, stdout, err) = await RunGitAsync(workspace, "push");
        return (code == 0, code == 0 ? (string.IsNullOrWhiteSpace(stdout) ? "Pushed to remote." : stdout.Trim()) : err.Trim());
    }

    public static async Task<(bool success, string message)> FetchAsync(string workspace)
    {
        var (code, stdout, err) = await RunGitAsync(workspace, "fetch --all --prune");
        return (code == 0, code == 0 ? "Fetched latest from remotes." : err.Trim());
    }

    public static async Task<(bool success, string message)> StashAsync(string workspace, string message = "")
    {
        var arg = string.IsNullOrWhiteSpace(message) ? "stash" : $"stash push -m \"{message.Replace("\"", "\\\"")}\"";
        var (code, stdout, err) = await RunGitAsync(workspace, arg);
        return (code == 0, code == 0 ? (string.IsNullOrWhiteSpace(stdout) ? "Saved working directory to stash" : stdout.Trim()) : err.Trim());
    }

    public static async Task<(bool success, string message)> StashPopAsync(string workspace)
    {
        var (code, stdout, err) = await RunGitAsync(workspace, "stash pop");
        return (code == 0, code == 0 ? (string.IsNullOrWhiteSpace(stdout) ? "Applied stash" : stdout.Trim()) : err.Trim());
    }

    public static async Task<List<GitCommitItem>> GetCommitHistoryAsync(string workspace, string? relativePath = null, int maxCount = 40)
    {
        var list = new List<GitCommitItem>();
        var safePath = !string.IsNullOrEmpty(relativePath) ? $" -- \"{relativePath.Replace('\\', '/')}\"" : "";
        var (code, stdout, _) = await RunGitAsync(workspace, $"log -n {maxCount} --pretty=format:\"%h\t%an\t%ar\t%s\"{safePath}");
        if (code != 0) return list;

        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length >= 4)
            {
                list.Add(new GitCommitItem
                {
                    Hash = parts[0].Trim(),
                    Author = parts[1].Trim(),
                    TimeAgo = parts[2].Trim(),
                    Subject = parts[3].Trim()
                });
            }
        }
        return list;
    }

    public static async Task<GitBlameResult> GetBlameAsync(string workspace, string relativePath)
    {
        var result = new GitBlameResult { Path = relativePath };
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(relativePath))
        {
            result.Error = "Missing file path for git blame.";
            return result;
        }

        var safeRel = relativePath;
        try
        {
            if (Path.IsPathRooted(relativePath))
            {
                safeRel = Path.GetRelativePath(workspace, relativePath);
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "GitService.GetBlameAsync.RelativePath"); }

        var (code, stdout, stderr) = await RunGitAsync(workspace, $"blame --porcelain -- \"{safeRel.Replace('\\', '/')}\"");
        if (code != 0)
        {
            result.Error = string.IsNullOrWhiteSpace(stderr) ? "Git blame unavailable for this file." : stderr.Trim();
            return result;
        }

        result.Lines = ParseBlamePorcelain(stdout);
        return result;
    }

    public static List<GitBlameLine> ParseBlamePorcelain(string output)
    {
        var results = new List<GitBlameLine>();
        if (string.IsNullOrWhiteSpace(output)) return results;

        var commitMeta = new Dictionary<string, (string author, long unixTime, string summary)>(StringComparer.Ordinal);
        var rawLines = output.Replace("\r\n", "\n").Split('\n');

        string? curHash = null;
        int curFinalLine = 0;
        string? curAuthor = null;
        long curTime = 0;
        string? curSummary = null;

        foreach (var line in rawLines)
        {
            if (line.Length == 0) continue;

            if (line[0] == '\t')
            {
                if (!string.IsNullOrEmpty(curHash) && curFinalLine > 0)
                {
                    if (curAuthor != null || curSummary != null)
                    {
                        commitMeta[curHash] = (
                            curAuthor ?? (commitMeta.TryGetValue(curHash, out var prev) ? prev.author : "Unknown"),
                            curTime != 0 ? curTime : (commitMeta.TryGetValue(curHash, out var prevT) ? prevT.unixTime : 0),
                            curSummary ?? (commitMeta.TryGetValue(curHash, out var prevS) ? prevS.summary : ""));
                    }

                    (string author, long unixTime, string summary) meta = commitMeta.TryGetValue(curHash, out var cached)
                        ? cached
                        : ("Unknown", 0L, "");
                    var isUncommitted = curHash.All(c => c == '0');
                    var dateText = meta.unixTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(meta.unixTime).ToLocalTime().ToString("yyyy-MM-dd")
                        : "";

                    results.Add(new GitBlameLine
                    {
                        Line = curFinalLine,
                        CommitHash = curHash,
                        ShortHash = isUncommitted ? "uncommitted" : (curHash.Length >= 7 ? curHash[..7] : curHash),
                        Author = isUncommitted ? "Not Committed Yet" : meta.author,
                        Date = dateText,
                        Summary = isUncommitted ? "Uncommitted change" : meta.summary
                    });
                }

                curHash = null;
                curFinalLine = 0;
                curAuthor = null;
                curTime = 0;
                curSummary = null;
                continue;
            }

            if (curHash == null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0].Length >= 7 && int.TryParse(parts[2], out var finalLine))
                {
                    curHash = parts[0];
                    curFinalLine = finalLine;
                }
                continue;
            }

            if (line.StartsWith("author ", StringComparison.Ordinal))
                curAuthor = line.Substring(7).Trim();
            else if (line.StartsWith("author-time ", StringComparison.Ordinal) && long.TryParse(line.Substring(12).Trim(), out var ts))
                curTime = ts;
            else if (line.StartsWith("summary ", StringComparison.Ordinal))
                curSummary = line.Substring(8).Trim();
        }

        return results;
    }
}

public sealed class GitBlameLine
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("commitHash")]
    public string CommitHash { get; set; } = "";

    [JsonPropertyName("shortHash")]
    public string ShortHash { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
}

public sealed class GitBlameResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("lines")]
    public List<GitBlameLine> Lines { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

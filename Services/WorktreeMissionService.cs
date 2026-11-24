using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;

namespace VoidCode.Services;

public sealed class MissionSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("workspace")]
    public string Workspace { get; init; } = "";

    [JsonPropertyName("worktreePath")]
    public string WorktreePath { get; init; } = "";

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "queued";

    [JsonPropertyName("lastStatus")]
    public string LastStatus { get; init; } = "";

    [JsonPropertyName("finalMessage")]
    public string FinalMessage { get; init; } = "";

    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    [JsonPropertyName("steps")]
    public int Steps { get; init; }

    [JsonPropertyName("toolCalls")]
    public int ToolCalls { get; init; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; init; }

    [JsonPropertyName("costLabel")]
    public string CostLabel { get; init; } = "";

    [JsonPropertyName("changedFiles")]
    public List<string> ChangedFiles { get; init; } = new();

    [JsonPropertyName("createdAtUtc")]
    public string CreatedAtUtc { get; init; } = "";

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }

    [JsonPropertyName("merged")]
    public bool Merged { get; init; }
}

public sealed class MissionDiffResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("missionId")]
    public string MissionId { get; init; } = "";

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "";

    [JsonPropertyName("diffStat")]
    public string DiffStat { get; init; } = "";

    [JsonPropertyName("unifiedDiff")]
    public string UnifiedDiff { get; init; } = "";

    [JsonPropertyName("changedFiles")]
    public List<string> ChangedFiles { get; init; } = new();

    [JsonPropertyName("error")]
    public string Error { get; init; } = "";
}

public sealed class MissionActionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("missionId")]
    public string MissionId { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("mission")]
    public MissionSnapshot? Mission { get; init; }
}

/// <summary>
/// Orchestrates parallel autonomous VoidAgent background runs ("Mission Control"),
/// each isolated inside its own Git worktree branch so multiple agents can edit, build,
/// and verify concurrently without colliding with the developer's active working tree.
/// </summary>
public static class WorktreeMissionService
{
    private sealed class MissionState
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Prompt { get; init; }
        public required string Workspace { get; init; }
        public required string WorktreePath { get; init; }
        public required string Branch { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
        public CancellationTokenSource Cts { get; } = new();

        public readonly object Gate = new();
        public string Status = "queued";
        public string LastStatus = "Preparing isolated Git worktree...";
        public string FinalMessage = "";
        public string Error = "";
        public int Steps;
        public int ToolCalls;
        public int TotalTokens;
        public string CostLabel = "";
        public List<string> ChangedFiles = new();
        public bool Merged;

        public MissionSnapshot ToSnapshot()
        {
            lock (Gate)
            {
                return new MissionSnapshot
                {
                    Id = Id,
                    Title = Title,
                    Prompt = Prompt,
                    Workspace = Workspace,
                    WorktreePath = WorktreePath,
                    Branch = Branch,
                    Status = Status,
                    LastStatus = LastStatus,
                    FinalMessage = FinalMessage,
                    Error = Error,
                    Steps = Steps,
                    ToolCalls = ToolCalls,
                    TotalTokens = TotalTokens,
                    CostLabel = CostLabel,
                    ChangedFiles = new List<string>(ChangedFiles),
                    CreatedAtUtc = CreatedAtUtc.ToString("O"),
                    ElapsedMs = Stopwatch.ElapsedMilliseconds,
                    Merged = Merged
                };
            }
        }
    }

    private static readonly ConcurrentDictionary<string, MissionState> _missions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional test seam to override the background agent runner inside a worktree.</summary>
    internal static Func<string, string, AppSettings, Action<string, object>, CancellationToken, Task>? AgentRunnerOverrideForTests { get; set; }

    public static string BuildWorktreeRoot(string workspace, string missionId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspace)))).Substring(0, 10).ToLowerInvariant();
        return Path.Combine(StorageService.Root, "worktrees", hash, missionId);
    }

    public static List<MissionSnapshot> ListMissions(string? workspace = null)
    {
        var normWs = string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace);
        return _missions.Values
            .Where(m => normWs == null || string.Equals(Path.GetFullPath(m.Workspace), normWs, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.CreatedAtUtc)
            .Select(m => m.ToSnapshot())
            .ToList();
    }

    public static async Task<MissionActionResult> StartMissionAsync(
        string workspace,
        string title,
        string prompt,
        AppSettings settings,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return new MissionActionResult { Ok = false, Message = "Open a Git repository workspace first to launch background worktree missions." };
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new MissionActionResult { Ok = false, Message = "A mission prompt is required." };
        }
        if (!WorkspaceTrustService.IsTrusted(workspace))
        {
            return new MissionActionResult { Ok = false, Message = "Trust this workspace before running parallel background agents." };
        }

        var status = await GitService.GetStatusAsync(workspace);
        if (!status.IsAvailable || !status.IsRepo)
        {
            return new MissionActionResult { Ok = false, Message = "Mission Control requires a Git repository (run `git init` first)." };
        }

        var missionId = "m-" + Guid.NewGuid().ToString("N")[..8];
        var branch = $"voidcode/mission-{missionId}";
        var worktreePath = BuildWorktreeRoot(workspace, missionId);
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        var (addExit, _, addErr) = await GitService.RunGitArgsAsync(
            workspace,
            new[] { "worktree", "add", "-b", branch, worktreePath, "HEAD" },
            ct);

        if (addExit != 0)
        {
            return new MissionActionResult
            {
                Ok = false,
                Message = $"Could not create Git worktree: {(string.IsNullOrWhiteSpace(addErr) ? "Ensure the repo has at least one commit (HEAD)." : addErr.Trim())}"
            };
        }

        // Automatically trust the isolated worktree copy derived from an already-trusted workspace.
        WorkspaceTrustService.SetTrusted(worktreePath, true);

        var safeTitle = string.IsNullOrWhiteSpace(title)
            ? (prompt.Length > 48 ? prompt[..48].Trim() + "..." : prompt.Trim())
            : title.Trim();

        var state = new MissionState
        {
            Id = missionId,
            Title = safeTitle,
            Prompt = prompt.Trim(),
            Workspace = Path.GetFullPath(workspace),
            WorktreePath = worktreePath,
            Branch = branch,
            CreatedAtUtc = DateTime.UtcNow
        };
        _missions[missionId] = state;

        // Clone settings for autonomous worktree execution (auto-approve in-worktree edits while keeping isolated from main tree).
        var missionSettings = CloneSettingsForMission(settings);

        _ = Task.Run(() => RunMissionLoopAsync(state, missionSettings));

        return new MissionActionResult
        {
            Ok = true,
            MissionId = missionId,
            Message = $"Launched mission '{safeTitle}' on branch {branch}.",
            Mission = state.ToSnapshot()
        };
    }

    public static async Task<MissionDiffResult> GetMissionDiffAsync(string missionId, CancellationToken ct = default)
    {
        if (!_missions.TryGetValue(missionId, out var state))
        {
            return new MissionDiffResult { Ok = false, MissionId = missionId, Error = "Mission not found." };
        }
        if (!Directory.Exists(state.WorktreePath))
        {
            return new MissionDiffResult { Ok = false, MissionId = missionId, Branch = state.Branch, Error = "Worktree directory no longer exists." };
        }

        var changed = await CollectChangedFilesAsync(state.WorktreePath, ct);
        lock (state.Gate) { state.ChangedFiles = changed; }

        // Stage untracked files with intent-to-add (`git add -N .`) so `git diff HEAD` includes newly created files too.
        await GitService.RunGitArgsAsync(state.WorktreePath, new[] { "add", "-N", "." }, ct);
        var (_, statOut, _) = await GitService.RunGitArgsAsync(state.WorktreePath, new[] { "diff", "--stat", "HEAD" }, ct);
        var (_, diffOut, _) = await GitService.RunGitArgsAsync(state.WorktreePath, new[] { "diff", "HEAD" }, ct);

        const int maxDiffChars = 120_000;
        var clippedDiff = diffOut.Length > maxDiffChars
            ? diffOut[..maxDiffChars] + "\n... [diff truncated for preview]"
            : diffOut;

        return new MissionDiffResult
        {
            Ok = true,
            MissionId = missionId,
            Branch = state.Branch,
            DiffStat = statOut.Trim(),
            UnifiedDiff = clippedDiff,
            ChangedFiles = changed
        };
    }

    public static async Task<MissionActionResult> MergeMissionAsync(string missionId, CancellationToken ct = default)
    {
        if (!_missions.TryGetValue(missionId, out var state))
        {
            return new MissionActionResult { Ok = false, MissionId = missionId, Message = "Mission not found." };
        }
        if (string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return new MissionActionResult { Ok = false, MissionId = missionId, Message = "Wait for the mission to finish (or cancel it) before merging." };
        }
        if (!Directory.Exists(state.WorktreePath))
        {
            return new MissionActionResult { Ok = false, MissionId = missionId, Message = "Worktree has already been cleaned up." };
        }

        // 1. Commit any uncommitted edits in the isolated worktree branch.
        var (stExit, stOut, _) = await GitService.RunGitArgsAsync(state.WorktreePath, new[] { "status", "--porcelain=v1" }, ct);
        if (stExit == 0 && !string.IsNullOrWhiteSpace(stOut))
        {
            await GitService.RunGitArgsAsync(state.WorktreePath, new[] { "add", "-A" }, ct);
            var (cmExit, _, cmErr) = await GitService.RunGitArgsAsync(
                state.WorktreePath,
                new[] { "-c", "user.name=VoidAgent", "-c", "user.email=voidagent@voidcode.local", "commit", "-m", $"feat(mission): {state.Title}" },
                ct);
            if (cmExit != 0)
            {
                return new MissionActionResult { Ok = false, MissionId = missionId, Message = $"Could not snapshot worktree commit: {cmErr.Trim()}" };
            }
        }

        // 2. Merge the mission branch cleanly into the active workspace.
        var (mExit, mOut, mErr) = await GitService.RunGitArgsAsync(
            state.Workspace,
            new[] { "merge", "--no-ff", "--no-edit", state.Branch },
            ct);

        if (mExit != 0)
        {
            return new MissionActionResult
            {
                Ok = false,
                MissionId = missionId,
                Message = $"Merge reported conflicts or errors: {(string.IsNullOrWhiteSpace(mErr) ? mOut : mErr).Trim()}"
            };
        }

        lock (state.Gate)
        {
            state.Merged = true;
            state.Status = "merged";
            state.LastStatus = $"Merged branch {state.Branch} into workspace.";
        }

        return new MissionActionResult
        {
            Ok = true,
            MissionId = missionId,
            Message = $"Merged '{state.Title}' ({state.Branch}) into your active workspace.",
            Mission = state.ToSnapshot()
        };
    }

    public static MissionActionResult CancelMission(string missionId)
    {
        if (!_missions.TryGetValue(missionId, out var state))
        {
            return new MissionActionResult { Ok = false, MissionId = missionId, Message = "Mission not found." };
        }
        try { state.Cts.Cancel(); } catch (Exception) { /* already cancelled */ }
        lock (state.Gate)
        {
            if (state.Status is "queued" or "running")
            {
                state.Status = "cancelled";
                state.LastStatus = "Cancelled by user.";
                state.Stopwatch.Stop();
            }
        }
        return new MissionActionResult { Ok = true, MissionId = missionId, Message = "Mission cancelled.", Mission = state.ToSnapshot() };
    }

    public static async Task<MissionActionResult> DiscardMissionAsync(string missionId, CancellationToken ct = default)
    {
        if (!_missions.TryRemove(missionId, out var state))
        {
            return new MissionActionResult { Ok = false, MissionId = missionId, Message = "Mission not found." };
        }

        try { state.Cts.Cancel(); } catch (Exception) { /* already cancelled */ }
        state.Stopwatch.Stop();

        await CleanupWorktreeAsync(state.Workspace, state.WorktreePath, state.Branch, ct);
        return new MissionActionResult { Ok = true, MissionId = missionId, Message = $"Removed worktree and deleted branch {state.Branch}." };
    }

    public static void ShutdownAll()
    {
        foreach (var kv in _missions)
        {
            try { kv.Value.Cts.Cancel(); } catch (Exception) { /* shutting down */ }
        }
    }

    private static async Task RunMissionLoopAsync(MissionState state, AppSettings missionSettings)
    {
        lock (state.Gate)
        {
            state.Status = "running";
            state.LastStatus = "Agent exploring isolated worktree...";
        }

        void HandleEvent(string evt, object payload)
        {
            try
            {
                var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(payload))?.AsObject();
                if (node == null) return;
                lock (state.Gate)
                {
                    switch (evt)
                    {
                        case "agent_status":
                            state.LastStatus = node["status"]?.GetValue<string>() ?? state.LastStatus;
                            break;
                        case "agent_tool_call":
                            state.ToolCalls++;
                            var tool = node["tool"]?.GetValue<string>() ?? "tool";
                            state.LastStatus = $"Running {tool}...";
                            break;
                        case "agent_tool_result":
                            var mod = node["modifiedFilePath"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(mod) && !state.ChangedFiles.Contains(mod, StringComparer.OrdinalIgnoreCase))
                            {
                                state.ChangedFiles.Add(mod);
                            }
                            break;
                        case "agent_done":
                            state.Steps = node["steps"]?.GetValue<int>() ?? state.Steps;
                            state.ToolCalls = node["toolCalls"]?.GetValue<int>() ?? state.ToolCalls;
                            state.TotalTokens = node["totalTokens"]?.GetValue<int>() ?? state.TotalTokens;
                            state.CostLabel = node["costLabel"]?.GetValue<string>() ?? state.CostLabel;
                            state.FinalMessage = node["finalMessage"]?.GetValue<string>() ?? "";
                            state.LastStatus = "Completed — ready to review & merge.";
                            break;
                        case "agent_error":
                            state.Error = node["message"]?.GetValue<string>() ?? "Agent error";
                            state.LastStatus = state.Error;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "WorktreeMissionService.HandleEvent");
            }
        }

        try
        {
            if (AgentRunnerOverrideForTests != null)
            {
                await AgentRunnerOverrideForTests(state.Prompt, state.WorktreePath, missionSettings, HandleEvent, state.Cts.Token);
            }
            else
            {
                await AgentLoop.RunAsync(
                    state.Prompt,
                    new List<JsonObject>(),
                    state.WorktreePath,
                    missionSettings,
                    HandleEvent,
                    state.Cts.Token);
            }

            var changed = await CollectChangedFilesAsync(state.WorktreePath, CancellationToken.None);
            lock (state.Gate)
            {
                state.ChangedFiles = changed;
                state.Stopwatch.Stop();
                if (state.Cts.IsCancellationRequested)
                {
                    state.Status = "cancelled";
                }
                else if (!string.IsNullOrEmpty(state.Error))
                {
                    state.Status = "failed";
                }
                else
                {
                    state.Status = "completed";
                    if (string.IsNullOrEmpty(state.LastStatus)) state.LastStatus = "Completed.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
            {
                state.Status = "cancelled";
                state.LastStatus = "Cancelled.";
                state.Stopwatch.Stop();
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Mission {state.Id} failed.", ex, nameof(WorktreeMissionService));
            lock (state.Gate)
            {
                state.Status = "failed";
                state.Error = ex.Message;
                state.LastStatus = $"Failed: {ex.Message}";
                state.Stopwatch.Stop();
            }
        }
    }

    private static async Task<List<string>> CollectChangedFilesAsync(string worktreePath, CancellationToken ct)
    {
        var list = new List<string>();
        try
        {
            var (exit, output, _) = await GitService.RunGitArgsAsync(worktreePath, new[] { "status", "--porcelain=v1" }, ct);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return list;
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length < 4) continue;
                var file = line[3..].Trim();
                if (!string.IsNullOrWhiteSpace(file)) list.Add(file);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "WorktreeMissionService.CollectChangedFiles");
        }
        return list;
    }

    private static async Task CleanupWorktreeAsync(string workspace, string worktreePath, string branch, CancellationToken ct)
    {
        try
        {
            await GitService.RunGitArgsAsync(workspace, new[] { "worktree", "remove", "--force", worktreePath }, ct);
        }
        catch (Exception) { /* fallback to directory deletion */ }

        try
        {
            if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, recursive: true);
        }
        catch (Exception) { /* best-effort cleanup */ }

        try
        {
            await GitService.RunGitArgsAsync(workspace, new[] { "worktree", "prune" }, ct);
            await GitService.RunGitArgsAsync(workspace, new[] { "branch", "-D", branch }, ct);
        }
        catch (Exception) { /* branch may already be removed */ }
    }

    private static AppSettings CloneSettingsForMission(AppSettings src) => new()
    {
        Provider = src.Provider,
        ApiKey = src.ApiKey,
        ApiKeyConfigured = src.ApiKeyConfigured,
        BaseUrl = src.BaseUrl,
        Model = src.Model,
        Temperature = src.Temperature,
        MaxTokens = src.MaxTokens,
        MaxAgentTurns = src.MaxAgentTurns,
        SystemPrompt = src.SystemPrompt,
        AutoApproveTools = true,
        AuthorityMode = "workspace_auto",
        AgentMode = "act",
        ModelSupportsVision = src.ModelSupportsVision
    };
}

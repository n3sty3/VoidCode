using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// Git, the biggest block in the switch - 21 cases.
///
/// Moved verbatim: <c>git_get_status</c>, the six stage/unstage/discard actions, <c>git_commit</c>,
/// The three diff readers (<c>git_get_head_content</c>, <c>git_get_file_diff</c>, <c>git_get_hunks</c>),
/// <c>git_apply_hunk</c>, the branch four (<c>get_branches</c>, <c>checkout</c>, <c>create</c>),
/// Remote three (<c>pull</c>, <c>push</c>, <c>fetch</c>), the two stash actions,
/// <c>git_get_history</c> and <c>generate_commit_message</c> (which is git's UI affordance even though
/// The text comes from the agent engine).
///
/// The replies are unchanged (<c>git_status_response</c>, <c>git_diff_response</c>,
/// <c>git_hunks_response</c>, <c>git_branches_response</c>, <c>git_history_response</c>, the toasts and
/// The occasional <c>tree_refreshed</c>), so the page needs no change. What the router adds: the
/// Payloads are typed, so a <c>git_stage_file</c> without a path or a <c>git_commit</c> without a
/// Message is refused as <c>invalid_payload</c> instead of running git against <c>""</c>, and the
/// Handlers no longer read the window's field three times per action - the workspace is read once
/// Through the injected delegate.
/// </summary>
public sealed class GitHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string, object> _send;
    private readonly IGitClient _git;

    /// <param name="git">
    /// The git seam. Production passes nothing and gets <see cref="GitClient"/>, which
    /// Delegates to <see cref="GitService"/>; a test passes a fake to assert the reply shapes of routes
    /// That otherwise only ever report "git is not installed".
    /// </param>
    public GitHandlers(Func<string?> workspace, Func<AppSettings> settings, Action<string, object> send, IGitClient? git = null)
    {
        _workspace = workspace;
        _settings = settings;
        _send = send;
        _git = git ?? new GitClient();
    }

    public sealed class PathRequest
    {
        public required string Path { get; init; }
    }

    public sealed class PathStagedRequest
    {
        public required string Path { get; init; }
        public bool Staged { get; init; }
    }

    public sealed class ApplyHunkRequest
    {
        public required string Path { get; init; }
        public string? HunkId { get; init; }
        public string? HunkAction { get; init; }
    }

    public sealed class CommitRequest
    {
        public required string Message { get; init; }
    }

    public sealed class BranchRequest
    {
        public required string Branch { get; init; }
    }

    public sealed class StashRequest
    {
        public string? Message { get; init; }
    }

    public sealed class HistoryRequest
    {
        public string? Path { get; init; }
    }

    public sealed class NoPayload
    {
    }

    public void Register(BridgeRouter router)
    {
        // Status + working tree
        router.Register<NoPayload, object?>(GitRoutes.GetStatus, GetStatusAsync, envelope: false);
        router.Register<PathRequest, object?>(GitRoutes.StageFile, StageFileAsync, envelope: false);
        router.Register<PathRequest, object?>(GitRoutes.UnstageFile, UnstageFileAsync, envelope: false);
        router.Register<NoPayload, object?>(GitRoutes.StageAll, StageAllAsync, envelope: false);
        router.Register<NoPayload, object?>(GitRoutes.UnstageAll, UnstageAllAsync, envelope: false);
        router.Register<PathRequest, object?>(GitRoutes.DiscardFile, DiscardFileAsync, envelope: false);
        router.Register<CommitRequest, object?>(GitRoutes.Commit, CommitAsync, envelope: false);
        router.Register<NoPayload, object?>(GitRoutes.GenerateCommitMessage, GenerateCommitMessageAsync, envelope: false);

        // Diff reading
        router.Register<PathRequest, object?>(GitRoutes.GetHeadContent, GetHeadContentAsync, envelope: false);
        router.Register<PathStagedRequest, object?>(GitRoutes.GetFileDiff, GetFileDiffAsync, envelope: false);
        router.Register<PathStagedRequest, object?>(GitRoutes.GetHunks, GetHunksAsync, envelope: false);
        router.Register<ApplyHunkRequest, object?>(GitRoutes.ApplyHunk, ApplyHunkAsync, envelope: false);

        // Branches + remotes + stash + history
        router.Register<NoPayload, object?>(GitRoutes.GetBranches, GetBranchesAsync, envelope: false);
        router.Register<BranchRequest, object?>(GitRoutes.CheckoutBranch, CheckoutBranchAsync, envelope: false);
        router.Register<BranchRequest, object?>(GitRoutes.CreateBranch, CreateBranchAsync, envelope: false);
        router.Register<NoPayload, object?>(GitRoutes.Pull, PullAsync, envelope: false);
        router.Register<NoPayload, object?>(GitRoutes.Push, PushAsync, envelope: false);
        router.Register<NoPayload, object?>(GitRoutes.Fetch, FetchAsync, envelope: false);
        router.Register<StashRequest, object?>(GitRoutes.Stash, StashAsync, envelope: false);
        router.Register<NoPayload, object?>(GitRoutes.StashPop, StashPopAsync, envelope: false);
        router.Register<HistoryRequest, object?>(GitRoutes.GetHistory, GetHistoryAsync, envelope: false);
        router.Register<PathRequest, object?>(GitRoutes.GetBlame, GetBlameAsync, envelope: false);
    }

    private string Workspace => _workspace() ?? "";

    /// <summary>Status first, then the toast - the order every mutating git action uses.</summary>
    private async Task ReportAsync(string toastType, string message)
    {
        var status = await _git.GetStatusAsync(Workspace);
        _send("git_status_response", status);
        _send("toast", new { type = toastType, message });
    }

    private void RefreshTree()
    {
        var workspace = _workspace();
        if (string.IsNullOrEmpty(workspace)) return;
        var tree = FileSystemService.GetDirectoryTree(workspace);
        _send("tree_refreshed", new { tree });
    }

    // ---------------------------------------------------------------- handlers (moved verbatim)

    private Task<object?> GetStatusAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            var status = await _git.GetStatusAsync(workspace);
            _send("git_status_response", status);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> StageFileAsync(PathRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.StageFileAsync(workspace, path);
            await ReportAsync(success ? "success" : "error", msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> UnstageFileAsync(PathRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.UnstageFileAsync(workspace, path);
            await ReportAsync(success ? "success" : "error", msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> StageAllAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.StageAllAsync(workspace);
            await ReportAsync(success ? "success" : "error", msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> UnstageAllAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.UnstageAllAsync(workspace);
            await ReportAsync(success ? "success" : "error", msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DiscardFileAsync(PathRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.DiscardFileAsync(workspace, path);
            // A discard is informational on success, not a success chime
            await ReportAsync(success ? "info" : "error", msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> CommitAsync(CommitRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var message = request.Message;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.CommitAsync(workspace, message);
            await ReportAsync(success ? "success" : "error", success ? "Committed successfully" : msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GenerateCommitMessageAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        var settings = _settings();
        _ = Task.Run(async () =>
        {
            try
            {
                _send("toast", new { type = "info", message = "Generating commit message with AI..." });
                var msg = await AgentEngine.GenerateCommitMessageAsync(workspace, settings);
                _send("generate_commit_message_response", new { commitMessage = msg });
            }
            catch (Exception ex)
            {
                _send("toast", new { type = "error", message = $"Failed to generate commit message: {ex.Message}" });
            }
        });
        return Task.FromResult<object?>(null);
    }
// ---------------------------------------------------------------- diff reading

    private Task<object?> GetHeadContentAsync(PathRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        _ = Task.Run(async () =>
        {
            if (Path.IsPathRooted(path) && !string.IsNullOrEmpty(workspace))
            {
                path = Path.GetRelativePath(workspace, path);
            }
            var (success, content, msg) = await _git.GetHeadFileContentAsync(workspace, path);
            var fullPath = Path.Combine(workspace, path);
            var currentContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
            _send("git_diff_response", new
            {
                success,
                path,
                headContent = content,
                currentContent,
                message = msg
            });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetFileDiffAsync(PathStagedRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        var staged = request.Staged;
        _ = Task.Run(async () =>
        {
            var result = await _git.GetFileDiffAsync(workspace, path, staged);
            // The page reads payload.path / payload.staged, and these three members used
            // To serialize PascalCase (named `result.X`), so the review pane showed no path and treated
            // Every diff as working-tree. The names are explicit now, like the other five fields.
            _send("git_diff_response", new
            {
                success = result.Success,
                path = result.Path,
                staged = result.Staged,
                headContent = result.OriginalContent,
                currentContent = result.ModifiedContent,
                message = result.Message
            });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetHunksAsync(PathStagedRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        var staged = request.Staged;
        _ = Task.Run(async () =>
        {
            var result = await _git.GetDiffHunksAsync(workspace, path, staged);
            _send("git_hunks_response", result);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ApplyHunkAsync(ApplyHunkRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        var hunkId = request.HunkId ?? "";
        var hunkAction = request.HunkAction ?? "";
        var staged = hunkAction.Equals("unstage", StringComparison.OrdinalIgnoreCase);
        _ = Task.Run(async () =>
        {
            var (success, message) = await _git.ApplyHunkAsync(workspace, path, hunkId, hunkAction);
            _send("toast", new { type = success ? "success" : "error", message });
            var status = await _git.GetStatusAsync(workspace);
            _send("git_status_response", status);
            var hunks = await _git.GetDiffHunksAsync(workspace, path, staged);
            _send("git_hunks_response", hunks);
            if (success)
            {
                var diff = await _git.GetFileDiffAsync(workspace, path, staged);
                _send("git_diff_response", new
                {
                    success = diff.Success,
                    path = diff.Path,
                    staged = diff.Staged,
                    headContent = diff.OriginalContent,
                    currentContent = diff.ModifiedContent,
                    message = diff.Message
                });
            }
        });
        return Task.FromResult<object?>(null);
    }
// ---------------------------------------------------------------- branches, remotes, stash, history

    private Task<object?> GetBranchesAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            var result = await _git.GetBranchesAsync(workspace);
            _send("git_branches_response", result);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> CheckoutBranchAsync(BranchRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var branch = request.Branch;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.CheckoutBranchAsync(workspace, branch);
            await ReportAsync(success ? "success" : "error", msg);
            if (success && !string.IsNullOrEmpty(workspace)) RefreshTree();
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> CreateBranchAsync(BranchRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var branch = request.Branch;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.CreateBranchAsync(workspace, branch);
            await ReportAsync(success ? "success" : "error", msg);
            if (success && !string.IsNullOrEmpty(workspace)) RefreshTree();
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> PullAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            _send("toast", new { type = "info", message = "Pulling from remote..." });
            var (success, msg) = await _git.PullAsync(workspace);
            await ReportAsync(success ? "success" : "error", msg);
            if (success && !string.IsNullOrEmpty(workspace)) RefreshTree();
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> PushAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            _send("toast", new { type = "info", message = "Pushing to remote..." });
            var (success, msg) = await _git.PushAsync(workspace);
            await ReportAsync(success ? "success" : "error", msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> FetchAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            _send("toast", new { type = "info", message = "Fetching remotes..." });
            var (success, msg) = await _git.FetchAsync(workspace);
            await ReportAsync(success ? "success" : "error", msg);
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> StashAsync(StashRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var message = request.Message ?? "";
        _ = Task.Run(async () =>
        {
            var (success, retMsg) = await _git.StashAsync(workspace, message);
            await ReportAsync(success ? "success" : "error", retMsg);
            if (success && !string.IsNullOrEmpty(workspace)) RefreshTree();
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> StashPopAsync(NoPayload request, BridgeContext context)
    {
        var workspace = Workspace;
        _ = Task.Run(async () =>
        {
            var (success, msg) = await _git.StashPopAsync(workspace);
            await ReportAsync(success ? "success" : "error", msg);
            if (success && !string.IsNullOrEmpty(workspace)) RefreshTree();
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetHistoryAsync(HistoryRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var relPath = request.Path;
        _ = Task.Run(async () =>
        {
            var history = await _git.GetCommitHistoryAsync(workspace, relPath);
            _send("git_history_response", new { path = relPath, commits = history });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetBlameAsync(PathRequest request, BridgeContext context)
    {
        var workspace = Workspace;
        var path = request.Path;
        _ = Task.Run(async () =>
        {
            var blame = await _git.GetBlameAsync(workspace, path);
            _send("git_blame_response", blame);
        });
        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of the git domain.</summary>
public static class GitRoutes
{
    public const string GetStatus = "git_get_status";
    public const string StageFile = "git_stage_file";
    public const string UnstageFile = "git_unstage_file";
    public const string StageAll = "git_stage_all";
    public const string UnstageAll = "git_unstage_all";
    public const string DiscardFile = "git_discard_file";
    public const string Commit = "git_commit";
    public const string GenerateCommitMessage = "generate_commit_message";
    public const string GetHeadContent = "git_get_head_content";
    public const string GetFileDiff = "git_get_file_diff";
    public const string GetHunks = "git_get_hunks";
    public const string ApplyHunk = "git_apply_hunk";
    public const string GetBranches = "git_get_branches";
    public const string CheckoutBranch = "git_checkout_branch";
    public const string CreateBranch = "git_create_branch";
    public const string Pull = "git_pull";
    public const string Push = "git_push";
    public const string Fetch = "git_fetch";
    public const string Stash = "git_stash";
    public const string StashPop = "git_stash_pop";
    public const string GetHistory = "git_get_history";
    public const string GetBlame = "git_get_blame";
}
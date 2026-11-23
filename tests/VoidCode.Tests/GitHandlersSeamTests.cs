using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The git domain's success payloads, which were unreachable before this seam -
// Git is not installed here, so every route only ever reported "Git is not installed on this machine."
// The fake answers; the assertions are about the reply shapes the page consumes.
public class GitHandlersSeamTests
{
    private const string Workspace = @"C:\fake\repo";
    private readonly Recorder _recorder = new();
    private readonly FakeGitClient _git = new();
    private readonly BridgeRouter _router = new();

    public GitHandlersSeamTests()
    {
        var handlers = new GitHandlers(() => Workspace, () => new AppSettings(), _recorder.Send, _git);
        handlers.Register(_router);
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, object? payload = null)
        => _router.TryHandleAsync(action, id, JsonNode.Parse(JsonSerializer.Serialize(payload ?? new { }))!.AsObject());

    [Fact]
    public async Task Status_SendsTheBranchAndTheThreeChangeLists()
    {
        _git.Status = new GitStatusResult
        {
            IsAvailable = true,
            IsRepo = true,
            Branch = "feature/seam",
            StagedChanges = { new GitChangeItem { Path = "staged.cs", Status = "M", Staged = true, StatusDesc = "Modified" } },
            WorkingChanges = { new GitChangeItem { Path = "working.cs", Status = "M", Staged = false, StatusDesc = "Modified" } }
        };

        await Dispatch(GitRoutes.GetStatus, "req_1");

        var reply = await _recorder.WaitForAsync("git_status_response");
        Assert.Equal("feature/seam", reply.GetProperty("branch").GetString());
        Assert.Single(reply.GetProperty("stagedChanges").EnumerateArray());
        Assert.Single(reply.GetProperty("workingChanges").EnumerateArray());
    }

    [Fact]
    public async Task StageFile_Success_SendsStatusThenASuccessToast()
    {
        _git.StageMessage = (true, "Staged");

        await Dispatch(GitRoutes.StageFile, "req_2", new { path = "src/a.cs" });

        await _recorder.WaitForAsync("git_status_response");
        var toast = await _recorder.WaitForAsync("toast");
        Assert.Equal("success", toast.GetProperty("type").GetString());
        Assert.Equal("Staged", toast.GetProperty("message").GetString());
        Assert.Equal(Workspace, _git.LastWorkspace);
        Assert.Equal("src/a.cs", _git.LastPath);
    }

    [Fact]
    public async Task Commit_Failure_ReportsGitMessageAsAnErrorToast()
    {
        _git.CommitMessage = (false, "nothing to commit, working tree clean");

        await Dispatch(GitRoutes.Commit, "req_3", new { message = "feat: seam" });

        var toast = await _recorder.WaitForAsync("toast");
        Assert.Equal("error", toast.GetProperty("type").GetString());
        Assert.Contains("nothing to commit", toast.GetProperty("message").GetString());
        Assert.Equal("feat: seam", _git.LastCommitMessage);
    }

    [Fact]
    public async Task Commit_Success_UsesTheHandlersOwnWording()
    {
        _git.CommitMessage = (true, "anything");

        await Dispatch(GitRoutes.Commit, "req_4", new { message = "fix: seam" });

        var toast = await _recorder.WaitForAsync("toast");
        Assert.Equal("success", toast.GetProperty("type").GetString());
        Assert.Equal("Committed successfully", toast.GetProperty("message").GetString());
    }

    [Fact]
    public async Task FileDiff_MapsTheClientResultOntoTheReplyThePageReads()
    {
        _git.Diff = new GitFileDiffResult
        {
            Success = true,
            Path = "src/a.cs",
            Staged = true,
            OriginalContent = "class Before { }",
            ModifiedContent = "class After { }",
            Message = "OK"
        };

        await Dispatch(GitRoutes.GetFileDiff, "req_5", new { path = "src/a.cs", staged = true });

        var reply = await _recorder.WaitForAsync("git_diff_response");
        Assert.True(reply.TryGetProperty("headContent", out var head), reply.GetRawText());
        Assert.Equal("class Before { }", head.GetString());
        Assert.True(reply.TryGetProperty("currentContent", out var current), reply.GetRawText());
        Assert.Equal("class After { }", current.GetString());
        // The page reads payload.path / payload.staged; PascalCase here silently broke the review pane
        // (no path in the header, every diff treated as working-tree).
        Assert.Equal("src/a.cs", reply.GetProperty("path").GetString());
        Assert.True(reply.GetProperty("staged").GetBoolean());
    }

    [Fact]
    public async Task History_SendsTheCommitsForTheRequestedPath()
    {
        _git.History = new List<GitCommitItem>
        {
            new() { Hash = "abc1234", Author = "Dev", Subject = "feat: seam", TimeAgo = "2 hours ago" }
        };

        await Dispatch(GitRoutes.GetHistory, "req_6", new { path = "src/a.cs" });

        var reply = await _recorder.WaitForAsync("git_history_response");
        Assert.Equal("src/a.cs", reply.GetProperty("path").GetString());
        var commit = Assert.Single(reply.GetProperty("commits").EnumerateArray());
        Assert.Equal("abc1234", commit.GetProperty("hash").GetString());
        Assert.Equal("src/a.cs", _git.LastHistoryPath);
    }

    [Fact]
    public async Task Branches_SendsTheClientResultVerbatim()
    {
        _git.Branches = new GitBranchesResult { Current = "main", Branches = { "main", "feature/seam" } };

        await Dispatch(GitRoutes.GetBranches, "req_7");

        var reply = await _recorder.WaitForAsync("git_branches_response");
        Assert.Equal("main", reply.GetProperty("current").GetString());
        Assert.Equal(2, reply.GetProperty("branches").GetArrayLength());
    }

    /// <summary>Answers every git call from fields the test sets and records what the domain asked for.</summary>
    private sealed class FakeGitClient : IGitClient
    {
        public GitStatusResult Status { get; set; } = new() { IsAvailable = true, IsRepo = true, Branch = "main" };
        public GitFileDiffResult Diff { get; set; } = new() { Success = true, Path = "a.cs", Message = "OK" };
        public GitHunksResult Hunks { get; set; } = new() { Success = true, Message = "OK" };
        public GitBranchesResult Branches { get; set; } = new() { Current = "main" };
        public List<GitCommitItem> History { get; set; } = new();

        public (bool success, string message) StageMessage { get; set; } = (true, "Staged");
        public (bool success, string message) CommitMessage { get; set; } = (true, "Committed");

        public string? LastWorkspace { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastCommitMessage { get; private set; }
        public string? LastHistoryPath { get; private set; }

        public Task<GitStatusResult> GetStatusAsync(string workspace)
        {
            LastWorkspace = workspace;
            return Task.FromResult(Status);
        }

        public Task<(bool success, string message)> StageFileAsync(string workspace, string relativePath)
        {
            LastWorkspace = workspace;
            LastPath = relativePath;
            return Task.FromResult(StageMessage);
        }

        public Task<(bool success, string message)> UnstageFileAsync(string workspace, string relativePath) =>
            Task.FromResult((true, "Unstaged"));

        public Task<(bool success, string message)> StageAllAsync(string workspace) => Task.FromResult((true, "Staged all changes"));

        public Task<(bool success, string message)> UnstageAllAsync(string workspace) => Task.FromResult((true, "Unstaged all changes"));

        public Task<(bool success, string message)> DiscardFileAsync(string workspace, string relativePath) =>
            Task.FromResult((true, "Discarded"));

        public Task<(bool success, string message)> CommitAsync(string workspace, string message)
        {
            LastCommitMessage = message;
            return Task.FromResult(CommitMessage);
        }

        public Task<(bool success, string content, string message)> GetHeadFileContentAsync(string workspace, string relativePath) =>
            Task.FromResult((true, "head", "OK"));

        public Task<GitFileDiffResult> GetFileDiffAsync(string workspace, string relativePath, bool staged) => Task.FromResult(Diff);

        public Task<GitHunksResult> GetDiffHunksAsync(string workspace, string relativePath, bool staged) => Task.FromResult(Hunks);

        public Task<(bool Success, string Message)> ApplyHunkAsync(string workspace, string relativePath, string hunkId, string action) =>
            Task.FromResult((true, "Staged selected hunk"));

        public Task<GitBranchesResult> GetBranchesAsync(string workspace) => Task.FromResult(Branches);

        public Task<(bool success, string message)> CheckoutBranchAsync(string workspace, string branchName) =>
            Task.FromResult((true, $"Switched to {branchName}"));

        public Task<(bool success, string message)> CreateBranchAsync(string workspace, string branchName, bool checkout = true) =>
            Task.FromResult((true, $"Created {branchName}"));

        public Task<(bool success, string message)> PullAsync(string workspace) => Task.FromResult((true, "Already up to date."));

        public Task<(bool success, string message)> PushAsync(string workspace) => Task.FromResult((true, "Pushed to remote."));

        public Task<(bool success, string message)> FetchAsync(string workspace) => Task.FromResult((true, "Fetched latest from remotes."));

        public Task<(bool success, string message)> StashAsync(string workspace, string message = "") =>
            Task.FromResult((true, "Saved working directory to stash"));

        public Task<(bool success, string message)> StashPopAsync(string workspace) => Task.FromResult((true, "Applied stash"));

        public Task<List<GitCommitItem>> GetCommitHistoryAsync(string workspace, string? relativePath = null, int maxCount = 40)
        {
            LastHistoryPath = relativePath;
            return Task.FromResult(History);
        }
    }

    /// <summary>Collects what the domain sent, with a poll for the background git calls.</summary>
    private sealed class Recorder
    {
        private readonly List<(string Action, JsonElement Payload)> _messages = new();
        private readonly object _gate = new();

        public void Send(string action, object payload)
        {
            var element = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
            lock (_gate) { _messages.Add((action, element)); }
        }

        public IReadOnlyList<(string Action, JsonElement Payload)> Messages
        {
            get { lock (_gate) { return _messages.ToArray(); } }
        }

        public async Task<JsonElement> WaitForAsync(string action, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                var hit = Messages.LastOrDefault(m => m.Action == action);
                if (hit.Payload.ValueKind != JsonValueKind.Undefined) return hit.Payload;
                await Task.Delay(20);
            }
            throw new TimeoutException($"no '{action}' message was sent");
        }
    }
}
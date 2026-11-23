using System.Collections.Generic;
using System.Threading.Tasks;

namespace VoidCode.Services;

/// <summary>
/// The real <see cref="IGitClient"/>: one delegation per member to <see cref="GitService"/>, which keeps
/// The process seam, the porcelain/diff parsing and the availability probe exactly as they were.
/// </summary>
public sealed class GitClient : IGitClient
{
    public Task<GitStatusResult> GetStatusAsync(string workspace) => GitService.GetStatusAsync(workspace);

    public Task<(bool success, string message)> StageFileAsync(string workspace, string relativePath) =>
        GitService.StageFileAsync(workspace, relativePath);

    public Task<(bool success, string message)> UnstageFileAsync(string workspace, string relativePath) =>
        GitService.UnstageFileAsync(workspace, relativePath);

    public Task<(bool success, string message)> StageAllAsync(string workspace) => GitService.StageAllAsync(workspace);

    public Task<(bool success, string message)> UnstageAllAsync(string workspace) => GitService.UnstageAllAsync(workspace);

    public Task<(bool success, string message)> DiscardFileAsync(string workspace, string relativePath) =>
        GitService.DiscardFileAsync(workspace, relativePath);

    public Task<(bool success, string message)> CommitAsync(string workspace, string message) =>
        GitService.CommitAsync(workspace, message);

    public Task<(bool success, string content, string message)> GetHeadFileContentAsync(string workspace, string relativePath) =>
        GitService.GetHeadFileContentAsync(workspace, relativePath);

    public Task<GitFileDiffResult> GetFileDiffAsync(string workspace, string relativePath, bool staged) =>
        GitService.GetFileDiffAsync(workspace, relativePath, staged);

    public Task<GitHunksResult> GetDiffHunksAsync(string workspace, string relativePath, bool staged) =>
        GitService.GetDiffHunksAsync(workspace, relativePath, staged);

    public Task<(bool Success, string Message)> ApplyHunkAsync(string workspace, string relativePath, string hunkId, string action) =>
        GitService.ApplyHunkAsync(workspace, relativePath, hunkId, action);

    public Task<GitBranchesResult> GetBranchesAsync(string workspace) => GitService.GetBranchesAsync(workspace);

    public Task<(bool success, string message)> CheckoutBranchAsync(string workspace, string branchName) =>
        GitService.CheckoutBranchAsync(workspace, branchName);

    public Task<(bool success, string message)> CreateBranchAsync(string workspace, string branchName, bool checkout = true) =>
        GitService.CreateBranchAsync(workspace, branchName, checkout);

    public Task<(bool success, string message)> PullAsync(string workspace) => GitService.PullAsync(workspace);

    public Task<(bool success, string message)> PushAsync(string workspace) => GitService.PushAsync(workspace);

    public Task<(bool success, string message)> FetchAsync(string workspace) => GitService.FetchAsync(workspace);

    public Task<(bool success, string message)> StashAsync(string workspace, string message = "") =>
        GitService.StashAsync(workspace, message);

    public Task<(bool success, string message)> StashPopAsync(string workspace) => GitService.StashPopAsync(workspace);

    public Task<List<GitCommitItem>> GetCommitHistoryAsync(string workspace, string? relativePath = null, int maxCount = 40) =>
        GitService.GetCommitHistoryAsync(workspace, relativePath, maxCount);
}
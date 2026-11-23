using System.Collections.Generic;
using System.Threading.Tasks;

namespace VoidCode.Services;

/// <summary>
/// (third seam): the git operations the git domain performs. It exists because the git
/// Domain's *success* payloads were untestable: git is not installed on the dev/CI machine this was
/// Written on, so every handler test only ever saw "Git is not installed on this machine." A fake client
/// Lets the 21 routes' reply shapes be asserted - branch/staged/working/untracked mapping, hunk review,
/// History - without a repository, a binary or a subprocess.
///
/// The signatures mirror <see cref="GitService"/> exactly, tuples included, so <see cref="GitClient"/>
/// Is pure delegation and no behaviour moved.
/// </summary>
public interface IGitClient
{
    Task<GitStatusResult> GetStatusAsync(string workspace);

    Task<(bool success, string message)> StageFileAsync(string workspace, string relativePath);

    Task<(bool success, string message)> UnstageFileAsync(string workspace, string relativePath);

    Task<(bool success, string message)> StageAllAsync(string workspace);

    Task<(bool success, string message)> UnstageAllAsync(string workspace);

    Task<(bool success, string message)> DiscardFileAsync(string workspace, string relativePath);

    Task<(bool success, string message)> CommitAsync(string workspace, string message);

    Task<(bool success, string content, string message)> GetHeadFileContentAsync(string workspace, string relativePath);

    Task<GitFileDiffResult> GetFileDiffAsync(string workspace, string relativePath, bool staged);

    Task<GitHunksResult> GetDiffHunksAsync(string workspace, string relativePath, bool staged);

    Task<(bool Success, string Message)> ApplyHunkAsync(string workspace, string relativePath, string hunkId, string action);

    Task<GitBranchesResult> GetBranchesAsync(string workspace);

    Task<(bool success, string message)> CheckoutBranchAsync(string workspace, string branchName);

    Task<(bool success, string message)> CreateBranchAsync(string workspace, string branchName, bool checkout = true);

    Task<(bool success, string message)> PullAsync(string workspace);

    Task<(bool success, string message)> PushAsync(string workspace);

    Task<(bool success, string message)> FetchAsync(string workspace);

    Task<(bool success, string message)> StashAsync(string workspace, string message = "");

    Task<(bool success, string message)> StashPopAsync(string workspace);

    Task<List<GitCommitItem>> GetCommitHistoryAsync(string workspace, string? relativePath = null, int maxCount = 40);

    Task<GitBlameResult> GetBlameAsync(string workspace, string relativePath)
        => GitService.GetBlameAsync(workspace, relativePath);
}
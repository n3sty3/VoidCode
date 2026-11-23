using System;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// Text search in the workspace.
///
/// Handles these bridge actions: <c>search_workspace</c>, <c>cancel_search</c> and
/// <c>replace_workspace</c> - with their replies unchanged (<c>search_workspace_response</c>,
/// <c>search_workspace_cancelled</c>, <c>replace_workspace_response</c>, <c>tree_refreshed</c>), so the
/// Page needs no change. <c>search_codebase</c> deliberately stays in the switch for the LSP session:
/// Its answer merges language-server symbols with the local index and uses the LSP path-restriction
/// Helpers, which that session moves.
///
/// The domain owns the search cancellation token: a new search cancels the previous one, and the window
/// Calls <see cref="CancelPending"/> on close and on a workspace switch instead of poking a field.
/// </summary>
public sealed class SearchHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;
    private CancellationTokenSource? _searchCts;

    public SearchHandlers(Func<string?> workspace, Action<string, object> send)
    {
        _workspace = workspace;
        _send = send;
    }

    public sealed class WorkspaceSearchRequest
    {
        public required string Query { get; init; }
        public bool IsRegex { get; init; }
        public bool MatchCase { get; init; }
        public bool MatchWholeWord { get; init; }
        public string? FileFilter { get; init; }
        public string? ExcludeFilter { get; init; }
        /// <summary>Optional: the page sends its own id so it can match the answer to its request.</summary>
        public string? SearchId { get; init; }
    }

    public sealed class ReplaceRequest
    {
        public required string Query { get; init; }
        public string? ReplaceText { get; init; }
        public bool IsRegex { get; init; }
        public bool MatchCase { get; init; }
        public bool MatchWholeWord { get; init; }
        public string? FileFilter { get; init; }
        public string? ExcludeFilter { get; init; }
    }

    public sealed class NoPayload
    {
    }

    /// <summary>Cancels the running search and drops the token source (window close, workspace switch).</summary>
    public void CancelPending()
    {
        var previous = Interlocked.Exchange(ref _searchCts, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    public void Register(BridgeRouter router)
    {
        router.Register<WorkspaceSearchRequest, object?>(SearchRoutes.SearchWorkspace, SearchWorkspaceAsync, envelope: false);
        router.Register<NoPayload, object?>(SearchRoutes.CancelSearch, CancelSearchAsync, envelope: false);
        router.Register<ReplaceRequest, object?>(SearchRoutes.ReplaceWorkspace, ReplaceWorkspaceAsync, envelope: false);
    }

    private Task<object?> SearchWorkspaceAsync(WorkspaceSearchRequest request, BridgeContext context)
    {
        var query = request.Query;
        var searchId = string.IsNullOrEmpty(request.SearchId) ? context.Id : request.SearchId;
        var workspace = _workspace() ?? "";

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchCts, next);
        previous?.Cancel();
        previous?.Dispose();
        var token = next.Token;

        _ = Task.Run(() =>
        {
            try
            {
                var response = FileSystemService.SearchWorkspaceStructured(
                    workspace,
                    query,
                    request.IsRegex,
                    request.MatchCase,
                    request.MatchWholeWord,
                    request.FileFilter,
                    request.ExcludeFilter,
                    cancellationToken: token);
                response.SearchId = searchId!;
                if (!token.IsCancellationRequested) _send("search_workspace_response", response);
            }
            catch (OperationCanceledException)
            {
                _send("search_workspace_cancelled", new { searchId });
            }
            catch (Exception ex)
            {
                _send("search_workspace_response", new SearchWorkspaceResponse
                {
                    SearchId = searchId!,
                    Query = query,
                    Error = $"Search failed: {ex.Message}"
                });
            }
        }, token);

        return Task.FromResult<object?>(null);
    }

    private Task<object?> CancelSearchAsync(NoPayload request, BridgeContext context)
    {
        CancelPending();
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ReplaceWorkspaceAsync(ReplaceRequest request, BridgeContext context)
    {
        CancelPending();
        var query = request.Query;
        var replaceText = request.ReplaceText ?? "";
        var workspace = _workspace() ?? "";

        _ = Task.Run(() =>
        {
            var response = FileSystemService.ReplaceWorkspaceStructured(
                workspace,
                query,
                replaceText,
                request.IsRegex,
                request.MatchCase,
                request.MatchWholeWord,
                request.FileFilter,
                request.ExcludeFilter);
            _send("replace_workspace_response", response);

            if (response.ReplacedCount > 0 && !string.IsNullOrEmpty(workspace))
            {
                var tree = FileSystemService.GetDirectoryTree(workspace);
                _send("tree_refreshed", new { tree });
            }
        });

        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of the search domain.</summary>
public static class SearchRoutes
{
    public const string SearchWorkspace = "search_workspace";
    public const string CancelSearch = "cancel_search";
    public const string ReplaceWorkspace = "replace_workspace";
}
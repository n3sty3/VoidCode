using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// The rest of the LSP surface - the actions that touch a *workspace
/// Edit*, which is why they move as one unit and not with the read-only requests.
///
/// Seven cases: <c>lsp_rename</c>, <c>lsp_formatting</c>, <c>lsp_code_actions</c>,
/// <c>lsp_resolve_code_action</c>, <c>lsp_execute_command</c>, <c>resolve_lsp_workspace_edit</c> and
/// <c>search_codebase</c> (the last one merges language-server symbols with the codebase index and needs
/// The symbol-kind labels below).
///
/// Four pieces moved with them, because none of them means anything alone:
/// <list type="bullet">
/// <item><see cref="RequestReviewAsync"/> - the round-trip the window used to hand to
/// <c>LspService.Initialize</c>: announce <c>lsp_workspace_edit_review</c>, wait for the page's
/// <c>resolve_lsp_workspace_edit</c>, and give up after 90 seconds.</item>
/// <item>the pending-review dictionary that <c>resolve_lsp_workspace_edit</c> completes, with
/// <see cref="CancelPending"/> replacing the drain that lived in <c>MainWindow_Closed</c>.</item>
/// <item><c>RestrictLspCodeActionsToWorkspace</c> - a code action that edits outside the workspace loses
/// Its edits and says why.</item>
/// <item><c>IsPathWithinWorkspace</c> - the stricter sibling of <c>PathPolicy.IsInsideWorkspace</c>
/// (it refuses relative paths), used by the restriction above, by rename and by the review guard.</item>
/// </list>
/// </summary>
public sealed class LspEditHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LspApplyEditResult>> _pendingReviews = new();

    public LspEditHandlers(Func<string?> workspace, Action<string, object> send)
    {
        _workspace = workspace;
        _send = send;
    }

    public sealed class RenameRequest
    {
        public required string Path { get; init; }
        public int Line { get; init; }
        public int Col { get; init; }
        public string? NewName { get; init; }
    }

    public sealed class FormattingRequest
    {
        public required string Path { get; init; }
        public int TabSize { get; init; }
        public bool? InsertSpaces { get; init; }
    }

    public sealed class CodeActionsRequest
    {
        public required string Path { get; init; }
        public int StartLine { get; init; }
        public int StartColumn { get; init; }
        public int EndLine { get; init; }
        public int EndColumn { get; init; }
        public JsonArray? Diagnostics { get; init; }
    }

    public sealed class ResolveCodeActionRequest
    {
        public required string Path { get; init; }
        public JsonObject? CodeAction { get; init; }
    }

    public sealed class ExecuteCommandRequest
    {
        public required string Path { get; init; }
        public string? Command { get; init; }
        public JsonArray? Arguments { get; init; }
    }

    public sealed class ResolveWorkspaceEditRequest
    {
        public string? ReviewId { get; init; }
        public bool Applied { get; init; }
        public string? FailureReason { get; init; }
    }

    public sealed class SearchCodebaseRequest
    {
        public required string Query { get; init; }
        public string? SearchId { get; init; }
        public string? Path { get; init; }
    }

    public void Register(BridgeRouter router)
    {
        router.Register<RenameRequest, object?>(LspEditRoutes.Rename, RenameAsync, envelope: false);
        router.Register<FormattingRequest, object?>(LspEditRoutes.Formatting, FormattingAsync, envelope: false);
        router.Register<CodeActionsRequest, object?>(LspEditRoutes.CodeActions, CodeActionsAsync, envelope: false);
        router.Register<ResolveCodeActionRequest, object?>(LspEditRoutes.ResolveCodeAction, ResolveCodeActionAsync, envelope: false);
        router.Register<ExecuteCommandRequest, object?>(LspEditRoutes.ExecuteCommand, ExecuteCommandAsync, envelope: false);
        router.Register<ResolveWorkspaceEditRequest, object?>(LspEditRoutes.ResolveWorkspaceEdit, ResolveWorkspaceEditAsync, envelope: false);
        router.Register<SearchCodebaseRequest, object?>(LspEditRoutes.SearchCodebase, SearchCodebaseAsync, envelope: false);
    }

    /// <summary>
    /// Fails every review the page never answered (window close), so <c>LspService</c> callers stop
    /// Waiting instead of bleeding into a closed window.
    /// </summary>
    public void CancelPending()
    {
        foreach (var pending in _pendingReviews)
        {
            if (_pendingReviews.TryRemove(pending.Key, out var review))
            {
                review.TrySetResult(new LspApplyEditResult
                {
                    FailureReason = "VoidCode closed before the workspace edit was reviewed."
                });
            }
        }
    }

    // ---------------------------------------------------------------- handlers (moved verbatim)

    private Task<object?> RenameAsync(RenameRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        var line = request.Line > 0 ? request.Line : 1;
        var col = request.Col > 0 ? request.Col : 1;
        var newName = request.NewName ?? "";
        var workspace = _workspace();
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleRenameAsync(filePath, line, col, newName, workspace);
            if (string.IsNullOrWhiteSpace(workspace) ||
                result.Any(edit => !IsPathWithinWorkspace(workspace!, edit.Path)))
            {
                result.Clear();
            }
            _send("lsp_rename_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> FormattingAsync(FormattingRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        var tabSize = request.TabSize > 0 ? request.TabSize : 4;
        var insertSpaces = request.InsertSpaces ?? true;
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleFormattingAsync(filePath, tabSize, insertSpaces, _workspace());
            _send("lsp_formatting_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> CodeActionsAsync(CodeActionsRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        var startLine = request.StartLine > 0 ? request.StartLine : 1;
        var startColumn = request.StartColumn > 0 ? request.StartColumn : 1;
        var endLine = request.EndLine > 0 ? request.EndLine : startLine;
        var endColumn = request.EndColumn > 0 ? request.EndColumn : startColumn;
        var diagnostics = request.Diagnostics;
        var workspace = _workspace();
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleCodeActionsAsync(
                filePath, startLine, startColumn, endLine, endColumn, diagnostics, workspace);
            RestrictCodeActionsToWorkspace(result, workspace);
            _send("lsp_code_actions_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ResolveCodeActionAsync(ResolveCodeActionRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        var codeAction = request.CodeAction;
        var workspace = _workspace();
        _ = Task.Run(async () =>
        {
            var result = codeAction != null
                ? await LspService.HandleCodeActionResolveAsync(filePath, codeAction, workspace)
                : null;
            if (result != null) RestrictCodeActionsToWorkspace(new[] { result }, workspace);
            _send("lsp_resolve_code_action_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ExecuteCommandAsync(ExecuteCommandRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        var command = request.Command ?? "";
        var arguments = request.Arguments;
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleExecuteCommandAsync(filePath, command, arguments, _workspace());
            _send("lsp_execute_command_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ResolveWorkspaceEditAsync(ResolveWorkspaceEditRequest request, BridgeContext context)
    {
        var reviewId = request.ReviewId ?? "";
        var applied = request.Applied;
        var failureReason = request.FailureReason;
        if (_pendingReviews.TryRemove(reviewId, out var pendingReview))
        {
            pendingReview.TrySetResult(new LspApplyEditResult
            {
                Applied = applied,
                FailureReason = applied ? null : failureReason ?? "The operator declined the workspace edit."
            });
        }
        return Task.FromResult<object?>(null);
    }
private Task<object?> SearchCodebaseAsync(SearchCodebaseRequest request, BridgeContext context)
    {
        var query = request.Query;
        var searchId = string.IsNullOrEmpty(request.SearchId) ? context.Id : request.SearchId;
        var activeFile = request.Path ?? "";
        var workspace = _workspace() ?? "";
        _ = Task.Run(async () =>
        {
            var localTask = CodebaseIndexService.SearchAsync(workspace, query, 12);
            var lspTask = LspService.HandleWorkspaceSymbolsAsync(activeFile, query, workspace);
            var localResults = await localTask;
            var lspResults = await lspTask;

            var results = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in lspResults)
            {
                if (!IsPathWithinWorkspace(workspace, symbol.Path)) continue;
                var key = $"{symbol.Path}|{symbol.StartLine}|{symbol.Name}";
                if (!seen.Add(key)) continue;
                results.Add(new
                {
                    kind = GetLspSymbolKindLabel(symbol.Kind),
                    symbol = symbol.Name,
                    detail = symbol.ContainerName,
                    file = symbol.Path,
                    startLine = symbol.StartLine,
                    startColumn = symbol.StartColumn,
                    source = "language-server"
                });
                if (results.Count >= 100) break;
            }

            foreach (var local in localResults)
            {
                var matchingSymbol = local.Symbols.FirstOrDefault(symbol =>
                    symbol.Contains(query, StringComparison.OrdinalIgnoreCase));
                var key = $"{local.FilePath}|{local.StartLine}|{matchingSymbol}";
                if (!seen.Add(key)) continue;
                results.Add(new
                {
                    kind = matchingSymbol != null ? "symbol" : "chunk",
                    symbol = matchingSymbol ?? Path.GetFileName(local.FilePath),
                    detail = local.Preview,
                    file = local.FilePath,
                    startLine = local.StartLine,
                    startColumn = 1,
                    source = "local-index"
                });
            }
            _send("search_codebase_response", new { searchId, query, results });
        });
        return Task.FromResult<object?>(null);
    }

    // ---------------------------------------------------------------- helpers (moved verbatim)

    private static void RestrictCodeActionsToWorkspace(
        IEnumerable<LspCodeActionResult> actions,
        string? workspacePath)
    {
        foreach (var action in actions)
        {
            if (action.Edits.Count == 0) continue;
            if (string.IsNullOrWhiteSpace(workspacePath) ||
                action.Edits.Any(edit => !IsPathWithinWorkspace(workspacePath!, edit.Path)))
            {
                action.Edits.Clear();
                action.DisabledReason = "The language server requested an edit outside the active workspace.";
            }
        }
    }

    private static string GetLspSymbolKindLabel(int kind) => kind switch
    {
        1 => "file",
        2 => "module",
        3 => "namespace",
        4 => "package",
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        14 => "constant",
        22 => "enum member",
        23 => "struct",
        24 => "event",
        25 => "operator",
        26 => "type parameter",
        _ => "symbol"
    };

    /// <summary>
    /// Stricter than <c>PathPolicy.IsInsideWorkspace</c>: a relative candidate is refused outright, so a
    /// Language server cannot smuggle a path by leaving it unresolved.
    /// </summary>
    private static bool IsPathWithinWorkspace(string workspacePath, string candidatePath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(candidatePath)) return false;
            var workspace = Path.GetFullPath(workspacePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(candidatePath);
            var relative = Path.GetRelativePath(workspace, candidate);
            return !Path.IsPathRooted(relative) &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The review round-trip the window used to hand to <c>LspService.Initialize</c>.
    /// Announced as <c>lsp_workspace_edit_review</c>, answered by <c>resolve_lsp_workspace_edit</c>, given
    /// Up after 90 seconds - all three limits unchanged.
    /// </summary>
    public async Task<LspApplyEditResult> RequestReviewAsync(string language, string? label, JsonObject workspaceEdit)
    {
        if (LspService.HasUnsupportedResourceOperations(workspaceEdit))
        {
            return new LspApplyEditResult
            {
                FailureReason = "File create, rename, and delete operations are not supported by the language-server review flow."
            };
        }

        var edits = LspService.ParseWorkspaceEdit(workspaceEdit);
        if (edits.Count == 0)
        {
            return new LspApplyEditResult { FailureReason = "The language server returned no text edits." };
        }
        if (edits.Count >= 5000)
        {
            return new LspApplyEditResult { FailureReason = "The language server returned too many edits for safe review." };
        }

        var workspace = _workspace();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return new LspApplyEditResult { FailureReason = "No workspace is open." };
        }

        var affectedFiles = edits.Select(edit => edit.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (affectedFiles.Count > 40)
        {
            return new LspApplyEditResult
            {
                FailureReason = $"The language server requested edits in {affectedFiles.Count} files; the review limit is 40."
            };
        }

        var outsideWorkspace = affectedFiles.FirstOrDefault(path => !IsPathWithinWorkspace(workspace!, path));
        if (outsideWorkspace != null)
        {
            return new LspApplyEditResult
            {
                FailureReason = $"The language server requested an edit outside the workspace: {outsideWorkspace}"
            };
        }

        var reviewId = $"lsp_edit_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<LspApplyEditResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingReviews.TryAdd(reviewId, completion))
        {
            return new LspApplyEditResult { FailureReason = "Could not start the workspace-edit review." };
        }

        _send("lsp_workspace_edit_review", new
        {
            reviewId,
            language,
            label,
            edits,
            fileCount = affectedFiles.Count
        });

        var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        if (completed == completion.Task) return await completion.Task;

        if (_pendingReviews.TryRemove(reviewId, out var timedOut))
        {
            timedOut.TrySetResult(new LspApplyEditResult
            {
                FailureReason = "Workspace-edit review timed out."
            });
        }
        return await completion.Task;
    }
}

/// <summary>The action names of the workspace-edit half of the LSP domain.</summary>
public static class LspEditRoutes
{
    public const string Rename = "lsp_rename";
    public const string Formatting = "lsp_formatting";
    public const string CodeActions = "lsp_code_actions";
    public const string ResolveCodeAction = "lsp_resolve_code_action";
    public const string ExecuteCommand = "lsp_execute_command";
    public const string ResolveWorkspaceEdit = "resolve_lsp_workspace_edit";
    public const string SearchCodebase = "search_codebase";
}
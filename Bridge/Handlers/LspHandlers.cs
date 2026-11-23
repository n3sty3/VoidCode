using System;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// The LSP requests and document sync.
///
/// Handles read-only LSP requests and document sync: the eight read-only requests (<c>lsp_hover</c>, <c>lsp_definition</c>,
/// <c>lsp_completion</c>, <c>lsp_references</c>, <c>lsp_signature_help</c>,
/// <c>lsp_document_symbols</c>, <c>lsp_inlay_hints</c>, <c>lsp_semantic_tokens</c>), the four
/// <c>lsp_did_*</c> notifications and <c>lsp_restart</c>.
///
/// Workspace-edit and codebase-search actions live in LspEditHandlers: <c>lsp_rename</c>,
/// <c>lsp_formatting</c>, <c>lsp_code_actions</c>, <c>lsp_resolve_code_action</c>,
/// <c>lsp_execute_command</c>, <c>resolve_lsp_workspace_edit</c> and <c>search_codebase</c> all hang off
/// The workspace-edit review machinery - the pending-review dictionary, the
/// <c>RestrictLspCodeActionsToWorkspace</c> guard and the <c>RequestLspWorkspaceEditReviewAsync</c>
/// Round-trip the window registers with <c>LspService.Initialize</c>. Those move together or not at all,
/// And this file is what is left when they are taken out.
///
/// Replies keep their shape: <c>{ reqId, result }</c> for the requests, <c>{ reqId, items }</c> for
/// Completion, and <c>lsp_restarted</c> for the restart.
/// </summary>
public sealed class LspHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;

    public LspHandlers(Func<string?> workspace, Action<string, object> send)
    {
        _workspace = workspace;
        _send = send;
    }

    public sealed class PositionRequest
    {
        public required string Path { get; init; }
        public int Line { get; init; }
        public int Col { get; init; }

        public int LineNumber => Line > 0 ? Line : 1;
        public int ColumnNumber => Col > 0 ? Col : 1;
    }

    public sealed class FileRequest
    {
        public required string Path { get; init; }
    }

    public sealed class RangeRequest
    {
        public required string Path { get; init; }
        public int StartLine { get; init; }
        public int StartColumn { get; init; }
        public int EndLine { get; init; }
        public int EndColumn { get; init; }

        public int StartLineNumber => StartLine > 0 ? StartLine : 1;
        public int StartColumnNumber => StartColumn > 0 ? StartColumn : 1;
        public int EndLineNumber => EndLine > 0 ? EndLine : StartLineNumber;
        public int EndColumnNumber => EndColumn > 0 ? EndColumn : StartColumnNumber;
    }

    public sealed class DidOpenRequest
    {
        public required string Path { get; init; }
        public string? LanguageId { get; init; }
        public string? Content { get; init; }
    }

    public sealed class DidChangeRequest
    {
        public required string Path { get; init; }
        public int Version { get; init; }
        public string? Content { get; init; }
    }

    public sealed class NoPayload
    {
    }

    public void Register(BridgeRouter router)
    {
        router.Register<PositionRequest, object?>(LspRoutes.Hover, HoverAsync, envelope: false);
        router.Register<PositionRequest, object?>(LspRoutes.Definition, DefinitionAsync, envelope: false);
        router.Register<PositionRequest, object?>(LspRoutes.Completion, CompletionAsync, envelope: false);
        router.Register<PositionRequest, object?>(LspRoutes.References, ReferencesAsync, envelope: false);
        router.Register<PositionRequest, object?>(LspRoutes.SignatureHelp, SignatureHelpAsync, envelope: false);
        router.Register<FileRequest, object?>(LspRoutes.DocumentSymbols, DocumentSymbolsAsync, envelope: false);
        router.Register<RangeRequest, object?>(LspRoutes.InlayHints, InlayHintsAsync, envelope: false);
        router.Register<FileRequest, object?>(LspRoutes.SemanticTokens, SemanticTokensAsync, envelope: false);

        router.Register<DidOpenRequest, object?>(LspRoutes.DidOpen, DidOpenAsync, envelope: false);
        router.Register<DidChangeRequest, object?>(LspRoutes.DidChange, DidChangeAsync, envelope: false);
        router.Register<FileRequest, object?>(LspRoutes.DidSave, DidSaveAsync, envelope: false);
        router.Register<FileRequest, object?>(LspRoutes.DidClose, DidCloseAsync, envelope: false);

        router.Register<NoPayload, object?>(LspRoutes.Restart, RestartAsync, envelope: false);
    }

    // ---------------------------------------------------------------- handlers (moved verbatim)

    private Task<object?> HoverAsync(PositionRequest request, BridgeContext context)
    {
        var (filePath, line, col) = (request.Path, request.LineNumber, request.ColumnNumber);
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleHoverAsync(filePath, line, col, _workspace());
            _send("lsp_hover_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DefinitionAsync(PositionRequest request, BridgeContext context)
    {
        var (filePath, line, col) = (request.Path, request.LineNumber, request.ColumnNumber);
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleDefinitionAsync(filePath, line, col, _workspace());
            _send("lsp_definition_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> CompletionAsync(PositionRequest request, BridgeContext context)
    {
        var (filePath, line, col) = (request.Path, request.LineNumber, request.ColumnNumber);
        _ = Task.Run(async () =>
        {
            var items = await LspService.HandleCompletionAsync(filePath, line, col, _workspace());
            _send("lsp_completion_response", new { reqId = context.Id, items });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ReferencesAsync(PositionRequest request, BridgeContext context)
    {
        var (filePath, line, col) = (request.Path, request.LineNumber, request.ColumnNumber);
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleReferencesAsync(filePath, line, col, _workspace());
            _send("lsp_references_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> SignatureHelpAsync(PositionRequest request, BridgeContext context)
    {
        var (filePath, line, col) = (request.Path, request.LineNumber, request.ColumnNumber);
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleSignatureHelpAsync(filePath, line, col, _workspace());
            _send("lsp_signature_help_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DocumentSymbolsAsync(FileRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleDocumentSymbolsAsync(filePath, _workspace());
            _send("lsp_document_symbols_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> InlayHintsAsync(RangeRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        var startLine = request.StartLineNumber;
        var startColumn = request.StartColumnNumber;
        var endLine = request.EndLineNumber;
        var endColumn = request.EndColumnNumber;
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleInlayHintsAsync(
                filePath, startLine, startColumn, endLine, endColumn, _workspace());
            _send("lsp_inlay_hints_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> SemanticTokensAsync(FileRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        _ = Task.Run(async () =>
        {
            var result = await LspService.HandleSemanticTokensAsync(filePath, _workspace());
            _send("lsp_semantic_tokens_response", new { reqId = context.Id, result });
        });
        return Task.FromResult<object?>(null);
    }

    // ---------------------------------------------------------------- document sync

    private Task<object?> DidOpenAsync(DidOpenRequest request, BridgeContext context)
    {
        LspService.NotifyDidOpen(request.Path, request.LanguageId ?? "", request.Content ?? "", _workspace());
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DidChangeAsync(DidChangeRequest request, BridgeContext context)
    {
        var version = request.Version > 0 ? request.Version : 1;
        LspService.NotifyDidChange(request.Path, version, request.Content ?? "", _workspace());
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DidSaveAsync(FileRequest request, BridgeContext context)
    {
        LspService.NotifyDidSave(request.Path);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DidCloseAsync(FileRequest request, BridgeContext context)
    {
        LspService.NotifyDidClose(request.Path);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RestartAsync(NoPayload request, BridgeContext context)
    {
        LspService.ShutdownAll();
        _send("lsp_restarted", new { });
        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of the LSP domain (the workspace-edit half is still in MainWindow).</summary>
public static class LspRoutes
{
    public const string Hover = "lsp_hover";
    public const string Definition = "lsp_definition";
    public const string Completion = "lsp_completion";
    public const string References = "lsp_references";
    public const string SignatureHelp = "lsp_signature_help";
    public const string DocumentSymbols = "lsp_document_symbols";
    public const string InlayHints = "lsp_inlay_hints";
    public const string SemanticTokens = "lsp_semantic_tokens";
    public const string DidOpen = "lsp_did_open";
    public const string DidChange = "lsp_did_change";
    public const string DidSave = "lsp_did_save";
    public const string DidClose = "lsp_did_close";
    public const string Restart = "lsp_restart";
}
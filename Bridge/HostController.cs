using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;

namespace VoidCode.Bridge;

/// <summary>
/// Platform-agnostic IDE host coordinator shared between the Windows WPF/WebView2 host
/// and the macOS Photino/WKWebView host. Owns settings, active workspace, the typed
/// <see cref="BridgeRouter"/>, all 10 handler domains, file watching, and shutdown cleanup.
/// </summary>
public sealed class HostController
{
    public sealed record HostHooks(
        Action<string> SendRawJson,
        Action<string?> ApplyThemeIcon,
        Action<string?> OnWorkspaceMapped,
        Action? OnMinimize,
        Action? OnMaximize,
        SessionWindowHandlers.WindowChrome WindowChrome,
        Func<string?>? PromptFolder = null,
        Func<string?, string[]>? PromptFiles = null);

    private AppSettings _settings;
    private string? _currentWorkspace;
    private readonly HostHooks _hooks;
    private readonly WorkspaceWatcherService _workspaceWatcher;
    private readonly BridgeRouter _bridgeRouter = new();
    private readonly FileSystemHandlers _fileSystemHandlers;
    private readonly WorkspaceHandlers _workspaceHandlers;
    private readonly SearchHandlers _searchHandlers;
    private readonly GitHandlers _gitHandlers;
    private readonly LspHandlers _lspHandlers;
    private readonly LspEditHandlers _lspEditHandlers;
    private readonly TerminalHandlers _terminalHandlers;
    private readonly TestsDebugHandlers _testsDebugHandlers;
    private readonly SettingsAgentHandlers _settingsAgentHandlers;
    private readonly SessionWindowHandlers _sessionWindowHandlers;
    private readonly string _bridgeNonce = BridgeProtocol.CreateNonce();
    private volatile bool _handshakeConfirmed;

    public HostController(HostHooks hooks)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _settings = SettingsService.Load();
        _hooks.ApplyThemeIcon(_settings.Theme);

        _workspaceWatcher = new WorkspaceWatcherService(() => _currentWorkspace, SendToJs);
        _fileSystemHandlers = new FileSystemHandlers(() => _currentWorkspace, SendToJs);
        _workspaceHandlers = new WorkspaceHandlers(
            () => _settings,
            () => _currentWorkspace,
            SendToJs,
            folder => ActivateWorkspace(folder, notifyUi: true),
            hooks.PromptFolder,
            hooks.PromptFiles);
        _searchHandlers = new SearchHandlers(() => _currentWorkspace, SendToJs);
        _gitHandlers = new GitHandlers(() => _currentWorkspace, () => _settings, SendToJs);
        _lspHandlers = new LspHandlers(() => _currentWorkspace, SendToJs);
        _lspEditHandlers = new LspEditHandlers(() => _currentWorkspace, SendToJs);
        _terminalHandlers = new TerminalHandlers(() => _currentWorkspace, SendToJs);
        _testsDebugHandlers = new TestsDebugHandlers(() => _currentWorkspace, SendToJs);
        _settingsAgentHandlers = new SettingsAgentHandlers(
            () => _currentWorkspace,
            () => _settings,
            updated => _settings = updated,
            _hooks.ApplyThemeIcon,
            SendToJs);
        _sessionWindowHandlers = new SessionWindowHandlers(
            () => _currentWorkspace,
            () => _settings,
            updated => _settings = updated,
            _hooks.WindowChrome,
            SendToJs);

        RegisterBridgeActions();

        _currentWorkspace = _settings.LastWorkspace;
        if (string.IsNullOrEmpty(_currentWorkspace) || !Directory.Exists(_currentWorkspace))
        {
            _currentWorkspace = null;
        }
        TerminalService.ResetWorkingDir(_currentWorkspace);
    }

    public AppSettings Settings => _settings;
    public string? CurrentWorkspace => _currentWorkspace;
    public string BridgeNonce => _bridgeNonce;
    public bool HandshakeConfirmed => _handshakeConfirmed;

    public void ResetHandshake() => _handshakeConfirmed = false;

    public string BuildHandshakeJson() => BridgeProtocol.BuildHandshakeJson(_bridgeNonce);

    public void InitializeServices()
    {
        ActivateWorkspace(_currentWorkspace, notifyUi: false);

        LspService.Initialize(
            (path, diags) => SendToJs("lsp_diagnostics", new { path, diagnostics = diags }),
            (language, status, detail) => SendToJs("lsp_status", new { language, status, detail }),
            _lspEditHandlers.RequestReviewAsync);

        DebugService.Initialize((evt, payload) => SendToJs(evt, payload));
    }

    public void ActivateWorkspace(string? folderPath, bool notifyUi)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

        if (!string.IsNullOrEmpty(_currentWorkspace) &&
            !string.Equals(_currentWorkspace, folderPath, StringComparison.OrdinalIgnoreCase))
        {
            LspService.ShutdownAll();
            _searchHandlers.CancelPending();
            _fileSystemHandlers.CancelPending();
        }

        _currentWorkspace = folderPath;
        WorkspaceTrustService.ApplyTo(folderPath, notifyUi ? new Action<string, object>(SendToJs) : null);
        SettingsService.AddRecentWorkspace(_settings, folderPath);
        TerminalService.ResetWorkingDir(folderPath);
        _hooks.OnWorkspaceMapped(folderPath);
        _workspaceWatcher.Watch(folderPath);

        _ = Task.Run(async () =>
        {
            try { await CodebaseIndexService.BuildOrUpdateIndexAsync(folderPath); }
            catch (Exception ex) { LoggingService.Swallowed(ex, "HostController.BuildIndex"); }
        });

        if (notifyUi)
        {
            var tree = FileSystemService.GetDirectoryTree(folderPath);
            var checkpoints = FileSystemService.GetRecentCheckpointsSummary(folderPath);
            var recovery = StorageService.LoadRecoveryState(folderPath);
            SendToJs("workspace_changed", new
            {
                workspace = _currentWorkspace,
                recentWorkspaces = _settings.RecentWorkspaces,
                tree,
                checkpoints,
                recovery
            });
        }
    }

    public async Task HandleWebMessageAsync(string? sourceUri, string? rawMessage, Uri? allowedDocumentUri = null)
    {
        try
        {
            if (!BridgeProtocol.TryAccept(sourceUri, rawMessage, _bridgeNonce, out var node, out var rejected, allowedDocumentUri))
            {
                LoggingService.LogWarning($"Rejected WebView message: {rejected}.", nameof(HostController));
                return;
            }
            _handshakeConfirmed = true;

            var action = node["action"]?.GetValue<string>() ?? "";
            var id = node["id"]?.GetValue<string>() ?? "";

            if (WorkspaceTrustService.TryHandleMessage(action, node, SendToJs)) return;

            var routed = await _bridgeRouter.TryHandleAsync(action, id, node);
            if (routed.Handled)
            {
                if (routed.ExpectsEnvelope && !string.IsNullOrEmpty(id))
                {
                    SendToJs($"{action}_response", BridgeRouter.BuildEnvelope(id, routed));
                }
                return;
            }

            if (!string.IsNullOrEmpty(id))
            {
                SendToJs($"{action}_response", BridgeRouter.BuildEnvelope(id, BridgeRouter.UnknownActionOutcome(action)));
            }
            else
            {
                LoggingService.LogWarning($"Unhandled bridge notification '{action}'.", nameof(HostController));
            }
        }
        catch (Exception ex)
        {
            SendToJs("toast", new { type = "error", message = $"Host error: {ex.Message}" });
        }
    }

    public void SendToJs(string action, object payload)
    {
        var json = JsonSerializer.Serialize(new { action, payload });
        _hooks.SendRawJson(json);
    }

    public void Shutdown()
    {
        _settingsAgentHandlers.CancelPending();
        _searchHandlers.CancelPending();
        _fileSystemHandlers.CancelPending();
        _lspEditHandlers.CancelPending();
        _ = DebugService.StopDebuggingAsync();
        WorktreeMissionService.ShutdownAll();
        LspService.ShutdownAll();
        PtyManager.KillAll();
        TerminalService.KillActiveProcess();
        _workspaceWatcher.Dispose();
    }

    private void RegisterBridgeActions()
    {
        BridgeActions.Register(_bridgeRouter, _hooks.OnMinimize, _hooks.OnMaximize);
        _fileSystemHandlers.Register(_bridgeRouter);
        _workspaceHandlers.Register(_bridgeRouter);
        _searchHandlers.Register(_bridgeRouter);
        _gitHandlers.Register(_bridgeRouter);
        _lspHandlers.Register(_bridgeRouter);
        _lspEditHandlers.Register(_bridgeRouter);
        _terminalHandlers.Register(_bridgeRouter);
        _testsDebugHandlers.Register(_bridgeRouter);
        _settingsAgentHandlers.Register(_bridgeRouter);
        _sessionWindowHandlers.Register(_bridgeRouter);
    }
}

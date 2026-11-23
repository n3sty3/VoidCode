using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Native;
using VoidCode.Services;

namespace VoidCode;

public partial class MainWindow : Window
{
    private AppSettings _settings;
    private string? _currentWorkspace;
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

    public MainWindow()
    {
        InitializeComponent();

        // Ensure initial and maximized window sizes always fit inside the monitor WorkArea (above taskbar)
        var workArea = SystemParameters.WorkArea;
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        Width = Math.Min(1340, Math.Floor(workArea.Width * 0.92));
        Height = Math.Min(760, Math.Floor(workArea.Height * 0.90));
        Left = workArea.Left + Math.Floor((workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Floor((workArea.Height - Height) / 2);

        _settings = SettingsService.Load();
        ApplyThemeWindowIcon(_settings.Theme);
        _workspaceWatcher = new WorkspaceWatcherService(() => _currentWorkspace, SendToJs);
        // Register the actions the typed router owns before any message can arrive.
        // The file-system domain reads the workspace through this delegate, so the
        // Handlers never touch window state directly.
        _fileSystemHandlers = new FileSystemHandlers(() => _currentWorkspace, SendToJs);
        // The workspace domain needs the settings object, the current folder, the send
        // Channel and the one "switch to this folder" entry point (trust, watcher and LSP live there).
        _workspaceHandlers = new WorkspaceHandlers(
            () => _settings,
            () => _currentWorkspace,
            SendToJs,
            folder => ActivateWorkspace(folder, notifyUi: true));
        // The search domain owns the search cancellation token.
        _searchHandlers = new SearchHandlers(() => _currentWorkspace, SendToJs);
        // The git domain - one workspace read per action, and the agent engine for the
        // "generate commit message" affordance.
        _gitHandlers = new GitHandlers(() => _currentWorkspace, () => _settings, SendToJs);
        // The LSP requests and document sync.
        _lspHandlers = new LspHandlers(() => _currentWorkspace, SendToJs);
        // The workspace-edit half owns the pending-review dictionary the window used to
        // Hold, and supplies the review callback for LspService.
        _lspEditHandlers = new LspEditHandlers(() => _currentWorkspace, SendToJs);
        // The terminal domain - pty_init's output/exit delegates are wired inside it.
        _terminalHandlers = new TerminalHandlers(() => _currentWorkspace, SendToJs);
        // The test runner and the debugger - the run sequence lives here now.
        _testsDebugHandlers = new TestsDebugHandlers(() => _currentWorkspace, SendToJs);
        // Settings, the agent turn and inline assist - it owns both token sources, and the
        // Window keeps `_settings` because the title bar, the workspace and the operator read it too.
        _settingsAgentHandlers = new SettingsAgentHandlers(
            () => _currentWorkspace,
            () => _settings,
            updated => _settings = updated,
            ApplyThemeWindowIcon,
            SendToJs);
        // The session/window domain. The four chrome actions stay here as delegates: the
        // HWND, the close handshake flags and Close() are the window's, not a handler's.
        _sessionWindowHandlers = new SessionWindowHandlers(
            () => _currentWorkspace,
            () => _settings,
            updated => _settings = updated,
            new SessionWindowHandlers.WindowChrome(
                DragWindow,
                Close,
                () => _closeAck = true,
                () =>
                {
                    _closeAck = true;
                    _closeApproved = true;
                    Close();
                }),
            SendToJs);
        RegisterBridgeActions();
        _currentWorkspace = _settings.LastWorkspace;
        if (string.IsNullOrEmpty(_currentWorkspace) || !Directory.Exists(_currentWorkspace))
        {
            _currentWorkspace = null;
        }

        TerminalService.ResetWorkingDir(_currentWorkspace);

        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }
/// <summary>
    /// The Win32 chrome (dark title bar, rounded corners, caption colour) and
    /// The maximize clamp live in <see cref="WindowInterop"/>; the window only hooks them up.
    /// </summary>
    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowInterop.EnableDarkChrome(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowInterop.Hook);
    }

    /// <summary>
    /// The body of the old <c>window_drag</c> case. The session domain still reaches it
    /// Through its chrome delegate; the interop itself is one call into <see cref="WindowInterop"/>.
    /// </summary>
    private void DragWindow() => WindowInterop.Drag(this);

    private void ApplyThemeWindowIcon(string? theme) => WindowInterop.ApplyThemeIcon(this, theme);

    private bool _closeApproved;
    private bool _closeAck;

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeApproved || webView?.CoreWebView2 == null) return;

        // Ask the editor whether any tab has unsaved changes before the window goes away.
        e.Cancel = true;
        _closeAck = false;
        SendToJs("request_close", new { });

        // If the page never answers (broken or still loading), do not trap the user.
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (!_closeAck && !_closeApproved)
            {
                _closeApproved = true;
                Dispatcher.Invoke(Close);
            }
        });
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _settingsAgentHandlers.CancelPending();
        _searchHandlers.CancelPending();
        _fileSystemHandlers.CancelPending();
        _lspEditHandlers.CancelPending();
        _ = DebugService.StopDebuggingAsync();
        LspService.ShutdownAll();
        PtyManager.KillAll();
        TerminalService.KillActiveProcess();
        _workspaceWatcher.Dispose();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        RootGrid.Margin = new Thickness(0);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var webViewDataOverride = Environment.GetEnvironmentVariable("VOIDCODE_WEBVIEW2_USER_DATA_FOLDER");
            var appData = string.IsNullOrWhiteSpace(webViewDataOverride)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoidCode", "WebView2Data")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(webViewDataOverride));
            Directory.CreateDirectory(appData);

            var env = await CoreWebView2Environment.CreateAsync(null, appData);
            await webView.EnsureCoreWebView2Async(env);

            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            Directory.CreateDirectory(wwwroot);

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local",
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow
            );

            ActivateWorkspace(_currentWorkspace, notifyUi: false);

            LspService.Initialize((path, diags) =>
            {
                SendToJs("lsp_diagnostics", new { path, diagnostics = diags });
            }, (language, status, detail) =>
            {
                SendToJs("lsp_status", new { language, status, detail });
            }, _lspEditHandlers.RequestReviewAsync);

            DebugService.Initialize((evt, payload) => SendToJs(evt, payload));

            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            webView.CoreWebView2.ProcessFailed += (s, e) => LoggingService.LogError($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason})", source: nameof(MainWindow));
            CspPolicy.AttachTo(webView.CoreWebView2, wwwroot);
            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
#if DEBUG
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            webView.Source = new Uri("http://app.local/index.html");
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Failed to initialize WebView2.", ex, nameof(MainWindow));
            MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Deliver the nonce to the top-level document after navigation; retry briefly until
    // The page sends its first authenticated message in case ES modules were still loading on cold start.
    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        _handshakeConfirmed = false;
        var handshake = BridgeProtocol.BuildHandshakeJson(_bridgeNonce);
        PostHandshakeSafe(handshake);
        _ = Task.Run(async () =>
        {
            foreach (var delayMs in new[] { 150, 450, 1200 })
            {
                await Task.Delay(delayMs);
                if (_handshakeConfirmed) return;
                await Dispatcher.InvokeAsync(() => PostHandshakeSafe(handshake));
            }
        });
    }

    private void PostHandshakeSafe(string handshakeJson)
    {
        try { if (!_handshakeConfirmed && webView?.CoreWebView2 != null) webView.CoreWebView2.PostWebMessageAsJson(handshakeJson); }
        catch (Exception ex) { LoggingService.LogWarning($"Bridge handshake could not be delivered: {ex.Message}", nameof(MainWindow)); }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (BridgeProtocol.IsTrustedSource(e.Uri) || string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
        UriLauncher.OpenExternal(e.Uri);
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        UriLauncher.OpenExternal(e.Uri);
    }

    private void ActivateWorkspace(string? folderPath, bool notifyUi)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

        if (!string.IsNullOrEmpty(_currentWorkspace) &&
            !string.Equals(_currentWorkspace, folderPath, StringComparison.OrdinalIgnoreCase))
        {
            // Language servers are rooted to a workspace. Never reuse a session from the previous project.
            LspService.ShutdownAll();
            _searchHandlers.CancelPending();
            _fileSystemHandlers.CancelPending();
        }

        _currentWorkspace = folderPath;
        // Ask the user about a folder we have never opened, and tell the UI whether
        // This one is trusted (restricted mode is the default until they answer).
        WorkspaceTrustService.ApplyTo(folderPath, notifyUi ? new Action<string, object>(SendToJs) : null);
        SettingsService.AddRecentWorkspace(_settings, folderPath);
        TerminalService.ResetWorkingDir(folderPath);
        MapWorkspaceVirtualHost();
        _workspaceWatcher.Watch(folderPath);

        _ = Task.Run(async () =>
        {
            try { await CodebaseIndexService.BuildOrUpdateIndexAsync(folderPath); }
            catch (Exception ex) { LoggingService.Swallowed(ex, "MainWindow.BuildIndex"); }
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

    private void MapWorkspaceVirtualHost()
    {
        try
        {
            if (webView.CoreWebView2 != null && !string.IsNullOrEmpty(_currentWorkspace) && Directory.Exists(_currentWorkspace))
            {
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "workspace.local",
                    _currentWorkspace,
                    CoreWebView2HostResourceAccessKind.DenyCors
                );
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "MainWindow.MapWorkspaceVirtualHost"); }
    }

    /// <summary>
    /// The whole action surface, registered domain by domain. Every action the page sends is answered by one of these domains.
    /// Registered alongside
    /// <see cref="BridgeActions"/>. 
    /// </summary>
    private void RegisterBridgeActions()
    {
        BridgeActions.Register(_bridgeRouter, this);
        _fileSystemHandlers.Register(_bridgeRouter);
        // /10
        _workspaceHandlers.Register(_bridgeRouter);
        // /10
        _searchHandlers.Register(_bridgeRouter);
        // /10
        _gitHandlers.Register(_bridgeRouter);
        // /10 (requests + document sync)
        _lspHandlers.Register(_bridgeRouter);
        // /10 (workspace edits + codebase search)
        _lspEditHandlers.Register(_bridgeRouter);
        // /10 (terminal, PTY, run)
        _terminalHandlers.Register(_bridgeRouter);
        // /10 (test runner + debugger)
        _testsDebugHandlers.Register(_bridgeRouter);
        // /10 (settings, agent, inline assist)
        _settingsAgentHandlers.Register(_bridgeRouter);
        // /10 (session, window chrome, checkpoints)
        _sessionWindowHandlers.Register(_bridgeRouter);
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Frame + nonce validation lives in BridgeProtocol so it can be unit
            // Tested, and a hostile frame or a stale nonce never reaches the dispatch below.
            if (!BridgeProtocol.TryAccept(e.Source, e.TryGetWebMessageAsString(), _bridgeNonce, out var node, out var rejected))
            {
                LoggingService.LogWarning($"Rejected WebView message: {rejected}.", nameof(MainWindow));
                return;
            }
            _handshakeConfirmed = true;

            var action = node["action"]?.GetValue<string>() ?? "";
            var id = node["id"]?.GetValue<string>() ?? "";

            // Workspace-trust messages are handled by the service, not the switch.
            if (WorkspaceTrustService.TryHandleMessage(action, node, SendToJs)) return;

            // The typed router answers the actions registered with it (validated payloads, uniform
            // {id, ok, data|error} envelope). Skipped means no domain owns the action - see below.
            var routed = await _bridgeRouter.TryHandleAsync(action, id, node);
            if (routed.Handled)
            {
                if (routed.ExpectsEnvelope && !string.IsNullOrEmpty(id)) SendToJs($"{action}_response", BridgeRouter.BuildEnvelope(id, routed));
                return;
            }

            // All registered domain actions are handled above. Every action the page
            // Sends is answered above by one of the typed domains - BridgeActions (window state, JS
            // Diagnostics), FileSystemHandlers, WorkspaceHandlers, SearchHandlers, GitHandlers, LspHandlers,
            // LspEditHandlers, TerminalHandlers, TestsDebugHandlers, SettingsAgentHandlers and
            // SessionWindowHandlers - or by WorkspaceTrustService before the router runs.

            // Nothing owns this action. A request gets an `unknown_action`
            // Envelope (the old silent fall-out is what makes a typo'd action invisible); a notification is logged.
            if (!string.IsNullOrEmpty(id)) SendToJs($"{action}_response", BridgeRouter.BuildEnvelope(id, BridgeRouter.UnknownActionOutcome(action)));
            else LoggingService.LogWarning($"Unhandled bridge notification '{action}'.", nameof(MainWindow));
        }
        catch (Exception ex)
        {
            SendToJs("toast", new { type = "error", message = $"Host error: {ex.Message}" });
        }
    }

    private void SendToJs(string action, object payload)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (webView?.CoreWebView2 == null) return;
            var json = JsonSerializer.Serialize(new
            {
                action,
                payload
            });
            webView.CoreWebView2.PostWebMessageAsString(json);
        });
    }
}

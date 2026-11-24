using System;
using System.IO;
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
    private readonly HostController _host;
    private bool _closeApproved;
    private bool _closeAck;

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

        _host = new HostController(new HostController.HostHooks(
            SendRawJson: PostRawJsonToJs,
            ApplyThemeIcon: theme => WindowInterop.ApplyThemeIcon(this, theme),
            OnWorkspaceMapped: MapWorkspaceVirtualHost,
            OnMinimize: () => WindowState = WindowState.Minimized,
            OnMaximize: () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
            WindowChrome: new SessionWindowHandlers.WindowChrome(
                () => WindowInterop.Drag(this),
                Close,
                () => _closeAck = true,
                () =>
                {
                    _closeAck = true;
                    _closeApproved = true;
                    Close();
                })));

        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowInterop.EnableDarkChrome(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowInterop.Hook);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeApproved || webView?.CoreWebView2 == null) return;

        // Ask the editor whether any tab has unsaved changes before the window goes away.
        e.Cancel = true;
        _closeAck = false;
        _host.SendToJs("request_close", new { });

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
        _host.Shutdown();
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

            _host.InitializeServices();

            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            webView.CoreWebView2.ProcessFailed += (s, e) => LoggingService.LogError($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason})", source: nameof(MainWindow));
            WebView2Csp.AttachTo(webView.CoreWebView2, wwwroot);
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

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        _host.ResetHandshake();
        var handshake = _host.BuildHandshakeJson();
        PostHandshakeSafe(handshake);
        _ = Task.Run(async () =>
        {
            foreach (var delayMs in new[] { 150, 450, 1200 })
            {
                await Task.Delay(delayMs);
                if (_host.HandshakeConfirmed) return;
                await Dispatcher.InvokeAsync(() => PostHandshakeSafe(handshake));
            }
        });
    }

    private void PostHandshakeSafe(string handshakeJson)
    {
        try { if (!_host.HandshakeConfirmed && webView?.CoreWebView2 != null) webView.CoreWebView2.PostWebMessageAsJson(handshakeJson); }
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

    private void MapWorkspaceVirtualHost(string? workspacePath)
    {
        try
        {
            if (webView?.CoreWebView2 != null && !string.IsNullOrEmpty(workspacePath) && Directory.Exists(workspacePath))
            {
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "workspace.local",
                    workspacePath,
                    CoreWebView2HostResourceAccessKind.DenyCors
                );
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "MainWindow.MapWorkspaceVirtualHost"); }
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        await _host.HandleWebMessageAsync(e.Source, e.TryGetWebMessageAsString());
    }

    private void PostRawJsonToJs(string json)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (webView?.CoreWebView2 == null) return;
            webView.CoreWebView2.PostWebMessageAsString(json);
        });
    }
}

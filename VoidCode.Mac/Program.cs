using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Photino.NET;
using VoidCode.Agent.Mcp;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;

namespace VoidCode.Mac;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        PlatformServices.EnsureUnixPathEnvironment();
        LoggingService.LogInfo($"VoidCode ({PlatformServices.PlatformId}) starting up.");

        CliLaunchService.PendingTarget = CliLaunchService.ParseArgs(args);
        if (CliLaunchService.PendingTarget?.WorkspacePath != null)
        {
            LoggingService.LogInfo($"CLI target workspace: {CliLaunchService.PendingTarget.WorkspacePath}");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LoggingService.LogError("AppDomain UnhandledException", ex, nameof(Program));
                CliLaunchService.WriteCrashReport(ex, "AppDomain");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LoggingService.LogError("TaskScheduler UnobservedTaskException", e.Exception, nameof(Program));
            e.SetObserved();
        };

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(wwwroot);

        using var server = new LocalAssetServer(wwwroot);
        server.Start();

        PhotinoWindow? window = null;
        HostController? host = null;
        bool closeApproved = false;
        bool closeAck = false;

        window = new PhotinoWindow()
            .SetTitle("VoidCode")
            .SetUseOsDefaultSize(false)
            .SetSize(1360, 820)
            .SetMinSize(960, 600)
            .SetResizable(true)
            .Center()
            .SetContextMenuEnabled(false)
            .SetGrantBrowserPermissions(true)
#if DEBUG
            .SetDevToolsEnabled(true);
#else
            .SetDevToolsEnabled(false);
#endif

        // Register native macOS/OS NSOpenPanel dialogs through Photino.
        PlatformServices.RegisterDialogs(
            openFolder: () =>
            {
                var picked = window.ShowOpenFolder("Select Project Folder", multiSelect: false);
                return picked != null && picked.Length > 0 ? picked[0] : null;
            },
            openFiles: initialDir =>
            {
                var picked = window.ShowOpenFile(
                    "Select Files to Attach",
                    defaultPath: !string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) ? initialDir : null,
                    multiSelect: true);
                return picked ?? Array.Empty<string>();
            });

        host = new HostController(new HostController.HostHooks(
            SendRawJson: json =>
            {
                try { window.SendWebMessage(json); }
                catch (Exception ex) { LoggingService.Swallowed(ex, "Photino.SendWebMessage"); }
            },
            ApplyThemeIcon: _ => { /* macOS app icon is managed by the .app bundle Info.plist */ },
            OnWorkspaceMapped: ws => server.SetWorkspaceRoot(ws),
            OnMinimize: () => window.SetMinimized(true),
            OnMaximize: () => window.SetMaximized(!window.Maximized),
            WindowChrome: new SessionWindowHandlers.WindowChrome(
                () => { /* macOS uses native titlebar drag */ },
                () => window.Close(),
                () => closeAck = true,
                () =>
                {
                    closeAck = true;
                    closeApproved = true;
                    window.Close();
                })));

        host.InitializeServices();

        void DeliverHandshake()
        {
            try
            {
                if (!host.HandshakeConfirmed)
                {
                    window.SendWebMessage(host.BuildHandshakeJson());
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"Bridge handshake delivery failed: {ex.Message}", nameof(Program));
            }
        }

        window.RegisterWebMessageReceivedHandler((_, rawMessage) =>
        {
            // Photino's WKWebView bridge notifies us when bridge.js finishes installing so we push
            // the 256-bit cryptographic nonce immediately (before any authenticated message).
            try
            {
                var probe = JsonNode.Parse(rawMessage)?.AsObject();
                if (string.Equals(probe?["action"]?.GetValue<string>(), "bridge_ready", StringComparison.Ordinal))
                {
                    DeliverHandshake();
                    return;
                }
            }
            catch (Exception) { /* handled below by BridgeProtocol.TryAccept */ }

            _ = Task.Run(() => host.HandleWebMessageAsync(server.DocumentUri.AbsoluteUri, rawMessage, server.DocumentUri));
        });

        window.RegisterWindowClosingHandler((_, _) =>
        {
            if (closeApproved) return false;
            closeAck = false;
            host.SendToJs("request_close", new { });
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                if (!closeAck && !closeApproved)
                {
                    closeApproved = true;
                    window.Close();
                }
            });
            return true;
        });

        // Backup handshake pushes in case bridge_ready arrived before the handler settled.
        _ = Task.Run(async () =>
        {
            foreach (var delayMs in new[] { 200, 600, 1400 })
            {
                await Task.Delay(delayMs);
                if (host.HandshakeConfirmed) return;
                DeliverHandshake();
            }
        });

        window.Load(server.DocumentUri);
        window.WaitForClose();

        host.Shutdown();
        McpManager.ShutdownAll();
        LoggingService.LogInfo("VoidCode exiting cleanly.");
    }
}

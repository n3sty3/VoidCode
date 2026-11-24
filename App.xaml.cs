using System;
using System.Threading.Tasks;
using System.Windows;
using VoidCode.Agent.Mcp;
using VoidCode.Native;
using VoidCode.Services;

namespace VoidCode;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WindowsPlatformServices.Register();

        LoggingService.LogInfo("VoidCode application starting up.");
        CliLaunchService.PendingTarget = CliLaunchService.ParseArgs(e.Args);
        if (CliLaunchService.PendingTarget?.WorkspacePath != null)
        {
            LoggingService.LogInfo($"CLI target workspace: {CliLaunchService.PendingTarget.WorkspacePath}");
        }

        // Multi-window support: if another VoidCode window already holds the primary WebView2 data
        // folder (or --new-window was requested), automatically allocate an isolated WebView2 profile
        // subfolder instead of blocking the user from opening a second repository window.
        var wantsNewWindow = Array.Exists(e.Args, a => string.Equals(a, "--new-window", StringComparison.OrdinalIgnoreCase));
        var hasCustomDataDir = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOIDCODE_WEBVIEW2_USER_DATA_FOLDER"));
        if (!hasCustomDataDir)
        {
            _instanceMutex = new System.Threading.Mutex(true, "VoidCode.SingleInstance.v1", out var isFirstInstance);
            if (!isFirstInstance || wantsNewWindow)
            {
                var isolated = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VoidCode",
                    $"WebView2Data-win-{Environment.ProcessId}");
                Environment.SetEnvironmentVariable("VOIDCODE_WEBVIEW2_USER_DATA_FOLDER", isolated);
                LoggingService.LogInfo($"Opening additional VoidCode window with isolated WebView2 cache: {isolated}");
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LoggingService.LogError("AppDomain UnhandledException", ex);
                CliLaunchService.WriteCrashReport(ex, "AppDomain");
            }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LoggingService.LogError("DispatcherUnhandledException", args.Exception);
            var (_, summary) = CliLaunchService.WriteCrashReport(args.Exception, "UI Dispatcher");
            var choice = MessageBox.Show(
                summary + "\n\nClick Yes to open the logs folder, or No to keep working.",
                "VoidCode — Crash Diagnostics",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
            if (choice == MessageBoxResult.Yes)
            {
                LoggingService.OpenLogFolder();
            }
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LoggingService.LogError("TaskScheduler UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        McpManager.ShutdownAll();
        LoggingService.LogInfo($"VoidCode application exiting with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }
}

using System;
using System.Threading.Tasks;
using System.Windows;
using VoidCode.Agent.Mcp;
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

        LoggingService.LogInfo("VoidCode application starting up.");
        CliLaunchService.PendingTarget = CliLaunchService.ParseArgs(e.Args);
        if (CliLaunchService.PendingTarget?.WorkspacePath != null)
        {
            LoggingService.LogInfo($"CLI target workspace: {CliLaunchService.PendingTarget.WorkspacePath}");
        }

        // Single-instance guard: two copies fight over the WebView2 user-data folder and
        // The second fails to initialize. Allow explicit multi-instance for dev isolation.
        var allowMultiple = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOIDCODE_WEBVIEW2_USER_DATA_FOLDER"));
        if (!allowMultiple)
        {
            _instanceMutex = new System.Threading.Mutex(true, "VoidCode.SingleInstance.v1", out var isFirstInstance);
            if (!isFirstInstance)
            {
                MessageBox.Show(
                    "VoidCode is already running. Close the existing window first.\n\nTo run a second instance, set the VOIDCODE_WEBVIEW2_USER_DATA_FOLDER environment variable to a different folder.",
                    "VoidCode", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
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

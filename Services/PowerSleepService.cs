using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoidCode.Services;

/// <summary>
/// Prevents the OS from entering idle sleep while a long VoidAgent turn is executing.
/// Uses <c>SetThreadExecutionState</c> on Windows and <c>caffeinate -i</c> on macOS;
/// releases cleanly when disposed.
/// </summary>
public static class PowerSleepService
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public static IDisposable Acquire(bool enabled)
    {
        if (!enabled) return NoOpDisposable.Instance;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired);
                return new WindowsSleepGuard();
            }

            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("/usr/bin/caffeinate", "-i")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var proc = Process.Start(psi);
                if (proc != null) return new ProcessSleepGuard(proc);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "PowerSleepService.Acquire");
        }
        return NoOpDisposable.Instance;
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { /* nothing to release */ }
    }

    private sealed class WindowsSleepGuard : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (OperatingSystem.IsWindows()) SetThreadExecutionState(EsContinuous);
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "PowerSleepService.Release");
            }
        }
    }

    private sealed class ProcessSleepGuard : IDisposable
    {
        private readonly Process _process;
        private bool _disposed;
        public ProcessSleepGuard(Process process) => _process = process;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "PowerSleepService.KillCaffeinate");
            }
        }
    }
}

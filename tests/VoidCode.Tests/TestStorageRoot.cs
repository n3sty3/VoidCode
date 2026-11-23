using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace VoidCode.Tests;

// Every store the host owns lives under StorageService.Root, so one environment variable points the
// Whole profile - settings.json, credentials, operators, sessions, checkpoints, recovery - at a temp
// Directory for the length of the test run. Without this the suite rewrote the developer's real
// %APPDATA%\VoidCode: a test that opened a temp folder persisted it as LastWorkspace, and replace-in-file
// Tests left their checkpoints in the real checkpoints.json. Individual tests that need to assert
// Something specific still narrow this further with StorageService.UseRoot.
internal static class TestStorageRoot
{
    private static string? _root;

    [ModuleInitializer]
    internal static void RedirectProfile()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-tests-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("VOIDCODE_STORAGE_ROOT", _root);
        // Logs follow the same root already; set explicitly so a test that logs an error before the
        // First write cannot land in the developer's %APPDATA%\VoidCode\logs either.
        Environment.SetEnvironmentVariable("VOIDCODE_LOG_DIR", Path.Combine(_root, "logs"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* the run is over; a leftover temp dir is harmless */ }
            catch (UnauthorizedAccessException) { /* same */ }
        };
    }
}
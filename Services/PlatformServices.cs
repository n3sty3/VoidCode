using System;
using System.Diagnostics;
using System.IO;

namespace VoidCode.Services;

/// <summary>
/// Cross-platform OS integration hooks (file/folder dialogs, file-manager reveal, external URL launch,
/// and canonical platform identifiers). Desktop hosts (WPF on Windows, Photino/WKWebView on macOS)
/// register their native dialog implementations at startup via <see cref="RegisterDialogs"/>.
/// </summary>
public static class PlatformServices
{
    private static Func<string?>? _openFolderHandler;
    private static Func<string?, string[]>? _openFilesHandler;

    public static string PlatformId =>
        OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : "unknown";

    public static string DefaultShellName =>
        OperatingSystem.IsWindows() ? "PowerShell" : "zsh";

    public static string DefaultShellType =>
        OperatingSystem.IsWindows() ? "powershell" : "zsh";

    public static void RegisterDialogs(Func<string?> openFolder, Func<string?, string[]> openFiles)
    {
        _openFolderHandler = openFolder;
        _openFilesHandler = openFiles;
    }

    public static string? PromptOpenFolder()
    {
        if (_openFolderHandler != null)
        {
            return _openFolderHandler();
        }

        if (OperatingSystem.IsMacOS())
        {
            return RunAppleScriptPickFolder();
        }

        return null;
    }

    public static string[] PromptOpenFiles(string? initialDirectory = null)
    {
        if (_openFilesHandler != null)
        {
            return _openFilesHandler(initialDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return RunAppleScriptPickFiles(initialDirectory);
        }

        return Array.Empty<string>();
    }

    public static bool RevealInFileManager(string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (File.Exists(path))
                {
                    var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                    psi.ArgumentList.Add("-R");
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                    return true;
                }
                if (Directory.Exists(path))
                {
                    var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                    return true;
                }
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                var target = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
                {
                    var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                    psi.ArgumentList.Add(target);
                    Process.Start(psi);
                    return true;
                }
                return false;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string GetDefaultAppDataRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "VoidCode");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoidCode");
    }

    /// <summary>
    /// macOS GUI apps launched from Finder/Dock inherit a minimal PATH (/usr/bin:/bin:/usr/sbin:/sbin).
    /// Append Homebrew, /usr/local/bin, and standard user developer tool paths once at startup.
    /// </summary>
    public static void EnsureUnixPathEnvironment()
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var extraDirs = new[]
            {
                "/opt/homebrew/bin",
                "/opt/homebrew/sbin",
                "/usr/local/bin",
                Path.Combine(home, ".dotnet"),
                Path.Combine(home, ".dotnet", "tools"),
                Path.Combine(home, ".cargo", "bin"),
                Path.Combine(home, "go", "bin"),
                Path.Combine(home, ".local", "bin"),
                Path.Combine(StorageService.Root, "tools", "bin"),
                Path.Combine(StorageService.Root, "tools", "node_modules", ".bin")
            };

            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            var existing = new System.Collections.Generic.HashSet<string>(
                current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
            var additions = new System.Collections.Generic.List<string>();
            foreach (var dir in extraDirs)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && existing.Add(dir))
                {
                    additions.Add(dir);
                }
            }
            if (additions.Count > 0)
            {
                var updated = string.IsNullOrEmpty(current)
                    ? string.Join(Path.PathSeparator, additions)
                    : current + Path.PathSeparator + string.Join(Path.PathSeparator, additions);
                Environment.SetEnvironmentVariable("PATH", updated);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Could not enrich Unix PATH: {ex.Message}", nameof(PlatformServices));
        }
    }

    private static string? RunAppleScriptPickFolder()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("POSIX path of (choose folder with prompt \"Select Project Folder\")");
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;
            return output.TrimEnd('/');
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"macOS folder dialog failed: {ex.Message}", nameof(PlatformServices));
            return null;
        }
    }

    private static string[] RunAppleScriptPickFiles(string? initialDirectory)
    {
        try
        {
            var locationClause = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
                ? $" default location (POSIX file \"{initialDirectory.Replace("\\", "\\\\").Replace("\"", "\\\"")}\")"
                : "";
            var script =
                $"set picked to choose file with prompt \"Select Files to Attach\"{locationClause} with multiple selections allowed\n" +
                "set outList to {}\n" +
                "repeat with f in picked\n" +
                "  set end of outList to POSIX path of f\n" +
                "end repeat\n" +
                "set AppleScript's text item delimiters to linefeed\n" +
                "return outList as text";

            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var proc = Process.Start(psi);
            if (proc == null) return Array.Empty<string>();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"macOS file dialog failed: {ex.Message}", nameof(PlatformServices));
            return Array.Empty<string>();
        }
    }
}

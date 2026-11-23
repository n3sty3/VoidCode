using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VoidCode.Services;

/// <summary>
/// CLI argument parser (`voidcode .`, `voidcode path\to\dir`, `voidcode file.cs:42:10`)
/// And redacted crash-report writer for unhandled host exceptions.
/// </summary>
public sealed class CliLaunchTarget
{
    public string? WorkspacePath { get; init; }
    public string? FilePath { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
}

public static class CliLaunchService
{
    private static readonly Regex FileLineColRegex = new(
        @"^(?<path>.+?):(?<line>\d+)(?::(?<col>\d+))?$",
        RegexOptions.Compiled);

    public static CliLaunchTarget? PendingTarget { get; set; }

    public static CliLaunchTarget? ParseArgs(string[]? args, string? workingDirectory = null)
    {
        if (args == null || args.Length == 0) return null;
        var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;

        // Take the first non-flag positional argument.
        string? raw = null;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg.StartsWith("-", StringComparison.Ordinal)) continue;
            raw = arg.Trim().Trim('"');
            break;
        }
        if (string.IsNullOrEmpty(raw)) return null;

        int? line = null;
        int? col = null;
        string pathPart = raw;

        // Avoid splitting Windows drive colon (e.g. C:\foo) when no :line suffix is present.
        var m = FileLineColRegex.Match(raw);
        if (m.Success)
        {
            var candidatePath = m.Groups["path"].Value;
            if (candidatePath.Length > 1) // not just "C"
            {
                pathPart = candidatePath;
                if (int.TryParse(m.Groups["line"].Value, out var l)) line = Math.Max(1, l);
                if (m.Groups["col"].Success && int.TryParse(m.Groups["col"].Value, out var c)) col = Math.Max(1, c);
            }
        }

        string resolved;
        try
        {
            resolved = Path.IsPathRooted(pathPart)
                ? Path.GetFullPath(pathPart)
                : Path.GetFullPath(Path.Combine(cwd, pathPart));
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "CliLaunchService.ParseArgs");
            return null;
        }

        if (Directory.Exists(resolved))
        {
            return new CliLaunchTarget { WorkspacePath = resolved };
        }

        var parentDir = Path.GetDirectoryName(resolved);
        return new CliLaunchTarget
        {
            WorkspacePath = !string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir) ? parentDir : null,
            FilePath = resolved,
            Line = line,
            Column = col
        };
    }

    public static (string reportPath, string summary) WriteCrashReport(Exception exception, string source)
    {
        var logsDir = Path.Combine(StorageService.Root, "logs");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var reportPath = Path.Combine(logsDir, $"crash-{stamp}.txt");

        var sb = new StringBuilder();
        sb.AppendLine("VoidCode Crash Diagnostics");
        sb.AppendLine($"Timestamp (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Source:          {source}");
        sb.AppendLine($"OS:              {Environment.OSVersion}");
        sb.AppendLine($".NET Runtime:    {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("Exception:");
        sb.AppendLine(exception.ToString());

        var redacted = LoggingService.Redact(sb.ToString());
        try
        {
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(reportPath, redacted, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "CliLaunchService.WriteCrashReport");
        }

        var summary = $"VoidCode encountered an unexpected error ({source}): {LoggingService.Redact(exception.Message)}\n\nDiagnostics saved to:\n{reportPath}";
        return (reportPath, summary);
    }
}

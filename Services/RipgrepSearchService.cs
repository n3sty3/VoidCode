using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace VoidCode.Services;

/// <summary>
/// Ripgrep-accelerated workspace search (`rg --json`).
/// Uses a bundled or PATH `rg` binary when available, respects `.voidcodeignore`, streams matches,
/// Cancels cleanly by killing the child process, and falls back transparently to the C# walker
/// When `rg` is not installed.
/// </summary>
public static class RipgrepSearchService
{
    /// <summary>Optional test seam to override ripgrep executable discovery.</summary>
    internal static Func<string?>? ExecutableOverrideForTests { get; set; }

    public static string? FindExecutable()
    {
        if (ExecutableOverrideForTests != null)
            return ExecutableOverrideForTests();

        var rgBin = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", rgBin),
            Path.Combine(AppContext.BaseDirectory, rgBin),
            Path.Combine(StorageService.Root, "tools", "bin", rgBin),
            Path.Combine(StorageService.Root, "tools", rgBin)
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return LspService.FindInPath("rg.exe") ?? LspService.FindInPath("rg");
    }

    internal static List<string> BuildArguments(
        string rootPath,
        string pattern,
        bool isRegex,
        bool matchCase,
        bool matchWholeWord,
        string? fileFilter,
        string? excludeFilter)
    {
        var args = new List<string>
        {
            "--json",
            "--line-number",
            "--column",
            "--no-heading",
            "--no-require-git",
            "--max-filesize", "2M"
        };

        if (!isRegex) args.Add("--fixed-strings");
        args.Add(matchCase ? "--case-sensitive" : "--ignore-case");
        if (matchWholeWord) args.Add("--word-regexp");

        var voidcodeIgnore = Path.Combine(rootPath, ".voidcodeignore");
        if (File.Exists(voidcodeIgnore))
        {
            args.Add("--ignore-file");
            args.Add(voidcodeIgnore);
        }

        if (!string.IsNullOrWhiteSpace(fileFilter))
        {
            foreach (var glob in SplitGlobs(fileFilter))
            {
                args.Add("--glob");
                args.Add(glob);
            }
        }

        if (!string.IsNullOrWhiteSpace(excludeFilter))
        {
            foreach (var glob in SplitGlobs(excludeFilter))
            {
                args.Add("--glob");
                args.Add(glob.StartsWith("!", StringComparison.Ordinal) ? glob : "!" + glob);
            }
        }

        args.Add("--");
        args.Add(pattern);
        args.Add(rootPath);
        return args;
    }

    /// <summary>
    /// Attempts a ripgrep search. Returns null if ripgrep is not installed or fails to start so the
    /// Caller can transparently fall back to the built-in C# walker.
    /// </summary>
    public static SearchWorkspaceResponse? TrySearch(
        string rootPath,
        string pattern,
        bool isRegex,
        bool matchCase,
        bool matchWholeWord,
        string? fileFilter,
        string? excludeFilter,
        int maxMatches,
        CancellationToken cancellationToken)
    {
        var rgPath = FindExecutable();
        if (string.IsNullOrEmpty(rgPath) || !Directory.Exists(rootPath))
            return null;

        var args = BuildArguments(rootPath, pattern, isRegex, matchCase, matchWholeWord, fileFilter, excludeFilter);
        var psi = new ProcessStartInfo
        {
            FileName = rgPath,
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null) return null;

            using var cancelReg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { LoggingService.Swallowed(ex, "RipgrepSearchService.CancelKill"); }
            });

            var response = new SearchWorkspaceResponse { Query = pattern };
            var byFile = new Dictionary<string, SearchFileResult>(StringComparer.OrdinalIgnoreCase);

            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;

                ParseJsonMatchLine(line, rootPath, byFile, response, maxMatches);
                if (response.TotalMatches >= maxMatches)
                {
                    try
                    {
                        if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) { LoggingService.Swallowed(ex, "RipgrepSearchService.CapKill"); }
                    break;
                }
            }

            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);
            cancellationToken.ThrowIfCancellationRequested();

            // Rg exit codes: 0 = matches found, 1 = no matches, 2 = error (e.g. invalid regex)
            if (proc.ExitCode == 2 && response.TotalMatches == 0)
            {
                if (isRegex && !string.IsNullOrWhiteSpace(stderr))
                {
                    response.Error = $"Invalid regex: {stderr.Split('\n')[0].Trim()}";
                    return response;
                }
                return null;
            }

            response.Files = byFile.Values.ToList();
            response.FileCount = response.Files.Count;
            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "RipgrepSearchService.TrySearch");
            return null;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    internal static void ParseJsonMatchLine(
        string jsonLine,
        string rootPath,
        Dictionary<string, SearchFileResult> byFile,
        SearchWorkspaceResponse response,
        int maxMatches)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(jsonLine);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "RipgrepSearchService.ParseJsonMatchLine");
            return;
        }

        if (!string.Equals(node?["type"]?.GetValue<string>(), "match", StringComparison.Ordinal))
            return;

        var data = node?["data"];
        var rawPath = data?["path"]?["text"]?.GetValue<string>();
        if (string.IsNullOrEmpty(rawPath)) return;

        var fullPath = Path.IsPathRooted(rawPath) ? Path.GetFullPath(rawPath) : Path.GetFullPath(Path.Combine(rootPath, rawPath));
        var relPath = Path.GetRelativePath(rootPath, fullPath);
        var lineNumber = data?["line_number"]?.GetValue<int>() ?? 1;
        var lineText = (data?["lines"]?["text"]?.GetValue<string>() ?? "").TrimEnd('\r', '\n');
        var preview = lineText.Length > 220 ? lineText[..220] + "..." : lineText;

        var submatches = data?["submatches"]?.AsArray();
        if (submatches == null || submatches.Count == 0) return;

        if (!byFile.TryGetValue(fullPath, out var fileResult))
        {
            fileResult = new SearchFileResult
            {
                RelativePath = relPath,
                FullPath = fullPath
            };
            byFile[fullPath] = fileResult;
        }

        foreach (var sm in submatches)
        {
            if (response.TotalMatches >= maxMatches) break;
            var start = sm?["start"]?.GetValue<int>() ?? 0;
            var end = sm?["end"]?.GetValue<int>() ?? start;
            fileResult.Matches.Add(new SearchMatchItem
            {
                Line = lineNumber,
                Column = start + 1,
                Length = Math.Max(1, end - start),
                Preview = preview
            });
            response.TotalMatches++;
        }
    }

    private static IEnumerable<string> SplitGlobs(string filter)
    {
        return filter
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
    }
}

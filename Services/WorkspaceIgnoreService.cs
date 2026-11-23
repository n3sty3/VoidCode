using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VoidCode.Services;

public sealed class WorkspaceIgnoreMatcher
{
    private sealed record IgnoreRule(Regex Regex, bool Negated);

    private readonly string _workspace;
    private readonly HashSet<string> _alwaysIgnoredDirectories;
    private readonly List<IgnoreRule> _rules = new();

    public WorkspaceIgnoreMatcher(string workspace, IEnumerable<string> alwaysIgnoredDirectories)
    {
        _workspace = Path.GetFullPath(workspace)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _alwaysIgnoredDirectories = new HashSet<string>(alwaysIgnoredDirectories, StringComparer.OrdinalIgnoreCase);

        // Later files intentionally win when two rules match.
        LoadRules(Path.Combine(_workspace, ".gitignore"));
        LoadRules(Path.Combine(_workspace, ".ignore"));
        LoadRules(Path.Combine(_workspace, ".voidcodeignore"));
    }

    public bool IsIgnored(string fullPath, bool isDirectory = false)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(_workspace, Path.GetFullPath(fullPath))
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }
        catch
        {
            return true;
        }

        if (relative == ".") return false;
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            return true;

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (_alwaysIgnoredDirectories.Contains(segment)) return true;
        }

        bool ignored = false;
        var matchPath = isDirectory ? relative.TrimEnd('/') + "/" : relative;
        foreach (var rule in _rules)
        {
            if (rule.Regex.IsMatch(matchPath)) ignored = !rule.Negated;
        }
        return ignored;
    }

    private void LoadRules(string ignoreFile)
    {
        if (!File.Exists(ignoreFile)) return;
        try
        {
            foreach (var rawLine in File.ReadLines(ignoreFile))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

                bool negated = line.StartsWith('!');
                if (negated) line = line[1..].TrimStart();
                if (string.IsNullOrEmpty(line)) continue;

                bool anchored = line.StartsWith('/');
                if (anchored) line = line[1..];
                bool directoryOnly = line.EndsWith('/');
                line = line.TrimEnd('/');
                if (string.IsNullOrEmpty(line)) continue;

                var regex = BuildRegex(line, anchored, directoryOnly);
                _rules.Add(new IgnoreRule(regex, negated));
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Could not read {Path.GetFileName(ignoreFile)}: {ex.Message}", nameof(WorkspaceIgnoreMatcher));
        }
    }

    private static Regex BuildRegex(string pattern, bool anchored, bool directoryOnly)
    {
        pattern = pattern.Replace('\\', '/');
        var expression = new StringBuilder();
        expression.Append(anchored ? "^" : "(?:^|.*/)");

        for (int i = 0; i < pattern.Length; i++)
        {
            char current = pattern[i];
            if (current == '*')
            {
                bool isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDouble)
                {
                    i++;
                    bool followedBySlash = i + 1 < pattern.Length && pattern[i + 1] == '/';
                    if (followedBySlash)
                    {
                        i++;
                        expression.Append("(?:.*/)?");
                    }
                    else
                    {
                        expression.Append(".*");
                    }
                }
                else
                {
                    expression.Append("[^/]*");
                }
            }
            else if (current == '?')
            {
                expression.Append("[^/]");
            }
            else
            {
                expression.Append(Regex.Escape(current.ToString()));
            }
        }

        // A matching directory pattern also excludes everything below it. This
        // Is harmless for file-only patterns because files cannot have children.
        expression.Append(directoryOnly ? "(?:/.*)?/?$" : "(?:/.*)?$");
        return new Regex(expression.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

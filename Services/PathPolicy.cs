using System;
using System.IO;

namespace VoidCode.Services;

// The single authoritative containment check.
//
// Everything before this did only Path.GetFullPath + a ".." test. That misses two whole classes
// Of escape:
//   * a junction or symlink anywhere in the path (a workspace link pointing at C:\Windows
//     Passed the ".." test because the string never contained ".."), and
//   * the shapes Windows treats specially - \\?\, \\.\, UNC shares, alternate data streams
//     (file.txt:stream), drive-relative paths (C:foo) and trailing dots/spaces that Windows
//     Silently strips before touching the disk.
//
// IsInsideWorkspace validates the shape, canonicalizes against the root and then walks the path
// Component by component resolving link targets, so the answer describes the path the file
// System will actually use.
public static class PathPolicy
{
    // A junction chain longer than this is treated as hostile rather than followed further.
    private const int MaxLinkHops = 40;

    public static bool IsInsideWorkspace(string? workspaceRoot, string? candidatePath) =>
        TryResolveInsideWorkspace(workspaceRoot, candidatePath, out _);

    /// <summary>
    /// True when <paramref name="candidatePath"/> (absolute, or relative to the workspace root)
    /// Really lives inside <paramref name="workspaceRoot"/> once links are resolved.
    /// <paramref name="resolvedPath"/> is the canonical, link-resolved path on success.
    /// Never throws: anything it cannot prove is reported as outside.
    /// </summary>
    public static bool TryResolveInsideWorkspace(string? workspaceRoot, string? candidatePath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(candidatePath)) return false;

        if (!TryCanonicalize(workspaceRoot!, out var root)) return false;
        // A workspace may legitimately be reached through a link (a junction to another drive),
        // So compare against the real location of both ends.
        var realRoot = ResolveFinalTarget(root);

        var path = candidatePath!;
        // Validate unsafe path shapes (UNC, device prefixes, ADS, trailing dots/spaces) BEFORE
        // combining with root — otherwise on POSIX `Path.Combine(root, "\\\\server\\share")` hides the prefix.
        if (HasUnsafeShape(path)) return false;
        if (!OperatingSystem.IsWindows())
        {
            // Reject Windows drive-qualified paths ("C:\...") on POSIX and treat backslashes as separators
            // so Windows-style traversal ("..\outside.txt") can never slip past containment on macOS/Linux.
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return false;
            path = path.Replace('\\', '/');
        }
        if (!Path.IsPathRooted(path)) path = Path.Combine(root, path);
        if (!TryCanonicalize(path, out var candidate)) return false;

        var realCandidate = ResolveFinalTarget(candidate);
        if (!IsWithin(realRoot, realCandidate)) return false;

        resolvedPath = realCandidate;
        return true;
    }

    private static bool TryCanonicalize(string path, out string canonical)
    {
        canonical = string.Empty;
        if (HasUnsafeShape(path)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Keep a drive root ("C:\") intact rather than degrading it to "C:"
            canonical = trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
            return canonical.Length > 0;
        }
        catch (Exception)
        {
            // Invalid characters, malformed separators, path too long ...
            return false;
        }
    }

    private static bool HasUnsafeShape(string path)
    {
        // Device / extended-length namespaces
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return true;
        // UNC shares (\\server\share and //server/share)
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;

        // A colon is only allowed as a drive designator ("C:\"), never as a stream separator
        // ("file.txt:stream", "file.txt::$DATA") nor as a drive-relative path ("C:foo").
        var colon = path.IndexOf(':');
        if (colon >= 0)
        {
            var driveDesignator = colon == 1 && path.Length > 2 && (path[2] == '\\' || path[2] == '/');
            if (!driveDesignator) return true;
            // A second colon after the drive ("C:\ws\a.txt:stream") is an alternate data stream
            if (path.IndexOf(':', 2) >= 0) return true;
        }

        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") continue;
            // Windows trims these before hitting the file system, so the name on disk can
            // Differ from the name that was checked.
            if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // Boundary-aware and case-insensitive: "C:\ws" must not match "C:\wsx".
    private static bool IsWithin(string root, string candidate)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Follows every link on the way down so the result is the real location.</summary>
    private static string ResolveFinalTarget(string canonicalPath)
    {
        try
        {
            var volume = Path.GetPathRoot(canonicalPath) ?? string.Empty;
            var current = volume;
            foreach (var segment in canonicalPath.Substring(volume.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0) continue;
                current = ResolveLink(Path.Combine(current, segment), 0);
            }
            return current;
        }
        catch (Exception)
        {
            return canonicalPath;
        }
    }

    private static string ResolveLink(string path, int hops)
    {
        if (hops >= MaxLinkHops) return path;

        var isDirectory = Directory.Exists(path);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        FileAttributes attributes;
        try
        {
            if (!info.Exists) return path; // nothing on disk yet - nothing to resolve
            attributes = info.Attributes;
        }
        catch (Exception)
        {
            return path; // unreadable: fall back to the literal path
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0) return path;

        try
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target == null) return path;
            // A chain of junctions is possible, so resolve the target as well.
            return ResolveLink(target.FullName, hops + 1);
        }
        catch (Exception)
        {
            return path;
        }
    }
}

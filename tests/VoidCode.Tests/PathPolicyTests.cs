using System;
using System.Diagnostics;
using System.IO;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// Containment must survive junctions/symlinks and the path shapes Windows treats
// Specially. These are the tests the plan calls for: a junction to outside, "..", UNC, ADS and
// The extended-length prefixes.
public class PathPolicyTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vc-pathpolicy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process == null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public void FilesAndFoldersInsideTheWorkspaceAreAccepted()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.cs"), "x");

            Assert.True(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "src", "a.cs")));
            Assert.True(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "src")));
            // A file that does not exist yet still counts as inside (the editor creates files)
            Assert.True(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "src", "new.cs")));
            // The root itself is inside
            Assert.True(PathPolicy.IsInsideWorkspace(root, root));
            // Relative paths resolve against the root
            Assert.True(PathPolicy.IsInsideWorkspace(root, Path.Combine("src", "a.cs")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ParentTraversalIsRejected()
    {
        var root = NewTempDir();
        try
        {
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "..")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "..", "outside.txt")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "src", "..", "..", "outside.txt")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, "..\\outside.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SiblingWithTheSamePrefixIsRejected()
    {
        var root = NewTempDir();
        try
        {
            // "C:\ws" must not match "C:\wsx"
            Assert.False(PathPolicy.IsInsideWorkspace(root, root + "x\\a.txt"));
            Assert.False(PathPolicy.IsInsideWorkspace(root, root + "-evil\\a.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WindowsPathShapesAreRejected()
    {
        var root = NewTempDir();
        try
        {
            // UNC shares, in both separators
            Assert.False(PathPolicy.IsInsideWorkspace(root, @"\\server\share\a.txt"));
            Assert.False(PathPolicy.IsInsideWorkspace(root, "//server/share/a.txt"));
            // Extended-length and device namespaces
            Assert.False(PathPolicy.IsInsideWorkspace(root, @"\\?\C:\ws\a.txt"));
            Assert.False(PathPolicy.IsInsideWorkspace(root, @"\\.\C:\ws\a.txt"));
            // Alternate data streams
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "a.txt:stream")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "a.txt::$DATA")));
            // Drive-relative paths are ambiguous, so they are refused
            Assert.False(PathPolicy.IsInsideWorkspace(root, "C:a.txt"));
            // Windows trims trailing dots and spaces before touching the disk
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "a.txt.")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "a.txt ")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "folder. ", "a.txt")));
            // Nothing to work with
            Assert.False(PathPolicy.IsInsideWorkspace(null, Path.Combine(root, "a.txt")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, null));
            Assert.False(PathPolicy.IsInsideWorkspace(root, "   "));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CaseDifferencesDoNotChangeTheAnswer()
    {
        var root = NewTempDir();
        try
        {
            var upper = root.ToUpperInvariant();
            Assert.True(PathPolicy.IsInsideWorkspace(root, Path.Combine(upper, "A.TXT")));
            Assert.True(PathPolicy.IsInsideWorkspace(upper, Path.Combine(root, "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AJunctionLeavingTheWorkspaceIsRejected()
    {
        var sandbox = NewTempDir();
        var root = Path.Combine(sandbox, "root");
        var outside = Path.Combine(sandbox, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(root, "link");
        try
        {
            if (!TryCreateJunction(link, outside)) return; // no junction support here: skip

            // The path string never contained "..", which is exactly why the old
            // GetFullPath + ".." check let this through.
            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(link, "secret.txt")));
            Assert.False(PathPolicy.IsInsideWorkspace(root, link));

            File.WriteAllText(Path.Combine(root, "real.txt"), "ok");
            Assert.True(PathPolicy.IsInsideWorkspace(root, Path.Combine(root, "real.txt")));
        }
        finally { Cleanup(link, sandbox); }
    }

    [Fact]
    public void AJunctionStayingInsideTheWorkspaceIsAccepted()
    {
        var root = NewTempDir();
        var target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.cs"), "x");
        var link = Path.Combine(root, "alias");
        try
        {
            if (!TryCreateJunction(link, target)) return;

            Assert.True(PathPolicy.IsInsideWorkspace(root, Path.Combine(link, "a.cs")));
            Assert.True(PathPolicy.IsInsideWorkspace(root, link));
        }
        finally { Cleanup(link, root); }
    }

    [Fact]
    public void AWorkspaceReachedThroughAJunctionStillWorks()
    {
        var sandbox = NewTempDir();
        var real = Path.Combine(sandbox, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.cs"), "x");
        var alias = Path.Combine(sandbox, "alias");
        try
        {
            if (!TryCreateJunction(alias, real)) return;

            // Opening a workspace through a junction must keep working ...
            Assert.True(PathPolicy.IsInsideWorkspace(alias, Path.Combine(alias, "a.cs")));
            Assert.True(PathPolicy.IsInsideWorkspace(alias, Path.Combine(real, "a.cs")));
            // ... but the sandbox around it is still outside
            Assert.False(PathPolicy.IsInsideWorkspace(alias, sandbox));
        }
        finally { Cleanup(alias, sandbox); }
    }

    [Fact]
    public void ASymlinkLeavingTheWorkspaceIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sandbox = NewTempDir();
        var root = Path.Combine(sandbox, "root");
        var outside = Path.Combine(sandbox, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(root, "symlink");
        try
        {
            // Creating a symlink needs Developer Mode or elevation, so a failure means "skip"
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) { LoggingService.Swallowed(ex, "PathPolicyTests.symlinkSetup"); return; }

            Assert.False(PathPolicy.IsInsideWorkspace(root, Path.Combine(link, "secret.txt")));
        }
        finally { Cleanup(link, sandbox); }
    }

    [Fact]
    public void ResolvedPathPointsAtTheRealLocation()
    {
        var root = NewTempDir();
        var target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.cs"), "x");
        var link = Path.Combine(root, "alias");
        try
        {
            if (TryCreateJunction(link, target))
            {
                Assert.True(PathPolicy.TryResolveInsideWorkspace(root, Path.Combine(link, "a.cs"), out var throughLink));
                Assert.Equal(Path.Combine(target, "a.cs"), throughLink, ignoreCase: true);
                return;
            }

            Assert.True(PathPolicy.TryResolveInsideWorkspace(root, target, out var realTarget));
            Assert.True(PathPolicy.TryResolveInsideWorkspace(root, Path.Combine(target, "a.cs"), out var plain));
            Assert.Equal(Path.Combine(realTarget, "a.cs"), plain, ignoreCase: true);
        }
        finally { Cleanup(link, root); }
    }

    // Best-effort teardown that never masks an assertion failure. The link is removed explicitly
    // So the target outside the sandbox is not followed.
    private static void Cleanup(string link, string sandbox)
    {
        try { if (Directory.Exists(link)) Directory.Delete(link); }
        catch (Exception ex) { LoggingService.Swallowed(ex, "PathPolicyTests.removeLink"); }
        try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); }
        catch (Exception ex) { LoggingService.Swallowed(ex, "PathPolicyTests.removeSandbox"); }
    }
}

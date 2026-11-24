using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("StorageRoot")]
public class AgentV2Tests
{
    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voidcode-v2-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void UnifiedDiffService_ComputesExactAddedAndRemovedCounts()
    {
        var before = "line1\nline2\nline3\nline4";
        var after = "line1\nline2_mod\nline3\nline3_extra\nline4";

        var diff = UnifiedDiffService.Compute(before, after, "demo.cs");
        Assert.Equal(2, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Contains("@@ ", diff.DiffText);
        Assert.Contains("- line2", diff.DiffText);
        Assert.Contains("+ line2_mod", diff.DiffText);
        Assert.Contains("+ line3_extra", diff.DiffText);
    }

    [Fact]
    public void MultiPatchFileSmart_AppliesAtomicallyAndPreservesFileOnFailure()
    {
        var dir = CreateTempDir("multipatch");
        var file = Path.Combine(dir, "sample.cs");
        try
        {
            var initial = "int a = 1;\nint b = 2;\nint c = 3;\n";
            File.WriteAllText(file, initial);

            // Failing second hunk must leave file completely untouched.
            var fail = FileSystemService.MultiPatchFileSmart(file, new[]
            {
                ("int a = 1;", "int a = 10;"),
                ("DOES_NOT_EXIST", "int z = 99;")
            });
            Assert.False(fail.success);
            Assert.Equal(initial, File.ReadAllText(file));

            // Valid multi-hunk edit updates both hunks with a single checkpoint.
            var ok = FileSystemService.MultiPatchFileSmart(file, new[]
            {
                ("int a = 1;", "int a = 10;"),
                ("int c = 3;", "int c = 30;")
            });
            Assert.True(ok.success);
            Assert.NotNull(ok.checkpoint);
            var updated = File.ReadAllText(file);
            Assert.Contains("int a = 10;", updated);
            Assert.Contains("int c = 30;", updated);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void GlobFiles_MatchesPatternsAndSkipsIgnoredDirs()
    {
        var dir = CreateTempDir("glob");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "src", "nested"));
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "pkg"));
            File.WriteAllText(Path.Combine(dir, "src", "App.cs"), "");
            File.WriteAllText(Path.Combine(dir, "src", "nested", "Helper.cs"), "");
            File.WriteAllText(Path.Combine(dir, "src", "readme.md"), "");
            File.WriteAllText(Path.Combine(dir, "node_modules", "pkg", "Ignored.cs"), "");

            var csFiles = FileSystemService.GlobFiles(dir, "**/*.cs");
            Assert.Equal(2, csFiles.Count);
            Assert.Contains("src/App.cs", csFiles);
            Assert.Contains("src/nested/Helper.cs", csFiles);
            Assert.DoesNotContain(csFiles, f => f.Contains("node_modules"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void AgentSessionMemory_TracksTracesAndMidRunSteerQueue()
    {
        var ws = CreateTempDir("session-mem");
        try
        {
            AgentSessionMemory.ClearWorkspace(ws);
            AgentSessionMemory.ClearSteers();

            AgentSessionMemory.RecordAction(ws, "read_file(Program.cs) -> ok");
            AgentSessionMemory.RecordAction(ws, "multi_patch_file(Program.cs) -> ok");

            var block = AgentSessionMemory.FormatRecentWorkForPrompt(ws);
            Assert.Contains("RECENT SESSION TOOL TRACE", block);
            Assert.Contains("Program.cs", block);

            AgentSessionMemory.EnqueueSteer("Use Debug build");
            AgentSessionMemory.EnqueueSteer("Keep zero warnings");
            var steers = AgentSessionMemory.DrainSteers();
            Assert.Equal(2, steers.Count);
            Assert.Equal("Use Debug build", steers[0]);
            Assert.Empty(AgentSessionMemory.DrainSteers());
        }
        finally
        {
            AgentSessionMemory.ClearWorkspace(ws);
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void AgentLoop_ParallelBatchingAllowsOnlySafeInWorkspaceReadTools()
    {
        var ws = CreateTempDir("par");
        try
        {
            Assert.True(AgentLoop.IsParallelizableReadOnlyTool("read_file", "{\"path\":\"a.cs\"}", ws, false));
            Assert.True(AgentLoop.IsParallelizableReadOnlyTool("glob_files", "{\"pattern\":\"**/*.cs\"}", ws, false));
            Assert.True(AgentLoop.IsParallelizableReadOnlyTool("get_lsp_diagnostics", "{}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("patch_file", "{\"path\":\"a.cs\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("multi_patch_file", "{\"path\":\"a.cs\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("run_command", "{\"command\":\"ls\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("delegate_research", "{\"question\":\"hi\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("read_file", "{\"path\":\"../escape.txt\"}", ws, false));
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public async Task GitPowerService_ValidatesUrlsHashesAndParsesGraph()
    {
        Assert.True(GitPowerService.IsValidCloneUrl("https://github.com/n3sty3/VoidCode.git"));
        Assert.True(GitPowerService.IsValidCloneUrl("git@github.com:n3sty3/VoidCode.git"));
        Assert.False(GitPowerService.IsValidCloneUrl("--upload-pack=evil"));
        Assert.False(GitPowerService.IsValidCloneUrl("file:///etc/passwd"));

        var badShow = await GitPowerService.ShowCommitAsync(Path.GetTempPath(), "not-a-hash;rm -rf");
        Assert.False(badShow.Ok);

        var sampleLog = "*   VCSEPabcdef1234567890\u001fabcdef1\u001fDevid\u001f2 hours ago\u001fHEAD -> main, origin/main\u001ffeat(v2): VoidAgent recode\n|\\  \n| * VCSEP1234567890abcdef\u001f1234567\u001fAda\u001f1 day ago\u001f\u001ffix: parser edge case\n";
        var parsed = GitPowerService.ParseCommitGraphOutput(sampleLog);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("abcdef1", parsed[0].ShortHash);
        Assert.Contains("HEAD -> main", parsed[0].Refs);
        Assert.Equal("feat(v2): VoidAgent recode", parsed[0].Subject);
        Assert.Equal("| *", parsed[1].Graph);
    }

    [Fact]
    public void HostController_ConstructsAndRegistersAllBridgeRoutesWithoutCollisions()
    {
        var dir = CreateTempDir("host-ctrl");
        using var scope = StorageService.UseRoot(dir);
        try
        {
            var hooks = new VoidCode.Bridge.HostController.HostHooks(
                SendRawJson: _ => { },
                ApplyThemeIcon: _ => { },
                OnWorkspaceMapped: _ => { },
                OnMinimize: null,
                OnMaximize: null,
                WindowChrome: new VoidCode.Bridge.Handlers.SessionWindowHandlers.WindowChrome(
                    () => { },
                    () => { },
                    () => { },
                    () => { }));

            var controller = new VoidCode.Bridge.HostController(hooks);
            Assert.NotNull(controller);
            Assert.False(string.IsNullOrWhiteSpace(controller.BridgeNonce));
            controller.Shutdown();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }
}

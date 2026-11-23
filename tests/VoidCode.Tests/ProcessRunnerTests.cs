using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The seam is process-wide static state (GitService.ProcessRunner), so this class must not run next to
// Anything else that talks to git.
[Collection("Process seam")]
public class ProcessRunnerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "voidcode-git-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _fake = new();

    public ProcessRunnerTests()
    {
        Directory.CreateDirectory(_workspace);
        // A fake that claims exit 0 for the availability probe, so nothing touches the real machine.
        _fake.Respond = _ => new ProcessResult(0, "", "");
        GitService.UseProcessSeam(_fake, "git");
    }

    public void Dispose()
    {
        GitService.UseProcessSeam();
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task RunGitAsync_UsesTheInjectedRunner()
    {
        _fake.Respond = _ => new ProcessResult(0, "out\n", "err\n");

        var (exitCode, stdout, stderr) = await GitService.RunGitAsync(_workspace, "status --porcelain=v1");

        Assert.Equal(0, exitCode);
        Assert.Equal("out\n", stdout);
        Assert.Equal("err\n", stderr);

        var call = Assert.Single(_fake.Calls);
        Assert.Equal("git", call.FileName);
        Assert.Equal("status --porcelain=v1", call.Arguments);
        Assert.Equal(_workspace, call.WorkingDirectory);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesPorcelainWithoutASubprocess()
    {
        // Git is only consulted for the .git marker; the porcelain text comes from the fake.
        Directory.CreateDirectory(Path.Combine(_workspace, ".git"));
        _fake.Respond = call => call.Arguments!.StartsWith("status", StringComparison.Ordinal)
            ? new ProcessResult(0, "## feature/seam...origin/feature/seam\nM  staged.cs\n M working.cs\n?? new.txt\n", "")
            : new ProcessResult(0, "", "");

        var status = await GitService.GetStatusAsync(_workspace);

        Assert.True(status.IsAvailable);
        Assert.True(status.IsRepo);
        Assert.Equal("feature/seam", status.Branch);
        Assert.Equal("staged.cs", Assert.Single(status.StagedChanges).Path);
        Assert.Equal(2, status.WorkingChanges.Count);
        Assert.Contains(status.WorkingChanges, c => c.Path == "working.cs" && c.StatusDesc == "Modified");
        Assert.Contains(status.WorkingChanges, c => c.Path == "new.txt" && c.StatusDesc == "Untracked");
    }

    [Fact]
    public async Task ApplyHunkAsync_ReviewsAHunkAndSendsThePatchOnStandardInput()
    {
        var diff = string.Join('\n',
            "diff --git a/src/Program.cs b/src/Program.cs",
            "index 1111111..2222222 100644",
            "--- a/src/Program.cs",
            "+++ b/src/Program.cs",
            "@@ -1,2 +1,2 @@",
            " class Alpha",
            "-class Beta",
            "+class Gamma",
            "");
        var patch = diff;

        // First call renders the diff (hunk ids come from the real parser), the second applies it.
        _fake.Respond = call => call.ArgumentList is { Count: > 0 } argv && argv[0] == "diff"
            ? new ProcessResult(0, patch, "")
            : new ProcessResult(0, "", "");

        var hunks = await GitService.GetDiffHunksAsync(_workspace, "src/Program.cs", staged: false);
        Assert.True(hunks.Success);
        var hunk = Assert.Single(hunks.Hunks);

        var (success, message) = await GitService.ApplyHunkAsync(_workspace, "src/Program.cs", hunk.Id, "stage");

        Assert.True(success, message);
        var apply = Assert.Single(_fake.Calls, c => c.ArgumentList is { Count: > 0 } argv && argv[0] == "apply");
        // Argument list, not a command line: no shell quoting; the patch itself arrives on stdin.
        Assert.Contains("--cached", apply.ArgumentList!);
        Assert.Contains("-", apply.ArgumentList!);
        Assert.Equal(hunk.Patch, apply.StandardInput);
    }

    [Fact]
    public async Task SystemProcessRunner_CapturesOutputAndTheExitCode()
    {
        var runner = new SystemProcessRunner();

        var echo = await runner.RunAsync("cmd.exe", "/c echo seam-check");
        var failure = await runner.RunAsync("cmd.exe", new[] { "/c", "exit", "3" });

        Assert.Equal(0, echo.ExitCode);
        Assert.Contains("seam-check", echo.Stdout);
        Assert.Equal(3, failure.ExitCode);
    }

    /// <summary>Records every call and answers with whatever the test set up.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public sealed record Call(
            string FileName,
            string? Arguments,
            IReadOnlyList<string>? ArgumentList,
            string? WorkingDirectory,
            string? StandardInput);

        private readonly List<Call> _calls = new();

        public Func<Call, ProcessResult> Respond { get; set; } = _ => new ProcessResult(0, "", "");

        public IReadOnlyList<Call> Calls
        {
            get { lock (_calls) { return _calls.ToArray(); } }
        }

        public Task<ProcessResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            var call = new Call(fileName, arguments, null, workingDirectory, null);
            lock (_calls) { _calls.Add(call); }
            return Task.FromResult(Respond(call));
        }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            var call = new Call(fileName, string.Join(' ', arguments), arguments, workingDirectory, standardInput);
            lock (_calls) { _calls.Add(call); }
            return Task.FromResult(Respond(call));
        }
    }
}
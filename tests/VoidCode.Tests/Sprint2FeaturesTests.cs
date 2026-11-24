using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("Process seam")]
public class Sprint2FeaturesTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request));
    }

    private sealed class FakeGitRunner : IProcessRunner
    {
        public List<string> Commands { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            var joined = string.Join(' ', arguments);
            Commands.Add(joined);

            // Simulate `git worktree add -b <branch> <path> HEAD` creating the worktree directory
            if (arguments.Count >= 4 && arguments[0] == "worktree" && arguments[1] == "add")
            {
                var targetDir = arguments[4];
                Directory.CreateDirectory(targetDir);
                return Task.FromResult(new ProcessResult(0, "Preparing worktree", ""));
            }
            if (arguments.Count >= 2 && arguments[0] == "status")
            {
                return Task.FromResult(new ProcessResult(0, " M src/App.cs\n", ""));
            }
            if (arguments.Count >= 2 && arguments[0] == "diff" && arguments[1] == "--stat")
            {
                return Task.FromResult(new ProcessResult(0, " src/App.cs | 4 ++++", ""));
            }
            if (arguments.Count >= 1 && arguments[0] == "diff")
            {
                return Task.FromResult(new ProcessResult(0, "+// added by mission\n", ""));
            }
            return Task.FromResult(new ProcessResult(0, "ok", ""));
        }
    }

    [Fact]
    public void EmbeddingService_LocalVectorRanksMorphologicalMatchesHigher()
    {
        var queryVec = EmbeddingService.ComputeLocalVector("authenticating user credentials token");
        var authVec = EmbeddingService.ComputeLocalVector("class AuthTokenService { public bool AuthenticateUserCredentials() {} }");
        var mathVec = EmbeddingService.ComputeLocalVector("class MatrixSolver { public double ComputeDeterminant() {} }");

        var authSim = EmbeddingService.CosineSimilarity(queryVec, authVec);
        var mathSim = EmbeddingService.CosineSimilarity(queryVec, mathVec);

        Assert.True(authSim > mathSim + 0.2, $"Expected authSim ({authSim:F3}) >> mathSim ({mathSim:F3})");
    }

    [Fact]
    public async Task EmbeddingService_TryEmbedViaOllama_ParsesAndNormalizesVector()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"embeddings\":[[3.0,4.0]]}", Encoding.UTF8, "application/json")
        }));

        var vec = await EmbeddingService.TryEmbedViaOllamaAsync("hello world", "nomic-embed-text", http);
        Assert.NotNull(vec);
        Assert.Equal(2, vec!.Length);
        Assert.InRange(vec[0], 0.59f, 0.61f);
        Assert.InRange(vec[1], 0.79f, 0.81f);
    }

    [Fact]
    public async Task CodebaseIndexService_HybridSearchFindsFuzzyAndSymbolMatches()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"voidcode_hybrid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            File.WriteAllText(Path.Combine(ws, "AuthManager.cs"),
                "namespace Demo;\npublic class AuthManager {\n  public bool VerifySessionToken(string token) {\n    return !string.IsNullOrEmpty(token);\n  }\n}\n");
            File.WriteAllText(Path.Combine(ws, "PhysicsEngine.cs"),
                "namespace Demo;\npublic class PhysicsEngine {\n  public double CalculateGravity(double mass) {\n    return mass * 9.81;\n  }\n}\n");

            await CodebaseIndexService.BuildOrUpdateIndexAsync(ws);
            var results = await CodebaseIndexService.SearchAsync(ws, "verifying authentication session", topK: 5);

            Assert.NotEmpty(results);
            Assert.Equal("AuthManager.cs", results[0].FilePath);
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public async Task WorktreeMissionService_RunsLifecycle_StartDiffMergeDiscard()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), $"voidcode_mstore_{Guid.NewGuid():N}");
        var ws = Path.Combine(Path.GetTempPath(), $"voidcode_mrepo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempStorage);
        Directory.CreateDirectory(Path.Combine(ws, ".git"));

        var fakeGit = new FakeGitRunner();
        using var storageScope = StorageService.UseRoot(tempStorage);
        GitService.UseProcessSeam(fakeGit, "git");
        WorkspaceTrustService.SetTrusted(ws, true);

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        WorktreeMissionService.AgentRunnerOverrideForTests = (prompt, worktreeDir, settings, emit, ct) =>
        {
            emit("agent_tool_call", new { tool = "patch_file" });
            emit("agent_done", new { steps = 2, toolCalls = 1, totalTokens = 512, costLabel = "$0 (Local)", finalMessage = "Mission complete." });
            doneTcs.TrySetResult(true);
            return Task.CompletedTask;
        };

        try
        {
            var start = await WorktreeMissionService.StartMissionAsync(ws, "Add tests", "Write tests for App.cs", new AppSettings());
            Assert.True(start.Ok, start.Message);
            Assert.NotNull(start.Mission);
            Assert.StartsWith("voidcode/mission-", start.Mission!.Branch);

            await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(120);

            var list = WorktreeMissionService.ListMissions(ws);
            var snap = Assert.Single(list, m => m.Id == start.MissionId);
            Assert.Equal("completed", snap.Status);
            Assert.Equal("$0 (Local)", snap.CostLabel);

            var diff = await WorktreeMissionService.GetMissionDiffAsync(start.MissionId);
            Assert.True(diff.Ok);
            Assert.Contains("added by mission", diff.UnifiedDiff);

            var merged = await WorktreeMissionService.MergeMissionAsync(start.MissionId);
            Assert.True(merged.Ok, merged.Message);
            Assert.Contains(fakeGit.Commands, c => c.StartsWith("merge --no-ff", StringComparison.Ordinal));

            var discarded = await WorktreeMissionService.DiscardMissionAsync(start.MissionId);
            Assert.True(discarded.Ok);
            Assert.DoesNotContain(WorktreeMissionService.ListMissions(ws), m => m.Id == start.MissionId);
        }
        finally
        {
            WorktreeMissionService.AgentRunnerOverrideForTests = null;
            GitService.UseProcessSeam();
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
            try { Directory.Delete(tempStorage, true); } catch (IOException) { /* cleanup */ }
        }
    }
}

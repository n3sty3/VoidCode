using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// /10: the 21 git routes. git is NOT installed on this machine, so the
// Tests cannot assert a real repository operation - they cover the two things the migration is
// Responsible for: the payload validation the switch never had, and that every route still reaches its
// Response/toast. GitService short-circuits without the binary, which keeps these deterministic.
public class GitHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly GitHandlers _handlers;
    private readonly AppSettings _settings = new();

    public GitHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P { }");

        _handlers = new GitHandlers(() => _root, () => _settings, _recorder.Send);
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json = "{}")
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void AllTwentyOneRoutesAreRegistered()
    {
        var gitActions = _router.Actions.Where(action => action.StartsWith("git_", StringComparison.Ordinal) ||
                                                         action == GitRoutes.GenerateCommitMessage).ToArray();

        Assert.Equal(22, gitActions.Length);
        Assert.Contains(GitRoutes.GetStatus, gitActions);
        Assert.Contains(GitRoutes.GetBlame, gitActions);
        Assert.Contains(GitRoutes.GenerateCommitMessage, gitActions);
    }

    [Fact]
    public async Task GetStatus_AnswersWithTheStatusShape()
    {
        await Dispatch(GitRoutes.GetStatus, "req_1");

        var reply = await _recorder.WaitForAsync("git_status_response");
        // The response class carries [JsonPropertyName] names; the page reads branch/isRepo/message
        Assert.True(reply.TryGetProperty("branch", out _) || reply.TryGetProperty("isRepo", out _) ||
                    reply.TryGetProperty("message", out _));
    }

    [Theory]
    [InlineData(GitRoutes.StageFile)]
    [InlineData(GitRoutes.UnstageFile)]
    [InlineData(GitRoutes.DiscardFile)]
    [InlineData(GitRoutes.GetHeadContent)]
    [InlineData(GitRoutes.GetFileDiff)]
    [InlineData(GitRoutes.GetHunks)]
    public async Task PathRoutes_RejectAPayloadWithoutAPath(string action)
    {
        var outcome = await Dispatch(action, "req_2");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }

    [Theory]
    [InlineData(GitRoutes.Commit)]
    [InlineData(GitRoutes.CheckoutBranch)]
    [InlineData(GitRoutes.CreateBranch)]
    public async Task MessageAndBranchRoutes_RejectAPayloadWithoutTheirRequiredField(string action)
    {
        var outcome = await Dispatch(action, "req_3", "{}");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }
[Fact]
    public async Task StageFile_StillReportsThroughStatusAndToast()
    {
        var path = Path.Combine(_root, "Program.cs");

        await Dispatch(GitRoutes.StageFile, "req_4", JsonSerializer.Serialize(new { path }));

        // Git is missing here, so the toast is an error - what matters is that both messages still go
        // Out in the old order (status first, then the toast).
        var toast = await _recorder.WaitForAsync("toast");
        Assert.False(string.IsNullOrEmpty(toast.GetProperty("message").GetString()));
        var actions = _recorder.Messages.Select(m => m.Action).ToList();
        Assert.True(actions.IndexOf("git_status_response") < actions.IndexOf("toast"));
    }

    [Fact]
    public async Task GetHistory_AnswersWithThePathAndACommitList()
    {
        await Dispatch(GitRoutes.GetHistory, "req_5", JsonSerializer.Serialize(new { path = "Program.cs" }));

        var reply = await _recorder.WaitForAsync("git_history_response");
        Assert.Equal("Program.cs", reply.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Array, reply.GetProperty("commits").ValueKind);
    }

    [Fact]
    public async Task GetBranches_AnswersWithABranchList()
    {
        await Dispatch(GitRoutes.GetBranches, "req_6");

        var reply = await _recorder.WaitForAsync("git_branches_response");
        Assert.True(reply.ValueKind == JsonValueKind.Object || reply.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task StashWithAMessage_ReachesTheToast()
    {
        await Dispatch(GitRoutes.Stash, "req_7", JsonSerializer.Serialize(new { message = "wip" }));

        var toast = await _recorder.WaitForAsync("toast");
        Assert.False(string.IsNullOrEmpty(toast.GetProperty("type").GetString()));
    }

    /// <summary>Collects what the domain sent, with a poll for the background git calls.</summary>
    private sealed class Recorder
    {
        private readonly List<(string Action, JsonElement Payload)> _messages = new();
        private readonly object _gate = new();

        public void Send(string action, object payload)
        {
            var element = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
            lock (_gate) { _messages.Add((action, element)); }
        }

        public IReadOnlyList<(string Action, JsonElement Payload)> Messages
        {
            get { lock (_gate) { return _messages.ToArray(); } }
        }

        public async Task<JsonElement> WaitForAsync(string action, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < deadline)
            {
                var hit = Messages.LastOrDefault(m => m.Action == action);
                if (hit.Payload.ValueKind != JsonValueKind.Undefined) return hit.Payload;
                await Task.Delay(20);
            }
            throw new TimeoutException($"no '{action}' message was sent");
        }
    }
}
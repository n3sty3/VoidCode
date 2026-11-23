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

// /10: the session/window surface. The four chrome actions are asserted through
// The delegates the window supplies (that is the whole contract), and the checkpoint/inspect routes by
// Their replies. The storage-writing routes - `auth_operator`, `save_session`, `delete_session`,
// `save_recovery_state` - run under a `StorageService.UseRoot` scope, so this class proves they persist
// To the host's root without ever touching the developer's `%APPDATA%\VoidCode` profile. The class is in
// The "Storage root" collection because that override is process-wide.
[Collection("Storage root")]
public class SessionWindowHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly string _storageRoot;
    private readonly IDisposable _storage;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly SessionWindowHandlers _handlers;
    private readonly AppSettings _settings = new();
    private readonly List<string> _chromeCalls = new();
    private int _settingsChangedCount;
    private int _persistCount;

    public SessionWindowHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello\n");

        _storageRoot = Path.Combine(Path.GetTempPath(), "voidcode-session-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
        _storage = StorageService.UseRoot(_storageRoot);

        _handlers = new SessionWindowHandlers(
            () => _root,
            () => _settings,
            _ => _settingsChangedCount++,
            new SessionWindowHandlers.WindowChrome(
                () => _chromeCalls.Add("drag"),
                () => _chromeCalls.Add("close"),
                () => _chromeCalls.Add("ack"),
                () => _chromeCalls.Add("confirm")),
            _recorder.Send,
            _ => _persistCount++);
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        _storage.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_storageRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json = "{}")
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void TwelveRoutesAreRegistered()
    {
        var routes = _router.Actions.Count(action =>
            action is SessionWindowRoutes.WindowDrag or SessionWindowRoutes.WindowClose
                or SessionWindowRoutes.AuthOperator or SessionWindowRoutes.SaveSession
                or SessionWindowRoutes.SaveRecoveryState or SessionWindowRoutes.ClearRecoveryState
                or SessionWindowRoutes.DeleteSession or SessionWindowRoutes.CloseAck
                or SessionWindowRoutes.ConfirmClose or SessionWindowRoutes.GetCheckpointDiff
                or SessionWindowRoutes.RestoreCheckpoint or SessionWindowRoutes.InspectFile);

        Assert.Equal(12, routes);
    }

    [Fact]
    public async Task ChromeRoutes_InvokeTheirWindowCallbacks()
    {
        await Dispatch(SessionWindowRoutes.WindowDrag, "req_1");
        await Dispatch(SessionWindowRoutes.WindowClose, "req_2");
        await Dispatch(SessionWindowRoutes.CloseAck, "req_3");
        await Dispatch(SessionWindowRoutes.ConfirmClose, "req_4");

        Assert.Equal(new[] { "drag", "close", "ack", "confirm" }, _chromeCalls);
        Assert.Equal(0, _persistCount);
        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task InspectFile_ResolvesARelativePathAgainstTheWorkspace()
    {
        await Dispatch(SessionWindowRoutes.InspectFile, "req_5",
            JsonSerializer.Serialize(new { path = "notes.txt" }));

        var reply = await _recorder.WaitForAsync("inspect_file_response");
        Assert.Equal(Path.Combine(_root, "notes.txt"), reply.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.String, reply.GetProperty("report").ValueKind);
    }

    [Fact]
    public async Task InspectFile_RejectsAPayloadWithoutAPath()
    {
        var outcome = await Dispatch(SessionWindowRoutes.InspectFile, "req_6");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }
[Fact]
    public async Task GetCheckpointDiff_ForAnUnknownCheckpoint_SendsNothing()
    {
        await Dispatch(SessionWindowRoutes.GetCheckpointDiff, "req_7",
            JsonSerializer.Serialize(new { checkpointId = "cp_missing" }));

        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task RestoreCheckpoint_ForAnUnknownCheckpoint_Toasts()
    {
        await Dispatch(SessionWindowRoutes.RestoreCheckpoint, "req_8",
            JsonSerializer.Serialize(new { checkpointId = "cp_missing" }));

        var reply = await _recorder.WaitForAsync("toast");
        Assert.Equal("error", reply.GetProperty("type").GetString());
        Assert.Contains("Checkpoint no longer available", reply.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ClearRecoveryState_WithoutAJournal_SendsNothing()
    {
        await Dispatch(SessionWindowRoutes.ClearRecoveryState, "req_9");

        Assert.Empty(_recorder.Messages);
        Assert.Empty(_chromeCalls);
    }

    [Fact]
    public async Task AuthOperator_RegistersIntoTheHostRoot_AndPersistsTheOperator()
    {
        await Dispatch(SessionWindowRoutes.AuthOperator, "req_auth_1", JsonSerializer.Serialize(new
        {
            mode = "register",
            username = "Seam Operator",
            password = "correct horse battery",
            role = "Systems operator"
        }));

        var reply = await _recorder.WaitForAsync("auth_operator_response");
        Assert.True(reply.GetProperty("success").GetBoolean());
        Assert.Equal("Operator profile created.", reply.GetProperty("message").GetString());
        Assert.Equal("Seam Operator", reply.GetProperty("username").GetString());
        Assert.Equal("Systems operator", reply.GetProperty("role").GetString());

        // The profile really is on disk, under the injected root rather than the user's profile.
        Assert.True(File.Exists(Path.Combine(_storageRoot, "operators.json")));
        Assert.Equal("Seam Operator", _settings.ActiveOperator);
        Assert.Equal(1, _settingsChangedCount);
        Assert.Equal(1, _persistCount);

        // A wrong passphrase comes back as a failure and does not touch the settings again.
        await Dispatch(SessionWindowRoutes.AuthOperator, "req_auth_2", JsonSerializer.Serialize(new
        {
            mode = "login",
            username = "Seam Operator",
            password = "wrong"
        }));

        var refused = _recorder.Messages.Last(m => m.Action == "auth_operator_response").Payload;
        Assert.False(refused.GetProperty("success").GetBoolean());
        Assert.Contains("Passphrase does not match", refused.GetProperty("message").GetString());
        Assert.Equal(1, _settingsChangedCount);
        Assert.Equal(1, _persistCount);
    }

    [Fact]
    public async Task SaveThenDeleteSession_EditsOnlyTheRequestedTranscript()
    {
        await Dispatch(SessionWindowRoutes.SaveSession, "req_save_1", JsonSerializer.Serialize(new
        {
            session = new
            {
                id = "session_keep",
                title = "Keep me",
                updatedAt = "2026-02-01T10:00:00Z",
                workspace = _root,
                messages = new object[] { new { role = "user", content = "hello" } }
            }
        }));
        await Dispatch(SessionWindowRoutes.SaveSession, "req_save_2", JsonSerializer.Serialize(new
        {
            session = new
            {
                id = "session_drop",
                title = "Drop me",
                updatedAt = "2026-02-02T10:00:00Z",
                workspace = _root,
                messages = new object[0]
            }
        }));

        Assert.True(File.Exists(Path.Combine(_storageRoot, "sessions.json")));
        Assert.Equal(2, StorageService.LoadSessions().Count);

        await Dispatch(SessionWindowRoutes.DeleteSession, "req_del_1",
            JsonSerializer.Serialize(new { sessionId = "session_drop" }));

        var remaining = Assert.Single(StorageService.LoadSessions());
        Assert.Equal("session_keep", remaining.Id);
        Assert.Single(remaining.Messages);
        Assert.Equal("Keep me", remaining.Title);

        // An empty payload deletes nothing, which is what the page relies on.
        await Dispatch(SessionWindowRoutes.DeleteSession, "req_del_2");
        Assert.Single(StorageService.LoadSessions());
    }

    [Fact]
    public async Task SaveRecoveryState_JournalsTheHostWorkspace_AndIgnoresAStaleOne()
    {
        await Dispatch(SessionWindowRoutes.SaveRecoveryState, "req_rec_1", JsonSerializer.Serialize(new
        {
            state = new
            {
                workspace = _root,
                activePath = Path.Combine(_root, "notes.txt"),
                buffers = new object[]
                {
                    new { path = Path.Combine(_root, "notes.txt"), content = "draft", cursorLine = 2, cursorColumn = 3 }
                }
            }
        }));

        Assert.Single(Directory.GetFiles(Path.Combine(_storageRoot, "recovery"), "*.json"));
        var journal = StorageService.LoadRecoveryState(_root);
        Assert.NotNull(journal);
        Assert.Equal("draft", Assert.Single(journal!.Buffers).Content);

        // A tab that still points at another workspace must not overwrite this workspace's journal.
        await Dispatch(SessionWindowRoutes.SaveRecoveryState, "req_rec_2", JsonSerializer.Serialize(new
        {
            state = new
            {
                workspace = Path.Combine(Path.GetTempPath(), "voidcode-somewhere-else"),
                buffers = new object[]
                {
                    new { path = "other.txt", content = "stale", cursorLine = 1, cursorColumn = 1 }
                }
            }
        }));

        Assert.Equal("draft", Assert.Single(StorageService.LoadRecoveryState(_root)!.Buffers).Content);
        Assert.Equal(0, _persistCount);
        Assert.Empty(_recorder.Messages);
    }

    [Theory]
    [InlineData(SessionWindowRoutes.SaveSession)]
    [InlineData(SessionWindowRoutes.SaveRecoveryState)]
    public async Task StorageRoutesWithARequiredPayload_RejectAnEmptyOne(string action)
    {
        var outcome = await Dispatch(action, "req_10");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }

    /// <summary>Collects what the domain sent.</summary>
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
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
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
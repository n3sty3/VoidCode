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

// /10: settings, the agent turn and inline assist. Two seams make this testable
// Without a network or a real profile: the settings object is supplied by the test (so `save_settings`
// Can be exercised without touching %APPDATA%), and the agent settings carry an empty BaseUrl/Model,
// Which makes AgentEngine answer "no model configured" immediately instead of calling out. The two
// Routes that must not run here are `test_connection` (a real HTTP probe) and `open_logs_folder` (it
// Would launch Explorer); both are covered by the UI harness.
public class SettingsAgentHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly SettingsAgentHandlers _handlers;
    private readonly List<string> _themes = new();
    private readonly List<AppSettings> _persisted = new();
    private AppSettings _settings;

    public SettingsAgentHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _settings = new AppSettings
        {
            BaseUrl = "",
            Model = "",
            ApiKey = "stored-key",
            Theme = "harbor",
            AgentMode = "act",
            RecentWorkspaces = new List<string> { @"C:\recent" }
        };

        _handlers = new SettingsAgentHandlers(
            () => _root,
            () => _settings,
            updated => _settings = updated,
            theme => _themes.Add(theme ?? ""),
            _recorder.Send,
            updated => _persisted.Add(updated));
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        _handlers.CancelPending();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json = "{}")
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void NineRoutesAreRegistered()
    {
        var routes = _router.Actions.Count(action =>
            action is SettingsAgentRoutes.TestConnection or SettingsAgentRoutes.SaveSettings
                or SettingsAgentRoutes.SetThemeIcon or SettingsAgentRoutes.StartAgent
                or SettingsAgentRoutes.CancelAgent or SettingsAgentRoutes.ResolveToolApproval
                or SettingsAgentRoutes.InlineAssistStart or SettingsAgentRoutes.InlineAssistCancel
                or SettingsAgentRoutes.OpenLogsFolder);

        Assert.Equal(9, routes);
    }

    [Fact]
    public async Task SetThemeIcon_WithoutATheme_AppliesTheDefault()
    {
        await Dispatch(SettingsAgentRoutes.SetThemeIcon, "req_1");

        Assert.Equal(new[] { "harbor" }, _themes);
    }

    [Fact]
    public async Task SetThemeIcon_AppliesTheSentTheme()
    {
        await Dispatch(SettingsAgentRoutes.SetThemeIcon, "req_2", JsonSerializer.Serialize(new { theme = "crimson" }));

        Assert.Equal(new[] { "crimson" }, _themes);
    }

    [Fact]
    public async Task SaveSettings_KeepsTheStoredKeyWorkspaceAndRecentList()
    {
        var draft = new
        {
            Model = "deepseek/deepseek-chat",
            Theme = "nebula",
            BaseUrl = "https://example.invalid/api",
            RecentWorkspaces = Array.Empty<string>()
        };

        await Dispatch(SettingsAgentRoutes.SaveSettings, "req_3", JsonSerializer.Serialize(new { settings = draft }));

        var saved = Assert.Single(_persisted);
        Assert.Equal("deepseek/deepseek-chat", saved.Model);
        Assert.Equal("nebula", saved.Theme);
        Assert.Equal("stored-key", saved.ApiKey);
        Assert.Equal(_root, saved.LastWorkspace);
        Assert.Equal(new List<string> { @"C:\recent" }, saved.RecentWorkspaces);
        Assert.Same(saved, _settings);

        var reply = await _recorder.WaitForAsync("settings_saved");
        Assert.Equal("deepseek/deepseek-chat", reply.GetProperty("settings").GetProperty("Model").GetString());
        Assert.Equal(new[] { "nebula" }, _themes);
    }

    [Fact]
    public async Task SaveSettings_WithASubmittedKey_StoresTheNewKey()
    {
        await Dispatch(SettingsAgentRoutes.SaveSettings, "req_4",
            JsonSerializer.Serialize(new { settings = new { Model = "m" }, apiKey = "fresh-key" }));

        Assert.Equal("fresh-key", Assert.Single(_persisted).ApiKey);
    }

    [Fact]
    public async Task SaveSettings_WithoutASettingsObject_ChangesNothing()
    {
        await Dispatch(SettingsAgentRoutes.SaveSettings, "req_5");

        Assert.Empty(_persisted);
        Assert.Empty(_themes);
        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task StartAgent_WithoutAModel_ReportsAnErrorAndRefreshesTheTree()
    {
        await Dispatch(SettingsAgentRoutes.StartAgent, "req_6", JsonSerializer.Serialize(new { prompt = "hello" }));

        var error = await _recorder.WaitForAsync("agent_error");
        Assert.Contains("No model is configured", error.GetProperty("message").GetString());
        await _recorder.WaitForAsync("tree_refreshed");
    }

    [Fact]
    public async Task InlineAssistStart_WithNoProvider_ReportsAnError()
    {
        await Dispatch(SettingsAgentRoutes.InlineAssistStart, "req_7",
            JsonSerializer.Serialize(new { instruction = "rename this", selectedCode = "var a = 1;" }));

        var error = await _recorder.WaitForAsync("inline_assist_error");
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task CancelRoutes_AnswerTheirOwnShapes()
    {
        await Dispatch(SettingsAgentRoutes.CancelAgent, "req_8");
        await Dispatch(SettingsAgentRoutes.InlineAssistCancel, "req_9");

        Assert.Equal("Cancelled.", (await _recorder.WaitForAsync("agent_done")).GetProperty("finalMessage").GetString());
        await _recorder.WaitForAsync("inline_assist_cancelled");
    }

    [Fact]
    public async Task ResolveToolApproval_ForAnUnknownToolCall_IsQuiet()
    {
        await Dispatch(SettingsAgentRoutes.ResolveToolApproval, "req_10",
            JsonSerializer.Serialize(new { id = "call_404", approved = true }));

        Assert.Empty(_recorder.Messages);
    }

    [Theory]
    [InlineData(SettingsAgentRoutes.StartAgent)]
    [InlineData(SettingsAgentRoutes.InlineAssistStart)]
    [InlineData(SettingsAgentRoutes.ResolveToolApproval)]
    public async Task RoutesWithARequiredField_RejectAnEmptyPayload(string action)
    {
        var outcome = await Dispatch(action, "req_11");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }

    /// <summary>Collects what the domain sent, with a poll for the asynchronous routes.</summary>
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
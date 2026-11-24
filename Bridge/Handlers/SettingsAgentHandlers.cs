using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// Settings, the agent run, inline assist and the two small shell
/// Actions - nine cases.
///
/// <c>test_connection</c>, <c>save_settings</c>, <c>set_theme_icon</c>, <c>start_agent</c>,
/// <c>cancel_agent</c>, <c>resolve_tool_approval</c>, <c>inline_assist_start</c>,
/// <c>inline_assist_cancel</c> and <c>open_logs_folder</c>.
///
/// The domain owns the two cancellation token sources managed for this domain (<c>_agentCts</c> and
/// <c>_inlineAssistCts</c>); the window's close path calls <see cref="CancelPending"/> instead of poking
/// Them. It does <em>not</em> own the settings object: the window still reads <c>_settings</c> itself
/// (the app title, the workspace, the operator), so it passes a getter and a setter, and the theme icon
/// Stays a window concern behind the <c>applyTheme</c> callback.
///
/// The replies keep their shapes - <c>test_connection_response</c>, <c>settings_saved</c> - while the
/// Agent and inline-assist streams come from <see cref="AgentEngine"/>'s <c>emitEvent</c> callback, so
/// Every route here is <c>envelope: false</c>.
/// </summary>
public sealed class SettingsAgentHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _settingsChanged;
    private readonly Action<string?> _applyTheme;
    private readonly Action<string, object> _send;
    private readonly Action<AppSettings> _persist;
    private CancellationTokenSource? _agentCts;
    private CancellationTokenSource? _inlineAssistCts;
    private CancellationTokenSource? _autocompleteCts;

    public SettingsAgentHandlers(
        Func<string?> workspace,
        Func<AppSettings> settings,
        Action<AppSettings> settingsChanged,
        Action<string?> applyTheme,
        Action<string, object> send,
        Action<AppSettings>? persist = null)
    {
        _workspace = workspace;
        _settings = settings;
        _settingsChanged = settingsChanged;
        _applyTheme = applyTheme;
        _send = send;
        // The saver is a seam, not an option: `SettingsService.Save` writes to %APPDATA% and stores the
        // Key with DPAPI, so a test that exercises `save_settings` must not reach it.
        _persist = persist ?? SettingsService.Save;
    }

    /// <summary>Cancels a running agent turn and a running inline assist (window close).</summary>
    public void CancelPending()
    {
        _agentCts?.Cancel();
        _inlineAssistCts?.Cancel();
        _autocompleteCts?.Cancel();
    }

    /// <summary>An action that carries nothing the host needs.</summary>
    public sealed class NoPayload
    {
    }

    public sealed class ProviderProbeRequest
    {
        /// <summary>Optional: an unsaved settings draft, tested instead of the stored ones.</summary>
        public JsonObject? Settings { get; init; }

        /// <summary>Optional: a key the page just typed, not yet persisted.</summary>
        public string? ApiKey { get; init; }

        /// <summary>The page's "forget the stored key" checkbox.</summary>
        public bool ClearApiKey { get; init; }
    }

    public sealed class SaveSettingsRequest
    {
        /// <summary>Optional: without it the page sent nothing to save and the route is a no-op.</summary>
        public JsonObject? Settings { get; init; }

        public string? ApiKey { get; init; }
        public bool ClearApiKey { get; init; }
    }

    public sealed class SetThemeIconRequest
    {
        /// <summary>Optional: an unknown or missing theme falls back to the default icon.</summary>
        public string? Theme { get; init; }
    }

    public sealed class StartAgentRequest
    {
        /// <summary>Required: an agent turn without a prompt is a page bug.</summary>
        public required string Prompt { get; init; }

        /// <summary>Optional: the transcript the page re-sends so a reload can continue a conversation.</summary>
        public JsonArray? History { get; init; }

        /// <summary>Optional: the editor context (selection, open file) the tools may read.</summary>
        public JsonObject? Context { get; init; }

        /// <summary>Optional: a one-run mode override (Review/Edit/Agent).</summary>
        public string? Mode { get; init; }
    }

    public sealed class ResolveToolApprovalRequest
    {
        /// <summary>Required: the tool call id the page is answering.</summary>
        public required string Id { get; init; }

        public bool Approved { get; init; }
    }

    public sealed class InlineAssistStartRequest
    {
        /// <summary>Required: the user's instruction for the selection.</summary>
        public required string Instruction { get; init; }

        public string? SelectedCode { get; init; }
        public string? FileContent { get; init; }
        public string? RelativePath { get; init; }
        public string? Language { get; init; }
    }

    public sealed class TurnIdRequest
    {
        public required string TurnId { get; init; }
    }

    public sealed class TurnFileRequest
    {
        public required string TurnId { get; init; }
        public required string Path { get; init; }
    }

    public sealed class RejectTurnHunkRequest
    {
        public required string TurnId { get; init; }
        public required string Path { get; init; }
        public int HunkIndex { get; init; }
    }

    public sealed class InspectAgentContextRequest
    {
        public JsonObject? Context { get; init; }
        public JsonArray? History { get; init; }
        public JsonArray? Attachments { get; init; }
        public string? PromptDraft { get; init; }
        public string? Mode { get; init; }
    }

    public sealed class InstallLspServerRequest
    {
        public required string Language { get; init; }
    }

    public sealed class CheckForUpdatesRequest
    {
        public string? Repo { get; init; }
        public bool Silent { get; init; }
    }

    public void Register(BridgeRouter router)
    {
        router.Register<ProviderProbeRequest, object?>(SettingsAgentRoutes.TestConnection, TestConnectionAsync, envelope: false);
        router.Register<SaveSettingsRequest, object?>(SettingsAgentRoutes.SaveSettings, SaveSettingsAsync, envelope: false);
        router.Register<SetThemeIconRequest, object?>(SettingsAgentRoutes.SetThemeIcon, SetThemeIconAsync, envelope: false);
        router.Register<StartAgentRequest, object?>(SettingsAgentRoutes.StartAgent, StartAgentAsync, envelope: false);
        router.Register<NoPayload, object?>(SettingsAgentRoutes.CancelAgent, CancelAgentAsync, envelope: false);
        router.Register<ResolveToolApprovalRequest, object?>(SettingsAgentRoutes.ResolveToolApproval, ResolveToolApprovalAsync, envelope: false);
        router.Register<InlineAssistStartRequest, object?>(SettingsAgentRoutes.InlineAssistStart, InlineAssistStartAsync, envelope: false);
        router.Register<NoPayload, object?>(SettingsAgentRoutes.InlineAssistCancel, InlineAssistCancelAsync, envelope: false);
        router.Register<NoPayload, object?>(SettingsAgentRoutes.OpenLogsFolder, OpenLogsFolderAsync, envelope: false);
        router.Register<TurnIdRequest, object?>(SettingsAgentRoutes.GetTurnSummary, GetTurnSummaryAsync, envelope: false);
        router.Register<TurnFileRequest, object?>(SettingsAgentRoutes.GetTurnFileDiff, GetTurnFileDiffAsync, envelope: false);
        router.Register<TurnFileRequest, object?>(SettingsAgentRoutes.RevertTurnFile, RevertTurnFileAsync, envelope: false);
        router.Register<RejectTurnHunkRequest, object?>(SettingsAgentRoutes.RejectTurnHunk, RejectTurnHunkAsync, envelope: false);
        router.Register<TurnIdRequest, object?>(SettingsAgentRoutes.RevertWholeTurn, RevertWholeTurnAsync, envelope: false);
        router.Register<InspectAgentContextRequest, object?>(SettingsAgentRoutes.InspectAgentContext, InspectAgentContextAsync, envelope: false);
        router.Register<NoPayload, object?>(SettingsAgentRoutes.GetCapabilities, GetCapabilitiesAsync, envelope: false);
        router.Register<InstallLspServerRequest, object?>(SettingsAgentRoutes.InstallLspServer, InstallLspServerAsync, envelope: false);
        router.Register<CheckForUpdatesRequest, object?>(SettingsAgentRoutes.CheckForUpdates, CheckForUpdatesAsync, envelope: false);
        router.Register<InlineCompleteRequest, InlineCompleteResponse>(SettingsAgentRoutes.AiInlineComplete, AiInlineCompleteAsync, envelope: true);
        router.Register<NoPayload, LocalDiscoveryReport>(SettingsAgentRoutes.DiscoverLocalModels, DiscoverLocalModelsAsync, envelope: true);
    }

    private Task<object?> TestConnectionAsync(ProviderProbeRequest request, BridgeContext context)
    {
        var current = _settings();
        var testSettings = request.Settings != null
            ? JsonSerializer.Deserialize<AppSettings>(request.Settings.ToJsonString()) ?? current
            : current;
        var submittedApiKey = request.ApiKey ?? "";
        testSettings.ApiKey = request.ClearApiKey
            ? ""
            : (!string.IsNullOrWhiteSpace(submittedApiKey) ? submittedApiKey : current.ApiKey);

        _ = Task.Run(async () =>
        {
            var (success, message, models) = await AgentEngine.TestConnectionAsync(testSettings);
            _send("test_connection_response", new { success, message, models });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> SaveSettingsAsync(SaveSettingsRequest request, BridgeContext context)
    {
        if (request.Settings == null) return Task.FromResult<object?>(null);

        var updated = JsonSerializer.Deserialize<AppSettings>(request.Settings.ToJsonString());
        if (updated == null) return Task.FromResult<object?>(null);

        var current = _settings();
        var submittedApiKey = request.ApiKey ?? "";
        if (request.ClearApiKey)
        {
            SecretService.ClearApiKey();
            updated.ApiKey = "";
        }
        else
        {
            updated.ApiKey = !string.IsNullOrWhiteSpace(submittedApiKey) ? submittedApiKey : current.ApiKey;
        }

        // The page does not send these two: the workspace is the host's, and an empty recent list means
        // "unchanged", not "forget them".
        updated.LastWorkspace = _workspace();
        if (updated.RecentWorkspaces == null || updated.RecentWorkspaces.Count == 0)
        {
            updated.RecentWorkspaces = current.RecentWorkspaces;
        }

        _settingsChanged(updated);
        _persist(updated);
        _applyTheme(updated.Theme);
        _send("settings_saved", new { settings = updated });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> SetThemeIconAsync(SetThemeIconRequest request, BridgeContext context)
    {
        _applyTheme(request.Theme ?? "harbor");
        return Task.FromResult<object?>(null);
    }

    private Task<object?> StartAgentAsync(StartAgentRequest request, BridgeContext context)
    {
        var prompt = request.Prompt;
        var modeOverride = request.Mode;
        var editorContext = request.Context;

        var history = new List<JsonObject>();
        foreach (var item in request.History ?? new JsonArray())
        {
            if (item is JsonObject obj)
            {
                var cloned = JsonNode.Parse(obj.ToJsonString())?.AsObject();
                if (cloned != null) history.Add(cloned);
            }
        }

        // A new turn replaces the previous one. The old code cancelled the field and read `_agentCts.Token`
        // Inside the worker, so a fast second turn could hand the first worker the *new* token; the token
        // Is captured here instead, one per run.
        var next = new CancellationTokenSource();
        Interlocked.Exchange(ref _agentCts, next)?.Cancel();
        var token = next.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var current = _settings();
                if (string.IsNullOrWhiteSpace(current.ApiKey) &&
                    !current.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
                    !current.BaseUrl.Contains("127.0.0.1"))
                {
                    LoggingService.LogWarning("Agent run started with no API key configured.", nameof(SettingsAgentHandlers));
                }
                LoggingService.LogInfo($"Agent run start: model={current.Model} url={current.BaseUrl} mode={modeOverride ?? current.AgentMode}", nameof(SettingsAgentHandlers));

                await AgentEngine.RunAgentLoopAsync(
                    prompt,
                    history,
                    _workspace() ?? Environment.CurrentDirectory,
                    current,
                    (evt, payload) => _send(evt, payload),
                    token,
                    editorContext,
                    modeOverride
                );
            }
            catch (OperationCanceledException)
            {
                // The frontend already reset the UI when it sent cancel_agent.
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Agent run failed: {ex.Message}", ex, nameof(SettingsAgentHandlers));
                _send("agent_error", new { message = $"Agent run failed: {ex.Message}" });
            }
            finally
            {
                var workspace = _workspace();
                if (!string.IsNullOrEmpty(workspace) && Directory.Exists(workspace))
                {
                    try
                    {
                        var tree = FileSystemService.GetDirectoryTree(workspace);
                        _send("tree_refreshed", new { tree });
                    }
                    catch (Exception ex) { LoggingService.Swallowed(ex, "SettingsAgentHandlers.AgentRunTreeRefresh"); }
                }
            }
        });

        return Task.FromResult<object?>(null);
    }

    private Task<object?> CancelAgentAsync(NoPayload request, BridgeContext context)
    {
        _agentCts?.Cancel();
        TerminalService.KillActiveProcess();
        _send("agent_done", new { finalMessage = "Cancelled." });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ResolveToolApprovalAsync(ResolveToolApprovalRequest request, BridgeContext context)
    {
        AgentEngine.ResolveToolApproval(request.Id, request.Approved);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> InlineAssistStartAsync(InlineAssistStartRequest request, BridgeContext context)
    {
        var next = new CancellationTokenSource();
        Interlocked.Exchange(ref _inlineAssistCts, next)?.Cancel();
        var token = next.Token;

        var current = _settings();
        _ = Task.Run(async () =>
        {
            await AgentEngine.RunInlineAssistAsync(
                request.Instruction,
                request.SelectedCode ?? "",
                request.FileContent ?? "",
                request.RelativePath ?? "",
                request.Language ?? "",
                current,
                (evt, payload) => _send(evt, payload),
                token
            );
        });

        return Task.FromResult<object?>(null);
    }

    private Task<object?> InlineAssistCancelAsync(NoPayload request, BridgeContext context)
    {
        _inlineAssistCts?.Cancel();
        _send("inline_assist_cancelled", new { });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> OpenLogsFolderAsync(NoPayload request, BridgeContext context)
    {
        LoggingService.OpenLogFolder();
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetTurnSummaryAsync(TurnIdRequest request, BridgeContext context)
    {
        var tx = TurnTransactionLedger.GetTurn(request.TurnId);
        if (tx == null)
        {
            _send("turn_summary_response", new { ok = false, turnId = request.TurnId, error = "Turn snapshot not found." });
            return Task.FromResult<object?>(null);
        }
        _send("turn_summary_response", tx.ToSummaryPayload());
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetTurnFileDiffAsync(TurnFileRequest request, BridgeContext context)
    {
        var tx = TurnTransactionLedger.GetTurn(request.TurnId);
        var diff = tx?.GetFileDiff(request.Path);
        if (diff == null)
        {
            _send("toast", new { type = "error", message = "Turn diff snapshot not found." });
            return Task.FromResult<object?>(null);
        }
        _send("turn_file_diff_response", diff);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RevertTurnFileAsync(TurnFileRequest request, BridgeContext context)
    {
        var tx = TurnTransactionLedger.GetTurn(request.TurnId);
        if (tx == null)
        {
            _send("toast", new { type = "error", message = "Turn snapshot not found." });
            return Task.FromResult<object?>(null);
        }

        var (ok, existsNow, content, error) = tx.RevertFile(request.Path);
        if (!ok)
        {
            _send("toast", new { type = "error", message = error ?? "Failed to revert file." });
            return Task.FromResult<object?>(null);
        }

        RefreshWorkspaceTreeIfPresent();
        _send("turn_file_reverted", new
        {
            turnId = request.TurnId,
            path = request.Path,
            exists = existsNow,
            content
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RejectTurnHunkAsync(RejectTurnHunkRequest request, BridgeContext context)
    {
        var tx = TurnTransactionLedger.GetTurn(request.TurnId);
        if (tx == null)
        {
            _send("toast", new { type = "error", message = "Turn snapshot not found." });
            return Task.FromResult<object?>(null);
        }

        var (ok, updatedDiff, error) = tx.RejectHunk(request.Path, request.HunkIndex);
        if (!ok || updatedDiff == null)
        {
            _send("toast", new { type = "error", message = error ?? "Failed to reject hunk." });
            return Task.FromResult<object?>(null);
        }

        RefreshWorkspaceTreeIfPresent();
        _send("turn_hunk_rejected", updatedDiff);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RevertWholeTurnAsync(TurnIdRequest request, BridgeContext context)
    {
        var tx = TurnTransactionLedger.GetTurn(request.TurnId);
        if (tx == null)
        {
            _send("toast", new { type = "error", message = "Turn snapshot not found." });
            return Task.FromResult<object?>(null);
        }

        var result = tx.RevertWholeTurn();
        RefreshWorkspaceTreeIfPresent();
        var updatedContents = new List<object>();
        foreach (var path in result.RevertedFiles)
        {
            var exists = File.Exists(path);
            updatedContents.Add(new
            {
                path,
                exists,
                content = exists && !FileSystemService.IsBinaryFile(path) ? File.ReadAllText(path) : null
            });
        }

        _send("turn_reverted", new
        {
            turnId = result.TurnId,
            revertedFiles = result.RevertedFiles,
            failedFiles = result.FailedFiles,
            irreversibleActions = result.IrreversibleActions,
            files = updatedContents
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> InspectAgentContextAsync(InspectAgentContextRequest request, BridgeContext context)
    {
        var workspace = _workspace() ?? Environment.CurrentDirectory;
        var snapshot = ContextInspector.Inspect(
            workspace,
            _settings(),
            request.Context,
            request.History,
            request.Attachments,
            request.PromptDraft,
            request.Mode);
        _send("agent_context_response", snapshot);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetCapabilitiesAsync(NoPayload request, BridgeContext context)
    {
        var report = CapabilitiesService.Inspect(_workspace(), _settings());
        _send("capabilities_response", report);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> InstallLspServerAsync(InstallLspServerRequest request, BridgeContext context)
    {
        var language = request.Language;
        _ = Task.Run(async () =>
        {
            var result = await LspInstallService.InstallAsync(
                language,
                line => _send("lsp_install_progress", new { language, line }));
            _send("lsp_install_finished", result);
            if (result.Success)
            {
                var updated = CapabilitiesService.Inspect(_workspace(), _settings());
                _send("capabilities_response", updated);
            }
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> CheckForUpdatesAsync(CheckForUpdatesRequest request, BridgeContext context)
    {
        var repo = !string.IsNullOrWhiteSpace(request.Repo) ? request.Repo : _settings().UpdateRepo;
        var silent = request.Silent;
        _ = Task.Run(async () =>
        {
            var result = await UpdateCheckService.CheckForUpdatesAsync(repo, silent);
            _send("update_check_response", result);
        });
        return Task.FromResult<object?>(null);
    }

    private async Task<InlineCompleteResponse> AiInlineCompleteAsync(InlineCompleteRequest request, BridgeContext context)
    {
        _autocompleteCts?.Cancel();
        _autocompleteCts?.Dispose();
        _autocompleteCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = _autocompleteCts.Token;
        return await AutocompleteService.CompleteAsync(request, _settings(), ct);
    }

    private Task<LocalDiscoveryReport> DiscoverLocalModelsAsync(NoPayload request, BridgeContext context)
    {
        return LocalModelDiscoveryService.DiscoverAsync();
    }

    private void RefreshWorkspaceTreeIfPresent()
    {
        var workspace = _workspace();
        if (!string.IsNullOrEmpty(workspace) && Directory.Exists(workspace))
        {
            try
            {
                var tree = FileSystemService.GetDirectoryTree(workspace);
                _send("tree_refreshed", new { tree });
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "SettingsAgentHandlers.RefreshWorkspaceTree"); }
        }
    }
}

/// <summary>The action names of the settings/agent domain.</summary>
public static class SettingsAgentRoutes
{
    public const string TestConnection = "test_connection";
    public const string SaveSettings = "save_settings";
    public const string SetThemeIcon = "set_theme_icon";
    public const string StartAgent = "start_agent";
    public const string CancelAgent = "cancel_agent";
    public const string ResolveToolApproval = "resolve_tool_approval";
    public const string InlineAssistStart = "inline_assist_start";
    public const string InlineAssistCancel = "inline_assist_cancel";
    public const string OpenLogsFolder = "open_logs_folder";
    public const string GetTurnSummary = "get_turn_summary";
    public const string GetTurnFileDiff = "get_turn_file_diff";
    public const string RevertTurnFile = "revert_turn_file";
    public const string RejectTurnHunk = "reject_turn_hunk";
    public const string RevertWholeTurn = "revert_whole_turn";
    public const string InspectAgentContext = "inspect_agent_context";
    public const string GetCapabilities = "get_capabilities";
    public const string InstallLspServer = "install_lsp_server";
    public const string CheckForUpdates = "check_for_updates";
    public const string AiInlineComplete = "ai_inline_complete";
    public const string DiscoverLocalModels = "discover_local_models";
}
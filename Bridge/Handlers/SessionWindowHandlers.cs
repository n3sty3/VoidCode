using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// The operator/session surface, checkpoints and window chrome - the
/// Last twelve cases of the switch.
///
/// <c>window_drag</c>, <c>window_close</c>, <c>auth_operator</c>, <c>save_session</c>,
/// <c>save_recovery_state</c>, <c>clear_recovery_state</c>, <c>delete_session</c>, <c>close_ack</c>,
/// <c>confirm_close</c>, <c>get_checkpoint_diff</c>, <c>restore_checkpoint</c> and <c>inspect_file</c>.
///
/// Four of them cannot leave the window - dragging needs the HWND, closing needs <c>Close()</c>, and the
/// Two close-handshake acknowledgements flip the window's own flags - so the window hands them over as
/// A <see cref="WindowChrome"/> bundle instead of the domain reaching back into it.
///
/// The rest are pass-through to <see cref="StorageService"/> and <see cref="FileSystemService"/>, and
/// They keep their legacy replies (<c>auth_operator_response</c>, <c>checkpoint_diff_response</c>,
/// <c>checkpoint_restored</c>, <c>tree_refreshed</c>, the "Checkpoint no longer available" toast,
/// <c>inspect_file_response</c>); none of them answers an envelope. Only <c>inspect_file</c> and the two
/// JSON payloads are `required`, because they are the ones where a missing value would have reached a
/// Service with an empty string; the operator fields stay optional so the existing friendly
/// "Operator handle is required." answer survives instead of turning into an invisible
/// `invalid_payload`.
/// </summary>
public sealed class SessionWindowHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _settingsChanged;
    private readonly WindowChrome _chrome;
    private readonly Action<string, object> _send;
    private readonly Action<AppSettings> _persist;
    private readonly IFileSystem _files;

    public SessionWindowHandlers(
        Func<string?> workspace,
        Func<AppSettings> settings,
        Action<AppSettings> settingsChanged,
        WindowChrome chrome,
        Action<string, object> send,
        Action<AppSettings>? persist = null,
        IFileSystem? files = null)
    {
        _workspace = workspace;
        _settings = settings;
        _settingsChanged = settingsChanged;
        _chrome = chrome;
        _send = send;
        // Same seam as the settings domain: `SettingsService.Save` writes to %APPDATA%, so a test must
        // Be able to replace it.
        _persist = persist ?? SettingsService.Save;
        // Checkpoints and binary inspection through the same filesystem seam the
        // File-system domain uses, so the restore paths can be tested without a real checkpoint store.
        _files = files ?? new SystemFileSystem();
    }

    /// <summary>The four actions only the window can perform.</summary>
    public sealed record WindowChrome(
        Action Drag,
        Action Close,
        Action AcknowledgeClose,
        Action ConfirmClose);

    /// <summary>An action that carries nothing the host needs (the close handshake, window chrome).</summary>
    public sealed class NoPayload
    {
    }

    public sealed class AuthOperatorRequest
    {
        /// <summary>"register" or "login"; the service defaults an unknown mode to login.</summary>
        public string? Mode { get; init; }

        public string? Username { get; init; }
        public string? Password { get; init; }
        public string? Role { get; init; }
    }

    public sealed class SaveSessionRequest
    {
        /// <summary>Required: the transcript to persist (it also carries the id).</summary>
        public required JsonObject Session { get; init; }
    }

    public sealed class SaveRecoveryRequest
    {
        /// <summary>Required: the recovery journal (workspace + buffers).</summary>
        public required JsonObject State { get; init; }
    }

    public sealed class DeleteSessionRequest
    {
        /// <summary>Optional: an empty id deletes nothing, which is what the page expects.</summary>
        public string? SessionId { get; init; }
    }

    public sealed class CheckpointRequest
    {
        /// <summary>Optional: an unknown or empty id finds nothing.</summary>
        public string? CheckpointId { get; init; }
    }

    public sealed class InspectFileRequest
    {
        /// <summary>Required: a relative path is resolved against the workspace.</summary>
        public required string Path { get; init; }
    }

    public void Register(BridgeRouter router)
    {
        router.Register<NoPayload, object?>(SessionWindowRoutes.WindowDrag, (_, _) => Invoke(_chrome.Drag), envelope: false);
        router.Register<NoPayload, object?>(SessionWindowRoutes.WindowClose, (_, _) => Invoke(_chrome.Close), envelope: false);
        router.Register<AuthOperatorRequest, object?>(SessionWindowRoutes.AuthOperator, AuthOperatorAsync, envelope: false);
        router.Register<SaveSessionRequest, object?>(SessionWindowRoutes.SaveSession, SaveSessionAsync, envelope: false);
        router.Register<SaveRecoveryRequest, object?>(SessionWindowRoutes.SaveRecoveryState, SaveRecoveryStateAsync, envelope: false);
        router.Register<NoPayload, object?>(SessionWindowRoutes.ClearRecoveryState, (_, _) => ClearRecoveryStateAsync(), envelope: false);
        router.Register<DeleteSessionRequest, object?>(SessionWindowRoutes.DeleteSession, DeleteSessionAsync, envelope: false);
        router.Register<NoPayload, object?>(SessionWindowRoutes.CloseAck, (_, _) => Invoke(_chrome.AcknowledgeClose), envelope: false);
        router.Register<NoPayload, object?>(SessionWindowRoutes.ConfirmClose, (_, _) => Invoke(_chrome.ConfirmClose), envelope: false);
        router.Register<CheckpointRequest, object?>(SessionWindowRoutes.GetCheckpointDiff, GetCheckpointDiffAsync, envelope: false);
        router.Register<CheckpointRequest, object?>(SessionWindowRoutes.RestoreCheckpoint, RestoreCheckpointAsync, envelope: false);
        router.Register<InspectFileRequest, object?>(SessionWindowRoutes.InspectFile, InspectFileAsync, envelope: false);
    }

    /// <summary>Runs a synchronous action with the same result shape as the other routes.</summary>
    private static Task<object?> Invoke(Action action)
    {
        action();
        return Task.FromResult<object?>(null);
    }

    private Task<object?> AuthOperatorAsync(AuthOperatorRequest request, BridgeContext context)
    {
        var (success, message, resolvedUser, resolvedRole) = StorageService.AuthenticateOrRegister(
            request.Mode ?? "login",
            request.Username ?? "",
            request.Password ?? "",
            request.Role ?? "Systems operator");

        if (success)
        {
            var current = _settings();
            current.ActiveOperator = resolvedUser;
            _settingsChanged(current);
            _persist(current);
        }

        _send("auth_operator_response", new
        {
            success,
            message,
            username = resolvedUser,
            role = resolvedRole
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> SaveSessionAsync(SaveSessionRequest request, BridgeContext context)
    {
        var session = JsonSerializer.Deserialize<SavedChatSession>(request.Session.ToJsonString());
        if (session != null && !string.IsNullOrEmpty(session.Id))
        {
            StorageService.SaveSession(session);
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> SaveRecoveryStateAsync(SaveRecoveryRequest request, BridgeContext context)
    {
        var state = JsonSerializer.Deserialize<WorkspaceRecoveryState>(request.State.ToJsonString());
        var workspace = _workspace();
        if (state == null || string.IsNullOrEmpty(workspace)) return Task.FromResult<object?>(null);

        // A journal for another workspace is a stale tab: it must not overwrite this workspace's file.
        if (string.Equals(Path.GetFullPath(state.Workspace), Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase))
        {
            StorageService.SaveRecoveryState(state);
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ClearRecoveryStateAsync()
    {
        StorageService.ClearRecoveryState(_workspace());
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DeleteSessionAsync(DeleteSessionRequest request, BridgeContext context)
    {
        var sessionId = request.SessionId ?? "";
        if (!string.IsNullOrEmpty(sessionId))
        {
            StorageService.DeleteSession(sessionId);
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetCheckpointDiffAsync(CheckpointRequest request, BridgeContext context)
    {
        var checkpointId = request.CheckpointId ?? "";
        var checkpoint = _files.GetCheckpoint(checkpointId);
        if (checkpoint != null)
        {
            var afterContent = _files.FileExists(checkpoint.FilePath) ? _files.ReadAllText(checkpoint.FilePath) : "";
            _send("checkpoint_diff_response", new
            {
                checkpointId,
                path = checkpoint.FilePath,
                beforeContent = checkpoint.PreviousContent ?? "",
                afterContent
            });
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RestoreCheckpointAsync(CheckpointRequest request, BridgeContext context)
    {
        var checkpointId = request.CheckpointId ?? "";
        if (_files.RestoreCheckpoint(checkpointId, out var restoredPath))
        {
            var workspace = _workspace();
            if (!string.IsNullOrEmpty(workspace))
            {
                var tree = _files.GetDirectoryTree(workspace);
                _send("tree_refreshed", new { tree });
            }

            var exists = _files.FileExists(restoredPath);
            _send("checkpoint_restored", new
            {
                checkpointId,
                path = restoredPath,
                exists,
                content = exists ? _files.ReadAllText(restoredPath) : null
            });
        }
        else
        {
            _send("toast", new { type = "error", message = "Checkpoint no longer available." });
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> InspectFileAsync(InspectFileRequest request, BridgeContext context)
    {
        var path = request.Path;
        var workspace = _workspace();
        if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(workspace))
        {
            path = Path.Combine(workspace, path);
        }

        var report = _files.InspectBinary(path);
        _send("inspect_file_response", new { path, report });
        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of the session/window domain.</summary>
public static class SessionWindowRoutes
{
    public const string WindowDrag = "window_drag";
    public const string WindowClose = "window_close";
    public const string AuthOperator = "auth_operator";
    public const string SaveSession = "save_session";
    public const string SaveRecoveryState = "save_recovery_state";
    public const string ClearRecoveryState = "clear_recovery_state";
    public const string DeleteSession = "delete_session";
    public const string CloseAck = "close_ack";
    public const string ConfirmClose = "confirm_close";
    public const string GetCheckpointDiff = "get_checkpoint_diff";
    public const string RestoreCheckpoint = "restore_checkpoint";
    public const string InspectFile = "inspect_file";
}
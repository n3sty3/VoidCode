using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// The workspace actions.
///
/// Handles these bridge actions: <c>get_init_state</c>, <c>open_workspace_dialog</c>,
/// <c>select_workspace_path</c>, <c>create_project_folder</c>, <c>refresh_workspace</c>,
/// <c>reveal_in_explorer</c> and <c>open_file_dialog</c>. Like the file-system domain they answer in
/// Their existing shapes (<c>init_state_response</c>, <c>tree_refreshed</c>, <c>files_selected</c>, the
/// Toasts), so the page needs no change.
///
/// The window state they used is injected instead of reached for: the settings object, the current
/// Workspace, the "activate this folder" callback (which is where trust, watcher and LSP shutdown
/// Live), and the two dialogs - the last pair has production defaults and is faked in the tests, which
/// Is the only way to cover a route that opens a folder picker.
/// </summary>
public sealed class WorkspaceHandlers
{
    private readonly Func<AppSettings> _settings;
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;
    private readonly Action<string> _activateWorkspace;
    private readonly Func<string?> _promptFolder;
    private readonly Func<string?, string[]> _promptFiles;

    public WorkspaceHandlers(
        Func<AppSettings> settings,
        Func<string?> workspace,
        Action<string, object> send,
        Action<string> activateWorkspace,
        Func<string?>? promptFolder = null,
        Func<string?, string[]>? promptFiles = null)
    {
        _settings = settings;
        _workspace = workspace;
        _send = send;
        _activateWorkspace = activateWorkspace;
        _promptFolder = promptFolder ?? FileSystemService.PromptOpenFolder;
        _promptFiles = promptFiles ?? (current => FileSystemService.PromptOpenFiles(current));
    }

    public sealed class WorkspacePathRequest
    {
        public required string Path { get; init; }
    }

    public sealed class NewProjectRequest
    {
        public string? Name { get; init; }
    }

    public sealed class FileDialogRequest
    {
        public string? Target { get; init; }
    }

    public sealed class InitStateRequest
    {
    }

    public void Register(BridgeRouter router)
    {
        router.Register<InitStateRequest, object?>(WorkspaceRoutes.GetInitState, GetInitStateAsync, envelope: false);
        router.Register<FileDialogRequest, object?>(WorkspaceRoutes.OpenWorkspaceDialog, OpenWorkspaceDialogAsync, envelope: false);
        router.Register<WorkspacePathRequest, object?>(WorkspaceRoutes.SelectWorkspacePath, SelectWorkspacePathAsync, envelope: false);
        router.Register<NewProjectRequest, object?>(WorkspaceRoutes.CreateProjectFolder, CreateProjectFolderAsync, envelope: false);
        router.Register<FileDialogRequest, object?>(WorkspaceRoutes.OpenFileDialog, OpenFileDialogAsync, envelope: false);
        router.Register<InitStateRequest, object?>(WorkspaceRoutes.RefreshWorkspace, RefreshWorkspaceAsync, envelope: false);
        router.Register<WorkspacePathRequest, object?>(WorkspaceRoutes.RevealInExplorer, RevealInExplorerAsync, envelope: false);
    }

    // ---------------------------------------------------------------- handlers (moved verbatim)

    private Task<object?> GetInitStateAsync(InitStateRequest request, BridgeContext context)
    {
        var cli = CliLaunchService.PendingTarget;
        if (cli != null)
        {
            CliLaunchService.PendingTarget = null;
            if (!string.IsNullOrEmpty(cli.WorkspacePath) && Directory.Exists(cli.WorkspacePath))
            {
                _activateWorkspace(cli.WorkspacePath);
            }
        }

        var workspace = _workspace();
        var userSettings = _settings();
        var scoped = WorkspaceSettingsService.Resolve(userSettings, workspace);
        var settings = scoped.Effective;
        if (!string.IsNullOrEmpty(workspace) && Directory.Exists(workspace))
        {
            SettingsService.AddRecentWorkspace(userSettings, workspace);
        }

        var tree = !string.IsNullOrEmpty(workspace) && Directory.Exists(workspace)
            ? FileSystemService.GetDirectoryTree(workspace!)
            : null;

        var sessions = StorageService.LoadSessions();
        var checkpoints = FileSystemService.GetRecentCheckpointsSummary(workspace);
        var recovery = StorageService.LoadRecoveryState(workspace);

        _send("init_state_response", new
        {
            reqId = context.Id,
            settings,
            settingScopes = scoped.Scopes,
            workspace,
            tree,
            sessions,
            checkpoints,
            recovery
        });

        if (cli?.FilePath != null && File.Exists(cli.FilePath))
        {
            try
            {
                var content = FileSystemService.ReadFile(cli.FilePath);
                _send("read_file_response", new { path = cli.FilePath, content, line = cli.Line, column = cli.Column });
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "WorkspaceHandlers.CliOpenFile"); }
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> OpenWorkspaceDialogAsync(FileDialogRequest request, BridgeContext context)
    {
        var folder = _promptFolder();
        if (!string.IsNullOrEmpty(folder))
        {
            _activateWorkspace(folder!);
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> SelectWorkspacePathAsync(WorkspacePathRequest request, BridgeContext context)
    {
        var folder = request.Path;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            _activateWorkspace(folder);
        }
        else
        {
            _send("toast", new { type = "error", message = "Folder does not exist on disk." });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> CreateProjectFolderAsync(NewProjectRequest request, BridgeContext context)
    {
        var projectName = string.IsNullOrEmpty(request.Name) ? "NewProject" : request.Name;
        var parentDir = _promptFolder();
        if (!string.IsNullOrEmpty(parentDir))
        {
            var newFolder = Path.Combine(parentDir!, projectName!);
            Directory.CreateDirectory(newFolder);
            _activateWorkspace(newFolder);
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> OpenFileDialogAsync(FileDialogRequest request, BridgeContext context)
    {
        var target = string.IsNullOrEmpty(request.Target) ? "attach" : request.Target;
        var files = _promptFiles(_workspace());
        if (files.Length > 0)
        {
            _send("files_selected", new { target, files });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> RefreshWorkspaceAsync(InitStateRequest request, BridgeContext context)
    {
        var workspace = _workspace();
        if (!string.IsNullOrEmpty(workspace) && Directory.Exists(workspace))
        {
            var tree = FileSystemService.GetDirectoryTree(workspace!);
            _send("tree_refreshed", new { tree });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> RevealInExplorerAsync(WorkspacePathRequest request, BridgeContext context)
    {
        var path = request.Path;
        if (!string.IsNullOrEmpty(path))
        {
            if (!PlatformServices.RevealInFileManager(path, out var error) && !string.IsNullOrEmpty(error))
            {
                _send("toast", new { type = "error", message = $"Failed to open file manager: {error}" });
            }
        }

        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of the workspace domain.</summary>
public static class WorkspaceRoutes
{
    public const string GetInitState = "get_init_state";
    public const string OpenWorkspaceDialog = "open_workspace_dialog";
    public const string SelectWorkspacePath = "select_workspace_path";
    public const string CreateProjectFolder = "create_project_folder";
    public const string OpenFileDialog = "open_file_dialog";
    public const string RefreshWorkspace = "refresh_workspace";
    public const string RevealInExplorer = "reveal_in_explorer";
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// The file-system actions.
///
/// Handles these bridge actions: including their reply shapes, which
/// Are NOT the router envelope: <c>read_file_response</c>, <c>save_file_response</c>,
/// <c>create_entry_response</c>, <c>delete_entry_response</c>, <c>directory_children_response</c> and
/// <c>all_files_response</c> are what the page already listens for, so they are registered with
/// <c>envelope: false</c> and send themselves. What the router adds here is the part the switch never
/// Had: the payload is deserialized into a request type, so a message without a <c>path</c> is refused
/// As <c>invalid_payload</c> instead of running <c>FileSystemService</c> against an empty string, and
/// Every route runs with a cancellation token.
///
/// The domain owns the two pieces of state that came with it: the current workspace handle (so the
/// Handlers do not reach into the window) and the enumeration CTS that a newer <c>get_all_files</c>
/// Request cancels.
/// </summary>
public sealed class FileSystemHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;
    private readonly IFileSystem _files;
    private readonly object _gate = new();
    private CancellationTokenSource? _enumerationCts;

    /// <param name="files">
    /// The filesystem seam. Production passes nothing and gets <see cref="SystemFileSystem"/>;
    /// A test passes a fake to exercise the failure paths a real disk will not produce on demand.
    /// </param>
    public FileSystemHandlers(Func<string?> workspace, Action<string, object> send, IFileSystem? files = null)
    {
        _workspace = workspace;
        _send = send;
        _files = files ?? new SystemFileSystem();
    }

    // Request types: `required` is the validation. Path is the one field every caller always sends.
    public sealed class PathRequest
    {
        public required string Path { get; init; }
    }

    public sealed class SaveRequest
    {
        public required string Path { get; init; }
        public string? Content { get; init; }
    }

    public sealed class CreateEntryRequest
    {
        public required string Path { get; init; }
        public bool IsDir { get; init; }
    }

    public sealed class RenameRequest
    {
        public required string Path { get; init; }
        public required string NewName { get; init; }
    }

    public sealed class DirectoryChildrenRequest
    {
        public required string Path { get; init; }
        public int Offset { get; init; }
    }

    public sealed class EnumerationRequest
    {
    }

    /// <summary>Cancels a running <c>get_all_files</c> and disposes the token source (window close).</summary>
    public void CancelPending()
    {
        lock (_gate)
        {
            _enumerationCts?.Cancel();
            _enumerationCts?.Dispose();
            _enumerationCts = null;
        }
    }

    public void Register(BridgeRouter router)
    {
        router.Register<EnumerationRequest, object?>(FileSystemRoutes.GetAllFiles, GetAllFilesAsync, envelope: false);
        router.Register<DirectoryChildrenRequest, object?>(FileSystemRoutes.GetDirectoryChildren, GetDirectoryChildrenAsync, envelope: false);
        router.Register<PathRequest, object?>(FileSystemRoutes.ReadFile, ReadFileAsync, envelope: false);
        router.Register<PathRequest, object?>(FileSystemRoutes.ReadTextFile, ReadTextFileAsync, envelope: false);
        router.Register<SaveRequest, object?>(FileSystemRoutes.SaveFile, SaveFileAsync, envelope: false);
        router.Register<CreateEntryRequest, object?>(FileSystemRoutes.CreateEntry, CreateEntryAsync, envelope: false);
        router.Register<RenameRequest, object?>(FileSystemRoutes.RenameEntry, RenameEntryAsync, envelope: false);
        router.Register<PathRequest, object?>(FileSystemRoutes.DeleteEntry, DeleteEntryAsync, envelope: false);
    }

    // ---------------------------------------------------------------- handlers (moved verbatim)

    private Task<object?> GetAllFilesAsync(EnumerationRequest request, BridgeContext context)
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _enumerationCts, next);
        previous?.Cancel();
        previous?.Dispose();
        var token = next.Token;
        var workspace = _workspace();

        _ = Task.Run(() =>
        {
            try
            {
                var files = !string.IsNullOrEmpty(workspace)
                    ? _files.GetAllFilesFlat(workspace!, cancellationToken: token)
                    : new List<object>();
                if (!token.IsCancellationRequested)
                {
                    _send("all_files_response", new { reqId = context.Id, files, truncated = files.Count >= 50_000 });
                }
            }
            catch (OperationCanceledException) { /* enumeration cancelled by a newer request */ }
        }, token);

        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetDirectoryChildrenAsync(DirectoryChildrenRequest request, BridgeContext context)
    {
        var directoryPath = request.Path;
        var offset = request.Offset;
        var workspace = _workspace();

        _ = Task.Run(() =>
        {
            try
            {
                var result = !string.IsNullOrEmpty(workspace)
                    ? _files.GetDirectoryChildrenPage(workspace!, directoryPath, offset)
                    : new DirectoryChildrenResult { Offset = offset };
                _send("directory_children_response", new
                {
                    reqId = context.Id,
                    path = directoryPath,
                    children = result.Children,
                    offset = result.Offset,
                    total = result.Total,
                    hasMore = result.HasMore
                });
            }
            catch (Exception ex)
            {
                _send("directory_children_response", new { reqId = context.Id, path = directoryPath, children = Array.Empty<FileNode>(), offset, total = 0, hasMore = false, error = ex.Message });
            }
        });

        return Task.FromResult<object?>(null);
    }

    private Task<object?> ReadFileAsync(PathRequest request, BridgeContext context)
    {
        var path = request.Path;
        try
        {
            if (_files.IsImageFile(path))
            {
                var dataUri = _files.GetImageDataUri(path, out var sizeBytes);
                _send("image_preview_response", new { reqId = context.Id, path, dataUri, sizeBytes });
            }
            else if (_files.IsBinaryFile(path))
            {
                var report = _files.InspectBinary(path);
                _send("inspect_file_response", new { reqId = context.Id, path, report });
            }
            else
            {
                var text = _files.ReadFile(path);
                var metadata = _files.GetTextFileMetadata(path, text);
                _send("read_file_response", new { reqId = context.Id, path, content = text, metadata });
            }
        }
        catch (Exception ex)
        {
            _send("read_file_response", new { reqId = context.Id, path, error = ex.Message });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> ReadTextFileAsync(PathRequest request, BridgeContext context)
    {
        var path = request.Path;
        try
        {
            if (_files.IsImageFile(path) || _files.IsBinaryFile(path))
                throw new InvalidOperationException("The requested file is not a text document.");
            var content = _files.ReadFile(path);
            var metadata = _files.GetTextFileMetadata(path, content);
            _send("read_text_file_response", new { reqId = context.Id, path, content, metadata });
        }
        catch (Exception ex)
        {
            _send("read_text_file_response", new { reqId = context.Id, path, error = ex.Message });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> SaveFileAsync(SaveRequest request, BridgeContext context)
    {
        var path = request.Path;
        var content = request.Content ?? "";
        try
        {
            string? checkpointId = null;
            if (_files.FileExists(path))
            {
                var existing = _files.ReadAllText(path);
                if (!string.Equals(existing, content, StringComparison.Ordinal))
                {
                    var cp = _files.CreateCheckpoint(path, "Saved");
                    checkpointId = cp.IsRestorable ? cp.Id : null;
                }
            }
            _files.WriteFile(path, content);
            _send("save_file_response", new { reqId = context.Id, path, success = true, checkpointId });
        }
        catch (Exception ex)
        {
            _send("save_file_response", new { reqId = context.Id, path, error = ex.Message });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> CreateEntryAsync(CreateEntryRequest request, BridgeContext context)
    {
        var path = request.Path;
        var isDir = request.IsDir;
        try
        {
            if (isDir) _files.CreateDirectory(path);
            else _files.CreateFile(path);

            _send("create_entry_response", new { reqId = context.Id, path, error = (string?)null });

            var workspace = _workspace();
            if (!string.IsNullOrEmpty(workspace))
            {
                var tree = _files.GetDirectoryTree(workspace!);
                _send("tree_refreshed", new { tree });
            }
            if (!isDir && _files.FileExists(path))
            {
                _send("read_file_response", new { path, content = "" });
            }
        }
        catch (Exception ex)
        {
            _send("toast", new { type = "error", message = $"Failed to create: {ex.Message}" });
            _send("create_entry_response", new { reqId = context.Id, path, error = ex.Message });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> RenameEntryAsync(RenameRequest request, BridgeContext context)
    {
        var path = request.Path;
        var newName = request.NewName;
        try
        {
            _files.Rename(path, newName);
            var workspace = _workspace();
            if (!string.IsNullOrEmpty(workspace))
            {
                var tree = _files.GetDirectoryTree(workspace!);
                _send("tree_refreshed", new { tree });
            }
            _send("toast", new { type = "success", message = $"Renamed to {newName}" });
        }
        catch (Exception ex)
        {
            _send("toast", new { type = "error", message = $"Rename failed: {ex.Message}" });
        }

        return Task.FromResult<object?>(null);
    }

    private Task<object?> DeleteEntryAsync(PathRequest request, BridgeContext context)
    {
        var path = request.Path;
        try
        {
            string? checkpointId = null;
            if (_files.FileExists(path))
            {
                var cp = _files.CreateCheckpoint(path, "Deleted");
                checkpointId = cp.IsRestorable ? cp.Id : null;
            }
            _files.Delete(path);
            var workspace = _workspace();
            if (!string.IsNullOrEmpty(workspace))
            {
                var tree = _files.GetDirectoryTree(workspace!);
                _send("tree_refreshed", new { tree });
            }
            _send("entry_deleted", new { path, checkpointId });
            _send("delete_entry_response", new { reqId = context.Id, path, error = (string?)null });
        }
        catch (Exception ex)
        {
            _send("toast", new { type = "error", message = $"Delete failed: {ex.Message}" });
            _send("delete_entry_response", new { reqId = context.Id, path, error = ex.Message });
        }

        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of this domain, in one place so the window and the tests agree.</summary>
public static class FileSystemRoutes
{
    public const string GetAllFiles = "get_all_files";
    public const string GetDirectoryChildren = "get_directory_children";
    public const string ReadFile = "read_file";
    public const string ReadTextFile = "read_text_file";
    public const string SaveFile = "save_file";
    public const string CreateEntry = "create_entry";
    public const string RenameEntry = "rename_entry";
    public const string DeleteEntry = "delete_entry";
}
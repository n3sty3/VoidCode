using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

/// <summary>
/// One workspace watcher. It owns the
/// FileSystemWatcher, the 380 ms tree-refresh debounce, the per-file 350 ms debounce (editors write
/// Several times in a row), the ignored-directory rules and the code-index upkeep behind every event.
/// The window only says which folder is open and where to send events; nothing here reaches into it.
/// </summary>
public sealed class WorkspaceWatcherService : IDisposable
{
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;

    private FileSystemWatcher? _watcher;
    private Timer? _treeDebounce;

    public WorkspaceWatcherService(Func<string?> workspace, Action<string, object> send)
    {
        _workspace = workspace;
        _send = send;
    }

    /// <summary>Stops watching the current folder; safe to call more than once.</summary>
    public void Dispose() => Stop();

    public void Watch(string folderPath)
    {
        Stop();
        try
        {
            if (!Directory.Exists(folderPath)) return;

            _watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };

            void OnFsEvent(object s, FileSystemEventArgs ev)
            {
                if (IsIgnoredPath(ev.FullPath)) return;
                _treeDebounce?.Change(380, Timeout.Infinite);
            }

            _watcher.Created += (s, ev) =>
            {
                OnFsEvent(s, ev);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(200);
                        if (IsWorkspaceIgnoreFile(ev.FullPath))
                            await CodebaseIndexService.BuildOrUpdateIndexAsync(folderPath);
                        else
                            await CodebaseIndexService.UpdateFileAsync(folderPath, ev.FullPath);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"Could not update the code index for {ev.Name}: {ex.Message}", nameof(MainWindow));
                    }
                });
            };
            _watcher.Deleted += (s, ev) =>
            {
                OnFsEvent(s, ev);
                _ = IsWorkspaceIgnoreFile(ev.FullPath)
                    ? CodebaseIndexService.BuildOrUpdateIndexAsync(folderPath)
                    : CodebaseIndexService.RemoveFileAsync(folderPath, ev.FullPath);
            };
            _watcher.Renamed += (s, ev) =>
            {
                OnFsEvent(s, ev);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (IsWorkspaceIgnoreFile(ev.OldFullPath) || IsWorkspaceIgnoreFile(ev.FullPath))
                        {
                            await CodebaseIndexService.BuildOrUpdateIndexAsync(folderPath);
                            return;
                        }
                        await CodebaseIndexService.RemoveFileAsync(folderPath, ev.OldFullPath);
                        if (Directory.Exists(ev.FullPath))
                        {
                            await CodebaseIndexService.BuildOrUpdateIndexAsync(folderPath);
                        }
                        else
                        {
                            await CodebaseIndexService.UpdateFileAsync(folderPath, ev.FullPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"Could not update the renamed code-index entry: {ex.Message}", nameof(MainWindow));
                    }
                });
            };
            _watcher.Changed += OnFileContentChanged;

            _treeDebounce = new Timer(_ =>
            {
                try
                {
                    // The workspace is read once per tick: asking twice would let a folder switch
                    // Between the check and the use.
                    var workspace = _workspace();
                    if (!string.IsNullOrEmpty(workspace) && Directory.Exists(workspace))
                    {
                        var tree = FileSystemService.GetDirectoryTree(workspace);
                        _send("tree_refreshed", new { tree });
                    }
                }
                catch (Exception ex) { LoggingService.Swallowed(ex, "WorkspaceWatcherService.TreeDebounce"); }
            }, null, Timeout.Infinite, Timeout.Infinite);

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "WorkspaceWatcherService.Watch"); _send("toast", new { type = "warning", message = "File watching is unavailable; the tree will not auto-refresh." }); }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastFileChange = new();

    private void OnFileContentChanged(object sender, FileSystemEventArgs ev)
    {
        try
        {
            if (IsIgnoredPath(ev.FullPath)) return;
            var path = ev.FullPath;
            var stamp = DateTime.UtcNow;
            _lastFileChange[path] = stamp;

            // Debounce per file: editors and formatters often write several times in a row.
            _ = Task.Run(async () =>
            {
                await Task.Delay(350);
                if (!_lastFileChange.TryGetValue(path, out var latest) || latest != stamp) return;
                _lastFileChange.TryRemove(path, out _);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists || info.Length > 1_000_000) return;
                        var content = File.ReadAllText(path);
                        _send("file_changed", new { path, content });
                        var workspace = _workspace();
                        if (!string.IsNullOrEmpty(workspace))
                        {
                            if (IsWorkspaceIgnoreFile(path))
                                await CodebaseIndexService.BuildOrUpdateIndexAsync(workspace);
                            else
                                await CodebaseIndexService.UpdateFileAsync(workspace, path);
                        }
                        return;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(150);
                    }
                    catch { return; }
                }
            });
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "WorkspaceWatcherService.WatchFileThenIndex"); }
    }

    private static bool IsIgnoredPath(string fullPath)
    {
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => FileSystemService.IgnoredDirs.Contains(p));
    }

    private static bool IsWorkspaceIgnoreFile(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        return name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".ignore", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".voidcodeignore", StringComparison.OrdinalIgnoreCase);
    }

    public void Stop()
    {
        try
        {
            _treeDebounce?.Dispose();
            _treeDebounce = null;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch (Exception) { /* watcher already torn down */ }
    }
}

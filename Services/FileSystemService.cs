using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace VoidCode.Services;

public class FileNode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("children")]
    public List<FileNode>? Children { get; set; }

    [JsonPropertyName("hasChildren")]
    public bool HasChildren { get; set; }
}

public class FileCheckpoint
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "Modified";

    [JsonPropertyName("existedBefore")]
    public bool ExistedBefore { get; set; }

    [JsonPropertyName("previousContent")]
    public string? PreviousContent { get; set; }

    [JsonPropertyName("snapshotBlobPath")]
    public string? SnapshotBlobPath { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool IsRestorable => !ExistedBefore || PreviousContent != null ||
        (!string.IsNullOrEmpty(SnapshotBlobPath) && File.Exists(SnapshotBlobPath));
}

public class SearchMatchItem
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("preview")]
    public string Preview { get; set; } = "";
}

public class SearchFileResult
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("fullPath")]
    public string FullPath { get; set; } = "";

    [JsonPropertyName("matches")]
    public List<SearchMatchItem> Matches { get; set; } = new();
}

public class SearchWorkspaceResponse
{
    [JsonPropertyName("searchId")]
    public string SearchId { get; set; } = "";

    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("totalMatches")]
    public int TotalMatches { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("files")]
    public List<SearchFileResult> Files { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class BatchReplaceResponse
{
    [JsonPropertyName("replacedCount")]
    public int ReplacedCount { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class TextFileMetadata
{
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = "UTF-8";

    [JsonPropertyName("eol")]
    public string Eol { get; set; } = "LF";
}

public class DirectoryChildrenResult
{
    [JsonPropertyName("children")]
    public List<FileNode> Children { get; set; } = new();

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

public static class FileSystemService
{
    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", ".next", "__pycache__", "packages", "publish", ".voidcode"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".obj", ".lib", ".bin", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".7z", ".tar", ".gz", ".woff", ".woff2", ".ttf", ".mp3", ".mp4", ".wav"
    };

    private static readonly ConcurrentDictionary<string, FileCheckpoint> Checkpoints = new();

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".png", "image/png" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" },
        { ".webp", "image/webp" },
        { ".ico", "image/x-icon" },
        { ".bmp", "image/bmp" }
    };

    static FileSystemService()
    {
        foreach (var cp in StorageService.LoadCheckpoints())
        {
            if (!string.IsNullOrEmpty(cp.Id))
            {
                Checkpoints[cp.Id] = cp;
            }
        }
    }

    public static bool IsImageFile(string path) => ImageMimeTypes.ContainsKey(Path.GetExtension(path));

    public static bool IsBinaryFile(string path)
    {
        if (IsImageFile(path)) return false;
        var ext = Path.GetExtension(path);
        if (BinaryExtensions.Contains(ext)) return true;
        if (!File.Exists(path)) return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int toRead = (int)Math.Min(512, fs.Length);
            byte[] buf = new byte[toRead];
            int read = fs.Read(buf, 0, toRead);
            // Check for UTF-16 BOM; if not UTF-16, any null byte in first 512 bytes indicates binary
            if (read >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF)))
                return false;
            for (int i = 0; i < read; i++)
            {
                if (buf[i] == 0) return true;
            }
        }
        catch (Exception) { /* unreadable - not treated as binary */ }
        return false;
    }

    public static string GetImageDataUri(string path, out long sizeBytes)
    {
        var bytes = File.ReadAllBytes(path);
        sizeBytes = bytes.Length;
        var ext = Path.GetExtension(path);
        var mime = ImageMimeTypes.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    // Classifies a requested path for read_file: returns handled=true with an agent-facing
    // Hint when it is an image or binary, so reading it as text never returns garbage.
    public static (bool handled, string message) DescribeNonTextFile(string path)
    {
        if (IsImageFile(path))
            return (true, $"'{Path.GetFileName(path)}' is an image ({Path.GetExtension(path)}). Call view_image with this path to actually see it.");
        if (IsBinaryFile(path))
            return (true, $"'{Path.GetFileName(path)}' is a binary file. Use inspect_binary to examine it.");
        return (false, string.Empty);
    }

    public static FileCheckpoint? GetCheckpoint(string checkpointId)
    {
        return Checkpoints.TryGetValue(checkpointId, out var cp) ? cp : null;
    }

    public static List<object> GetRecentCheckpointsSummary(string? workspaceFilter = null)
    {
        return Checkpoints.Values
            .Where(c => string.IsNullOrEmpty(workspaceFilter) ||
                        c.FilePath.StartsWith(workspaceFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Timestamp)
            .Take(40)
            .Select(c => (object)new
            {
                checkpointId = c.Id,
                verb = c.Label,
                target = c.FilePath,
                time = c.Timestamp.ToString("HH:mm:ss")
            })
            .ToList();
    }

    public static FileCheckpoint CreateCheckpoint(string fullPath, string label = "Edited")
    {
        var cpId = Guid.NewGuid().ToString("N");
        string? prevContent = null;
        string? blobPath = null;
        bool existed = File.Exists(fullPath);
        if (existed)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (info.Length <= 4 * 1024 * 1024 && !IsBinaryFile(fullPath))
                {
                    prevContent = File.ReadAllText(fullPath);
                }
                else
                {
                    // Files > 4 MB (and binaries) are copied verbatim to a blob instead of dropping previous content.
                    var blobsDir = Path.Combine(StorageService.Root, "checkpoints", "blobs");
                    Directory.CreateDirectory(blobsDir);
                    blobPath = Path.Combine(blobsDir, cpId + ".bin");
                    File.Copy(fullPath, blobPath, overwrite: true);
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "FileSystemService.CreateCheckpoint"); }
        }

        var cp = new FileCheckpoint
        {
            Id = cpId,
            FilePath = fullPath,
            Label = label,
            ExistedBefore = existed,
            PreviousContent = prevContent,
            SnapshotBlobPath = blobPath,
            Timestamp = DateTime.Now
        };
        Checkpoints[cp.Id] = cp;
        StorageService.SaveCheckpoints(Checkpoints.Values);
        return cp;
    }

    public static bool RestoreCheckpoint(string checkpointId, out string restoredPath)
    {
        restoredPath = "";
        if (!Checkpoints.TryGetValue(checkpointId, out var cp)) return false;

        restoredPath = cp.FilePath;
        if (!cp.ExistedBefore)
        {
            if (File.Exists(cp.FilePath)) File.Delete(cp.FilePath);
        }
        else if (cp.PreviousContent != null)
        {
            var dir = Path.GetDirectoryName(cp.FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteFile(cp.FilePath, cp.PreviousContent);
        }
        else if (!string.IsNullOrEmpty(cp.SnapshotBlobPath) && File.Exists(cp.SnapshotBlobPath))
        {
            var dir = Path.GetDirectoryName(cp.FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(cp.SnapshotBlobPath, cp.FilePath, overwrite: true);
        }
        else
        {
            return false;
        }
        return true;
    }

    public static (bool success, string message, FileCheckpoint? checkpoint) PatchFileSmart(
        string fullPath,
        string searchBlock,
        string replaceBlock)
    {
        if (!File.Exists(fullPath))
        {
            return (false, $"Error: File '{fullPath}' does not exist.", null);
        }

        var originalText = File.ReadAllText(fullPath);
        if (string.IsNullOrEmpty(searchBlock))
        {
            return (false, "Error: 'search' block cannot be empty.", null);
        }

        // Strategy 1: Exact substring match
        if (originalText.Contains(searchBlock, StringComparison.Ordinal))
        {
            var cp = CreateCheckpoint(fullPath, "Patched");
            var idx = originalText.IndexOf(searchBlock, StringComparison.Ordinal);
            var startLine = originalText.Substring(0, idx).Split('\n').Length;
            var updated = originalText.Substring(0, idx) + replaceBlock + originalText.Substring(idx + searchBlock.Length);
            WriteFile(fullPath, updated);
            return (true, $"Successfully patched '{Path.GetFileName(fullPath)}' around line {startLine}.", cp);
        }

        // Strategy 2: CRLF/LF normalized match
        bool usesCrLf = originalText.Contains("\r\n", StringComparison.Ordinal);
        string normOriginal = originalText.Replace("\r\n", "\n");
        string normSearch = searchBlock.Replace("\r\n", "\n");
        string normReplace = replaceBlock.Replace("\r\n", "\n");

        if (normOriginal.Contains(normSearch, StringComparison.Ordinal))
        {
            var cp = CreateCheckpoint(fullPath, "Patched");
            var idx = normOriginal.IndexOf(normSearch, StringComparison.Ordinal);
            var startLine = normOriginal.Substring(0, idx).Split('\n').Length;
            var updated = normOriginal.Substring(0, idx) + normReplace + normOriginal.Substring(idx + normSearch.Length);
            if (usesCrLf) updated = updated.Replace("\n", "\r\n");
            WriteFile(fullPath, updated);
            return (true, $"Successfully patched '{Path.GetFileName(fullPath)}' around line {startLine}.", cp);
        }

        // Strategy 3: Line-by-line trimmed whitespace / indentation-resilient match
        var fileLines = normOriginal.Split('\n');
        var searchLines = normSearch.Split('\n');
        if (searchLines.Length > 0 && fileLines.Length >= searchLines.Length)
        {
            for (int i = 0; i <= fileLines.Length - searchLines.Length; i++)
            {
                bool allMatch = true;
                for (int j = 0; j < searchLines.Length; j++)
                {
                    if (!string.Equals(fileLines[i + j].Trim(), searchLines[j].Trim(), StringComparison.Ordinal))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    var cp = CreateCheckpoint(fullPath, "Patched");
                    var beforeLines = fileLines.Take(i);
                    var afterLines = fileLines.Skip(i + searchLines.Length);
                    var mergedLines = beforeLines.Concat(normReplace.Split('\n')).Concat(afterLines);
                    var sep = usesCrLf ? "\r\n" : "\n";
                    WriteFile(fullPath, string.Join(sep, mergedLines));
                    return (true, $"Successfully patched '{Path.GetFileName(fullPath)}' at lines {i + 1}-{i + searchLines.Length} (fuzzy whitespace match).", cp);
                }
            }
        }

        return (false, $"Error: Target search block was not found in '{Path.GetFileName(fullPath)}'. Read the file first with read_file to verify exact lines.", null);
    }

    public static FileNode GetDirectoryTree(string rootPath)
    {
        var dirInfo = new DirectoryInfo(rootPath);
        var matcher = new WorkspaceIgnoreMatcher(rootPath, IgnoredDirs);
        var node = new FileNode
        {
            Name = dirInfo.Name,
            Path = dirInfo.FullName,
            IsDirectory = true,
            Children = GetDirectoryChildrenInternal(rootPath, dirInfo.FullName, matcher),
            HasChildren = true
        };
        return node;
    }

    public static List<FileNode> GetDirectoryChildren(string rootPath, string directoryPath)
    {
        if (!Directory.Exists(rootPath) || !Directory.Exists(directoryPath)) return new List<FileNode>();
        if (!IsPathWithinRoot(rootPath, directoryPath)) return new List<FileNode>();
        var matcher = new WorkspaceIgnoreMatcher(rootPath, IgnoredDirs);
        return GetDirectoryChildrenInternal(rootPath, directoryPath, matcher);
    }

    public static DirectoryChildrenResult GetDirectoryChildrenPage(
        string rootPath,
        string directoryPath,
        int offset = 0,
        int limit = 500)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 50, 2_000);
        var allChildren = GetDirectoryChildren(rootPath, directoryPath);
        return new DirectoryChildrenResult
        {
            Children = allChildren.Skip(offset).Take(limit).ToList(),
            Offset = offset,
            Total = allChildren.Count,
            HasMore = offset + limit < allChildren.Count
        };
    }

    private static List<FileNode> GetDirectoryChildrenInternal(
        string rootPath,
        string directoryPath,
        WorkspaceIgnoreMatcher matcher)
    {
        var children = new List<FileNode>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(directoryPath).OrderBy(Path.GetFileName))
            {
                if (matcher.IsIgnored(directory, isDirectory: true)) continue;
                children.Add(new FileNode
                {
                    Name = Path.GetFileName(directory),
                    Path = directory,
                    IsDirectory = true,
                    Children = null,
                    HasChildren = HasVisibleChildren(rootPath, directory, matcher)
                });
            }

            foreach (var file in Directory.EnumerateFiles(directoryPath).OrderBy(Path.GetFileName))
            {
                if (matcher.IsIgnored(file)) continue;
                var info = new FileInfo(file);
                children.Add(new FileNode
                {
                    Name = info.Name,
                    Path = info.FullName,
                    IsDirectory = false,
                    Size = info.Length,
                    HasChildren = false
                });
            }
        }
        catch (UnauthorizedAccessException) { /* access denied - skip this entry */ }
        catch (IOException) { /* vanished or locked - skip this entry */ }
        return children;
    }

    private static bool HasVisibleChildren(string rootPath, string directoryPath, WorkspaceIgnoreMatcher matcher)
    {
        try
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                bool isDirectory = Directory.Exists(child);
                if (!matcher.IsIgnored(child, isDirectory)) return true;
            }
        }
        catch (UnauthorizedAccessException) { /* access denied - skip this entry */ }
        catch (IOException) { /* vanished or locked - skip this entry */ }
        return false;
    }

    public static List<object> GetAllFilesFlat(
        string rootPath,
        int maxFiles = 50_000,
        CancellationToken cancellationToken = default)
    {
        var results = new List<object>();
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return results;
        var matcher = new WorkspaceIgnoreMatcher(rootPath, IgnoredDirs);

        void Walk(string dir)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= maxFiles) return;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (matcher.IsIgnored(d, isDirectory: true)) continue;
                    Walk(d);
                }
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.Count >= maxFiles) break;
                    if (matcher.IsIgnored(f)) continue;
                    var rel = Path.GetRelativePath(rootPath, f);
                    results.Add(new { name = Path.GetFileName(f), path = f, relativePath = rel });
                }
            }
            catch (UnauthorizedAccessException) { /* access denied - skip this entry */ }
            catch (IOException) { /* vanished or locked - skip this entry */ }
        }

        Walk(rootPath);
        return results;
    }

    // Containment (reparse points, UNC, ADS, trailing dots/spaces) lives in PathPolicy.
    private static bool IsPathWithinRoot(string rootPath, string candidatePath) =>
        PathPolicy.IsInsideWorkspace(rootPath, candidatePath);

    public static string ReadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
        return File.ReadAllText(path);
    }

    public static TextFileMetadata GetTextFileMetadata(string path, string content)
    {
        var encoding = DetectTextEncoding(path);
        string label = encoding.CodePage switch
        {
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            12000 => "UTF-32 LE",
            12001 => "UTF-32 BE",
            65001 when encoding.GetPreamble().Length > 0 => "UTF-8 BOM",
            65001 => "UTF-8",
            _ => encoding.WebName.ToUpperInvariant()
        };

        string eol = content.Contains("\r\n", StringComparison.Ordinal)
            ? "CRLF"
            : (content.Contains('\n') ? "LF" : "None");

        return new TextFileMetadata { Encoding = label, Eol = eol };
    }

    public static string ReadFileWithLineNumbers(string path, int? startLine = null, int? endLine = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
        var lines = File.ReadAllLines(path);
        int start = Math.Max(1, startLine ?? 1);
        int end = Math.Min(lines.Length, endLine ?? Math.Min(lines.Length, start + 400));

        var sb = new StringBuilder();
        sb.AppendLine($"File: {path} (Showing lines {start}-{end} of {lines.Length})");
        for (int i = start; i <= end; i++)
        {
            sb.AppendLine($"{i,4}: {lines[i - 1]}");
        }
        return sb.ToString();
    }

    public static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var encoding = DetectTextEncoding(path);
        var fileName = Path.GetFileName(path);
        var tempPath = Path.Combine(dir ?? Environment.CurrentDirectory, $".{fileName}.voidcode-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, content, encoding);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null, true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, path, true);
                }
                catch (IOException)
                {
                    File.Move(tempPath, path, true);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static Encoding DetectTextEncoding(string path)
    {
        if (!File.Exists(path)) return new UTF8Encoding(false);

        try
        {
            Span<byte> bom = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = stream.Read(bom);
            if (read >= 4)
            {
                if (bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
                    return new UTF32Encoding(true, true);
                if (bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00)
                    return new UTF32Encoding(false, true);
            }
            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new UTF8Encoding(true);
            if (read >= 2)
            {
                if (bom[0] == 0xFE && bom[1] == 0xFF) return new UnicodeEncoding(true, true);
                if (bom[0] == 0xFF && bom[1] == 0xFE) return new UnicodeEncoding(false, true);
            }
        }
        catch (IOException)
        {
            // The subsequent write will report a useful error if the file remains locked.
        }

        return new UTF8Encoding(false);
    }

    public static void CreateFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(path)) WriteFile(path, "");
    }

    public static void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    public static void Rename(string oldPath, string newName)
    {
        var parent = Path.GetDirectoryName(oldPath) ?? "";
        var newPath = Path.Combine(parent, newName);
        if (File.Exists(oldPath)) File.Move(oldPath, newPath);
        else if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    public static string SearchCode(
        string rootPath,
        string pattern,
        string? fileFilter = null,
        int maxMatches = 120,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath)) return "Error: Workspace directory does not exist.";
        var matcher = new WorkspaceIgnoreMatcher(rootPath, IgnoredDirs);
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            return $"Invalid regex pattern: {ex.Message}";
        }

        var sb = new StringBuilder();
        int matchCount = 0;

        void SearchDir(string dir)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchCount >= maxMatches) return;
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (matcher.IsIgnored(subDir, isDirectory: true)) continue;
                    SearchDir(subDir);
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (matchCount >= maxMatches) break;
                    if (matcher.IsIgnored(file)) continue;
                    var ext = Path.GetExtension(file);
                    if (BinaryExtensions.Contains(ext)) continue;

                    if (!string.IsNullOrWhiteSpace(fileFilter) && !Path.GetFileName(file).Contains(fileFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var info = new FileInfo(file);
                    if (info.Length > 2 * 1024 * 1024) continue;

                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            var rel = Path.GetRelativePath(rootPath, file);
                            sb.AppendLine($"{rel}:{i + 1}: {lines[i].Trim()}");
                            matchCount++;
                            if (matchCount >= maxMatches) break;
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* access denied - skip this entry */ }
            catch (IOException) { /* vanished or locked - skip this entry */ }
        }

        try
        {
            SearchDir(rootPath);
        }
        catch (RegexMatchTimeoutException)
        {
            return "Search pattern timed out. Use a more specific expression.";
        }
        if (matchCount == 0) return $"No matches found for '{pattern}'.";
        return $"Found {matchCount} match(es):\n" + sb.ToString();
    }

    public static SearchWorkspaceResponse SearchWorkspaceStructured(
        string rootPath,
        string pattern,
        bool isRegex = false,
        bool matchCase = false,
        bool matchWholeWord = false,
        string? fileFilter = null,
        string? excludeFilter = null,
        int maxMatches = 500,
        CancellationToken cancellationToken = default)
    {
        var response = new SearchWorkspaceResponse { Query = pattern };
        if (string.IsNullOrWhiteSpace(pattern))
        {
            response.Error = "Search query is empty.";
            return response;
        }

        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            response.Error = "Workspace directory does not exist.";
            return response;
        }

        // Accelerate workspace search with ripgrep (`rg --json`) when available.
        var rgResult = RipgrepSearchService.TrySearch(
            rootPath, pattern, isRegex, matchCase, matchWholeWord, fileFilter, excludeFilter, maxMatches, cancellationToken);
        if (rgResult != null)
        {
            return rgResult;
        }

        var matcher = new WorkspaceIgnoreMatcher(rootPath, IgnoredDirs);

        Regex regex;
        try
        {
            string rawPattern = isRegex ? pattern : Regex.Escape(pattern);
            if (matchWholeWord) rawPattern = $@"\b{rawPattern}\b";
            var options = RegexOptions.Compiled;
            if (!matchCase) options |= RegexOptions.IgnoreCase;
            regex = new Regex(rawPattern, options, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            response.Error = $"Invalid search pattern: {ex.Message}";
            return response;
        }

        var includeFilters = !string.IsNullOrWhiteSpace(fileFilter)
            ? fileFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        var excludeFilters = !string.IsNullOrWhiteSpace(excludeFilter)
            ? excludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        int totalMatches = 0;
        var fileResults = new List<SearchFileResult>();

        void SearchDir(string dir)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalMatches >= maxMatches) return;
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (matcher.IsIgnored(subDir, isDirectory: true)) continue;
                    SearchDir(subDir);
                    if (totalMatches >= maxMatches) return;
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalMatches >= maxMatches) break;
                    if (matcher.IsIgnored(file)) continue;
                    var ext = Path.GetExtension(file);
                    if (BinaryExtensions.Contains(ext)) continue;

                    var fileName = Path.GetFileName(file);
                    var rel = Path.GetRelativePath(rootPath, file);

                    if (includeFilters.Length > 0)
                    {
                        bool matchesInclude = false;
                        foreach (var inc in includeFilters)
                        {
                            if (fileName.Contains(inc, StringComparison.OrdinalIgnoreCase) ||
                                rel.Contains(inc, StringComparison.OrdinalIgnoreCase) ||
                                (inc.StartsWith("*") && fileName.EndsWith(inc.Substring(1), StringComparison.OrdinalIgnoreCase)))
                            {
                                matchesInclude = true;
                                break;
                            }
                        }
                        if (!matchesInclude) continue;
                    }

                    if (excludeFilters.Length > 0)
                    {
                        bool matchesExclude = false;
                        foreach (var exc in excludeFilters)
                        {
                            if (fileName.Contains(exc, StringComparison.OrdinalIgnoreCase) ||
                                rel.Contains(exc, StringComparison.OrdinalIgnoreCase) ||
                                (exc.StartsWith("*") && fileName.EndsWith(exc.Substring(1), StringComparison.OrdinalIgnoreCase)))
                            {
                                matchesExclude = true;
                                break;
                            }
                        }
                        if (matchesExclude) continue;
                    }

                    var info = new FileInfo(file);
                    if (info.Length > 2 * 1024 * 1024) continue; // Skip files > 2MB

                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(file);
                    }
                    catch
                    {
                        continue;
                    }

                    var matchItems = new List<SearchMatchItem>();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var lineText = lines[i];
                        var matches = regex.Matches(lineText);
                        foreach (Match m in matches)
                        {
                            matchItems.Add(new SearchMatchItem
                            {
                                Line = i + 1,
                                Column = m.Index + 1,
                                Length = m.Length,
                                Preview = lineText.Trim()
                            });
                            totalMatches++;
                            if (totalMatches >= maxMatches) break;
                        }
                        if (totalMatches >= maxMatches) break;
                    }

                    if (matchItems.Count > 0)
                    {
                        fileResults.Add(new SearchFileResult
                        {
                            RelativePath = rel,
                            FullPath = file,
                            Matches = matchItems
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* access denied - skip this entry */ }
            catch (IOException) { /* vanished or locked - skip this entry */ }
        }

        try
        {
            SearchDir(rootPath);
        }
        catch (RegexMatchTimeoutException)
        {
            response.Error = "Search pattern timed out. Use a more specific expression.";
            return response;
        }
        response.TotalMatches = totalMatches;
        response.FileCount = fileResults.Count;
        response.Files = fileResults;
        return response;
    }

    public static BatchReplaceResponse ReplaceWorkspaceStructured(
        string rootPath,
        string pattern,
        string replaceText,
        bool isRegex = false,
        bool matchCase = false,
        bool matchWholeWord = false,
        string? fileFilter = null,
        string? excludeFilter = null,
        List<string>? targetRelativeFiles = null)
    {
        var response = new BatchReplaceResponse();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            response.Error = "Search pattern cannot be empty.";
            return response;
        }

        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            response.Error = "Workspace directory does not exist.";
            return response;
        }

        Regex regex;
        try
        {
            string rawPattern = isRegex ? pattern : Regex.Escape(pattern);
            if (matchWholeWord) rawPattern = $@"\b{rawPattern}\b";
            var options = RegexOptions.Compiled;
            if (!matchCase) options |= RegexOptions.IgnoreCase;
            regex = new Regex(rawPattern, options, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            response.Error = $"Invalid pattern: {ex.Message}";
            return response;
        }

        var searchRes = SearchWorkspaceStructured(
            rootPath,
            pattern,
            isRegex,
            matchCase,
            matchWholeWord,
            fileFilter,
            excludeFilter,
            maxMatches: 2000
        );

        if (searchRes.Files.Count == 0)
        {
            response.Message = "No matches found to replace.";
            return response;
        }

        int totalReplacements = 0;
        int modifiedFiles = 0;

        foreach (var fileResult in searchRes.Files)
        {
            if (targetRelativeFiles != null && targetRelativeFiles.Count > 0 &&
                !targetRelativeFiles.Contains(fileResult.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(fileResult.FullPath);
                var matches = regex.Matches(content);
                if (matches.Count > 0)
                {
                    CreateCheckpoint(fileResult.FullPath, "Global Replace");
                    var updated = regex.Replace(content, replaceText);
                    WriteFile(fileResult.FullPath, updated);
                    totalReplacements += matches.Count;
                    modifiedFiles++;
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "FileSystemService.GlobalReplace"); }
        }

        response.ReplacedCount = totalReplacements;
        response.FileCount = modifiedFiles;
        response.Message = $"Replaced {totalReplacements} occurrence(s) across {modifiedFiles} file(s).";
        return response;
    }

    public static string InspectBinary(string fullPath)
    {
        if (!File.Exists(fullPath)) return $"Error: File '{fullPath}' does not exist.";
        var info = new FileInfo(fullPath);
        var bytes = File.ReadAllBytes(fullPath);

        var sb = new StringBuilder();
        sb.AppendLine($"=== BINARY & PE FORENSIC REPORT ===");
        sb.AppendLine($"File:         {info.Name}");
        sb.AppendLine($"Full Path:    {info.FullName}");
        sb.AppendLine($"Size:         {info.Length:N0} bytes ({info.Length / 1024.0:F2} KB)");
        sb.AppendLine($"Modified UTC: {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");

        using var sha256 = SHA256.Create();
        using var md5 = MD5.Create();
        sb.AppendLine($"SHA-256:      {Convert.ToHexString(sha256.ComputeHash(bytes))}");
        sb.AppendLine($"MD5:          {Convert.ToHexString(md5.ComputeHash(bytes))}");

        double entropy = ComputeShannonEntropy(bytes, 0, bytes.Length);
        string entropyTag = entropy > 6.85 ? " [HIGH ENTROPY - Packed / Compressed / Encrypted]" : " [Normal]";
        sb.AppendLine($"Entropy:      {entropy:F3} / 8.000 bits/byte{entropyTag}");

        // Detect Magic Header
        if (bytes.Length >= 64 && bytes[0] == 'M' && bytes[1] == 'Z')
        {
            sb.AppendLine("\n--- PE / COFF HEADER ---");
            int peOffset = BitConverter.ToInt32(bytes, 0x3C);
            if (peOffset > 0 && peOffset + 24 <= bytes.Length &&
                bytes[peOffset] == 'P' && bytes[peOffset + 1] == 'E' && bytes[peOffset + 2] == 0 && bytes[peOffset + 3] == 0)
            {
                ushort machine = BitConverter.ToUInt16(bytes, peOffset + 4);
                ushort numSections = BitConverter.ToUInt16(bytes, peOffset + 6);
                uint timestamp = BitConverter.ToUInt32(bytes, peOffset + 8);
                ushort optHeaderSize = BitConverter.ToUInt16(bytes, peOffset + 20);
                ushort characteristics = BitConverter.ToUInt16(bytes, peOffset + 22);

                string arch = machine switch
                {
                    0x8664 => "x86_64 (AMD64)",
                    0x014c => "x86 (I386 32-bit)",
                    0xAA64 => "ARM64 (AArch64)",
                    _ => $"0x{machine:X4}"
                };

                var buildTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
                bool isDll = (characteristics & 0x2000) != 0;
                bool largeAddr = (characteristics & 0x0020) != 0;

                sb.AppendLine($"Target Arch:  {arch}");
                sb.AppendLine($"Image Type:   {(isDll ? "Dynamic Link Library (DLL)" : "Executable Image (EXE)")}");
                sb.AppendLine($"Compile Time: {buildTime:yyyy-MM-dd HH:mm:ss} UTC (0x{timestamp:X8})");
                sb.AppendLine($"Flags:        0x{characteristics:X4} (LargeAddressAware={largeAddr})");

                int optOffset = peOffset + 24;
                if (optHeaderSize >= 24 && optOffset + optHeaderSize <= bytes.Length)
                {
                    ushort magic = BitConverter.ToUInt16(bytes, optOffset);
                    bool isPe32Plus = magic == 0x20B;
                    uint entryPointRva = BitConverter.ToUInt32(bytes, optOffset + 16);
                    ulong imageBase = isPe32Plus
                        ? BitConverter.ToUInt64(bytes, optOffset + 24)
                        : BitConverter.ToUInt32(bytes, optOffset + 28);
                    uint sizeOfImage = BitConverter.ToUInt32(bytes, optOffset + 56);
                    ushort subsystem = optHeaderSize >= 70 ? BitConverter.ToUInt16(bytes, optOffset + 68) : (ushort)0;

                    string subName = subsystem switch
                    {
                        1 => "NATIVE (Driver / Kernel)",
                        2 => "WINDOWS_GUI",
                        3 => "WINDOWS_CUI (Console)",
                        _ => $"0x{subsystem:X}"
                    };

                    sb.AppendLine($"PE Format:    {(isPe32Plus ? "PE32+ (64-bit)" : "PE32 (32-bit)")}");
                    sb.AppendLine($"Subsystem:    {subName}");
                    sb.AppendLine($"ImageBase:    0x{imageBase:X}");
                    sb.AppendLine($"EntryPoint:   0x{entryPointRva:X} (RVA)");
                    sb.AppendLine($"SizeOfImage:  0x{sizeOfImage:X} ({sizeOfImage:N0} bytes)");
                }

                // Parse Section Table
                int secTableOffset = optOffset + optHeaderSize;
                if (numSections > 0 && secTableOffset + numSections * 40 <= bytes.Length)
                {
                    sb.AppendLine($"\n--- SECTION TABLE ({numSections} sections) ---");
                    sb.AppendLine("Name      VirtAddr    VirtSize    RawAddr     RawSize     Perms  Entropy");
                    for (int s = 0; s < numSections; s++)
                    {
                        int so = secTableOffset + s * 40;
                        string secName = Encoding.ASCII.GetString(bytes, so, 8).TrimEnd('\0');
                        uint virtSize = BitConverter.ToUInt32(bytes, so + 8);
                        uint virtAddr = BitConverter.ToUInt32(bytes, so + 12);
                        uint rawSize = BitConverter.ToUInt32(bytes, so + 16);
                        uint rawPtr = BitConverter.ToUInt32(bytes, so + 20);
                        uint secChars = BitConverter.ToUInt32(bytes, so + 36);

                        string perms =
                            ((secChars & 0x40000000) != 0 ? "R" : "-") +
                            ((secChars & 0x80000000) != 0 ? "W" : "-") +
                            ((secChars & 0x20000000) != 0 ? "X" : "-");

                        double secEnt = 0;
                        if (rawSize > 0 && rawPtr + rawSize <= bytes.Length)
                        {
                            secEnt = ComputeShannonEntropy(bytes, (int)rawPtr, (int)rawSize);
                        }

                        sb.AppendLine($"{secName,-9} 0x{virtAddr:X8}  0x{virtSize:X8}  0x{rawPtr:X8}  0x{rawSize:X8}  {perms,-5}  {secEnt:F2}");
                    }
                }
            }
        }
        else if (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == 'E' && bytes[2] == 'L' && bytes[3] == 'F')
        {
            sb.AppendLine("\nFormat: ELF Binary (Linux / System V)");
        }
        else if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4)
        {
            sb.AppendLine("\nFormat: ZIP Archive / Package (PK\\x03\\x04)");
        }

        // First 64 bytes hex preview
        int hexLen = Math.Min(64, bytes.Length);
        if (hexLen > 0)
        {
            sb.AppendLine("\n--- HEADER HEX DUMP (First 64 bytes) ---");
            for (int row = 0; row < hexLen; row += 16)
            {
                int count = Math.Min(16, hexLen - row);
                var hexPart = string.Join(" ", bytes.Skip(row).Take(count).Select(b => b.ToString("X2")));
                var asciiPart = new string(bytes.Skip(row).Take(count).Select(b => b >= 32 && b <= 126 ? (char)b : '.').ToArray());
                sb.AppendLine($"{row:X4}:  {hexPart,-48}  {asciiPart}");
            }
        }

        // Extract printable ASCII strings >= 6 chars
        sb.AppendLine("\n--- EXTRACTED ASCII STRINGS (Top 50) ---");
        int strCount = 0;
        var currentStr = new StringBuilder();
        foreach (byte b in bytes)
        {
            if (b >= 32 && b <= 126)
            {
                currentStr.Append((char)b);
            }
            else
            {
                if (currentStr.Length >= 6)
                {
                    sb.AppendLine($"  {currentStr}");
                    strCount++;
                    if (strCount >= 50) break;
                }
                currentStr.Clear();
            }
        }

        // Extract UTF-16LE wide strings >= 6 chars
        sb.AppendLine("\n--- EXTRACTED UTF-16LE STRINGS (Top 30) ---");
        int wideCount = 0;
        var wideSb = new StringBuilder();
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            byte lo = bytes[i];
            byte hi = bytes[i + 1];
            if (hi == 0 && lo >= 32 && lo <= 126)
            {
                wideSb.Append((char)lo);
            }
            else
            {
                if (wideSb.Length >= 6)
                {
                    sb.AppendLine($"  {wideSb}");
                    wideCount++;
                    if (wideCount >= 30) break;
                }
                wideSb.Clear();
            }
        }

        return sb.ToString();
    }

    private static double ComputeShannonEntropy(byte[] data, int offset, int length)
    {
        if (length <= 0) return 0.0;
        int[] counts = new int[256];
        int end = Math.Min(data.Length, offset + length);
        int actualLen = end - offset;
        if (actualLen <= 0) return 0.0;

        for (int i = offset; i < end; i++)
        {
            counts[data[i]]++;
        }

        double entropy = 0.0;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = (double)counts[i] / actualLen;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    public static string? PromptOpenFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Project Folder",
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public static string[] PromptOpenFiles(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Files to Attach",
            Multiselect = true,
            Filter = "All Files (*.*)|*.*"
        };

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }
}

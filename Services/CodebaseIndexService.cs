using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

public class CodeChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("file")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("symbols")]
    public List<string> Symbols { get; set; } = new();

    [JsonPropertyName("preview")]
    public string Preview { get; set; } = "";

    [JsonIgnore]
    public HashSet<string> Terms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public float[]? SemanticVector { get; set; }
}

public class CodebaseIndexData
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "";

    [JsonPropertyName("lastIndexedUtc")]
    public DateTime LastIndexedUtc { get; set; }

    [JsonPropertyName("chunks")]
    public List<CodeChunk> Chunks { get; set; } = new();
}

public class SearchResultItem
{
    [JsonPropertyName("file")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("symbols")]
    public List<string> Symbols { get; set; } = new();

    [JsonPropertyName("preview")]
    public string Preview { get; set; } = "";

    [JsonPropertyName("score")]
    public double Score { get; set; }
}

public static class CodebaseIndexService
{
    private static readonly ConcurrentDictionary<string, CodebaseIndexData> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceLocks = new();
    // Workspaces where '.voidcode' isn't writable fall back to an app-data index cache.
    private static readonly ConcurrentDictionary<string, bool> _useFallback = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> _warnedFallback = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex TokenRegex = new(@"[A-Za-z_][A-Za-z0-9_]{1,}", RegexOptions.Compiled);
    private static readonly Regex CamelSplitRegex = new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);
    private static readonly Regex SymbolDefRegex = new(@"(?:class|interface|struct|enum|record|def|function|fn|func|void|async|public|private|protected)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".js", ".jsx", ".tsx", ".py", ".rs", ".go", ".cpp", ".c", ".h", ".hpp",
        ".html", ".css", ".json", ".xaml", ".xml", ".sql", ".md", ".sh", ".ps1"
    };

    public static async Task BuildOrUpdateIndexAsync(string workspace, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace)) return;

        var sem = _workspaceLocks.GetOrAdd(workspace, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var indexData = new CodebaseIndexData
            {
                Workspace = workspace,
                LastIndexedUtc = DateTime.UtcNow
            };

            var files = GetSourceFiles(workspace);
            var chunksList = new List<CodeChunk>();

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var fileChunks = ChunkFile(workspace, file);
                    chunksList.AddRange(fileChunks);
                }
                catch (Exception) { /* unreadable file - skipped from the index */ }
            }

            indexData.Chunks = chunksList;
            _cache[workspace] = indexData;
            await PersistIndexAsync(workspace, indexData, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    public static async Task UpdateFileAsync(string workspace, string fullPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workspace) || string.IsNullOrEmpty(fullPath)) return;
        if (!Directory.Exists(workspace)) return;

        if (!_cache.ContainsKey(workspace))
        {
            await BuildOrUpdateIndexAsync(workspace, ct);
        }
        if (!_cache.TryGetValue(workspace, out var indexData)) return;

        var sem = _workspaceLocks.GetOrAdd(workspace, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var relativePath = Path.GetRelativePath(workspace, fullPath);
            indexData.Chunks.RemoveAll(chunk =>
                string.Equals(chunk.FilePath, relativePath, StringComparison.OrdinalIgnoreCase));

            if (ShouldIndexFile(workspace, fullPath))
            {
                indexData.Chunks.AddRange(ChunkFile(workspace, fullPath));
            }

            indexData.LastIndexedUtc = DateTime.UtcNow;
            await PersistIndexAsync(workspace, indexData, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    public static async Task RemoveFileAsync(string workspace, string fullPath, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(workspace, out var indexData)) return;
        var sem = _workspaceLocks.GetOrAdd(workspace, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var relativePath = Path.GetRelativePath(workspace, fullPath);
            var directoryPrefix = relativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (indexData.Chunks.RemoveAll(chunk =>
                    string.Equals(chunk.FilePath, relativePath, StringComparison.OrdinalIgnoreCase) ||
                    chunk.FilePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                indexData.LastIndexedUtc = DateTime.UtcNow;
                await PersistIndexAsync(workspace, indexData, ct);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public static async Task<List<SearchResultItem>> SearchAsync(string workspace, string query, int topK = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(workspace)) return new List<SearchResultItem>();

        if (!_cache.TryGetValue(workspace, out var indexData) || indexData.Chunks.Count == 0)
        {
            // Try loading from disk (workspace .voidcode, then app-data fallback) or trigger build.
            var indexCandidates = new List<string> { Path.Combine(workspace, ".voidcode", "index.json") };
            try { indexCandidates.Add(GetFallbackIndexPath(workspace)); } catch (Exception) { /* no usable fallback index path */ }
            foreach (var indexPath in indexCandidates)
            {
                if (!File.Exists(indexPath)) continue;
                try
                {
                    var json = await File.ReadAllTextAsync(indexPath);
                    var loaded = JsonSerializer.Deserialize<CodebaseIndexData>(json);
                    if (loaded != null)
                    {
                        foreach (var chunk in loaded.Chunks)
                        {
                            var text = chunk.Preview + " " + string.Join(" ", chunk.Symbols) + " " + chunk.FilePath;
                            chunk.Terms = ExtractTerms(text);
                            chunk.SemanticVector = EmbeddingService.ComputeLocalVector(text);
                        }
                        _cache[workspace] = loaded;
                        indexData = loaded;
                        break;
                    }
                }
                catch (Exception ex) { LoggingService.Swallowed(ex, "CodebaseIndexService.LoadCachedIndex"); }
            }

            if (indexData == null)
            {
                await BuildOrUpdateIndexAsync(workspace);
                _cache.TryGetValue(workspace, out indexData);
            }
        }

        if (indexData == null || indexData.Chunks.Count == 0) return new List<SearchResultItem>();

        var queryTerms = ExtractTerms(query).ToList();
        var queryVector = EmbeddingService.ComputeLocalVector(query);
        if (queryTerms.Count == 0 && queryVector.All(v => v == 0f)) return new List<SearchResultItem>();

        // Compute inverse-document-frequency (IDF) weights across chunks so rare symbols outrank boilerplate.
        int totalChunks = Math.Max(1, indexData.Chunks.Count);
        var termIdf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queryTerms)
        {
            if (termIdf.ContainsKey(q)) continue;
            int df = 0;
            foreach (var c in indexData.Chunks)
            {
                if (c.Terms.Contains(q)) df++;
            }
            termIdf[q] = Math.Log((totalChunks + 1.0) / (df + 1.0)) + 1.0;
        }

        var scored = new List<SearchResultItem>();

        foreach (var chunk in indexData.Chunks)
        {
            double score = 0;

            // 1. Exact & partial symbol definition matches (highest signal)
            foreach (var sym in chunk.Symbols)
            {
                foreach (var q in queryTerms)
                {
                    if (sym.Equals(q, StringComparison.OrdinalIgnoreCase)) score += 10.0;
                    else if (sym.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 4.5;
                }
            }

            // 2. IDF-weighted lexical BM25-style matches
            int matches = 0;
            foreach (var q in queryTerms)
            {
                if (chunk.Terms.Contains(q))
                {
                    matches++;
                    score += 2.0 * (termIdf.TryGetValue(q, out var idf) ? idf : 1.0);
                }
            }

            // 3. File path & filename match boost
            var fileName = Path.GetFileName(chunk.FilePath);
            foreach (var q in queryTerms)
            {
                if (fileName.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 5.0;
                else if (chunk.FilePath.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 2.0;
            }

            // 4. Dense semantic cosine similarity (captures fuzzy/morphological/conceptual matches)
            chunk.SemanticVector ??= EmbeddingService.ComputeLocalVector(chunk.Preview + " " + string.Join(" ", chunk.Symbols) + " " + chunk.FilePath);
            double cosine = EmbeddingService.CosineSimilarity(queryVector, chunk.SemanticVector);
            if (cosine > 0.18)
            {
                score += cosine * 8.0;
            }

            if (score > 0.5)
            {
                double coverageBonus = queryTerms.Count > 0 ? (matches / (double)queryTerms.Count) : 0.0;
                scored.Add(new SearchResultItem
                {
                    FilePath = chunk.FilePath,
                    StartLine = chunk.StartLine,
                    EndLine = chunk.EndLine,
                    Symbols = chunk.Symbols,
                    Preview = chunk.Preview,
                    Score = score + coverageBonus
                });
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .ToList();
    }

    private static List<string> GetSourceFiles(string root)
    {
        var result = new List<string>();
        var matcher = new WorkspaceIgnoreMatcher(root, FileSystemService.IgnoredDirs);
        try
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                if (matcher.IsIgnored(dir, isDirectory: true)) continue;

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!matcher.IsIgnored(sub, isDirectory: true)) stack.Push(sub);
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (matcher.IsIgnored(file)) continue;
                    var ext = Path.GetExtension(file);
                    if (SourceExtensions.Contains(ext))
                    {
                        var info = new FileInfo(file);
                        if (info.Length < 1024 * 1024) // Skip files > 1MB
                        {
                            result.Add(file);
                        }
                    }
                }

                if (result.Count >= 2000) break; // Practical ceiling for fast local IDE indexing
            }
        }
        catch (Exception) { /* unreadable directory - skipped */ }

        return result;
    }

    private static bool ShouldIndexFile(string workspace, string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return false;
            var relative = Path.GetRelativePath(workspace, fullPath);
            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return false;
            var matcher = new WorkspaceIgnoreMatcher(workspace, FileSystemService.IgnoredDirs);
            if (matcher.IsIgnored(fullPath, isDirectory: false)) return false;
            if (!SourceExtensions.Contains(Path.GetExtension(fullPath))) return false;
            return new FileInfo(fullPath).Length < 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    private static string WorkspaceKey(string workspace)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var ch in workspace.ToLowerInvariant())
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
    }

    private static string GetFallbackIndexPath(string workspace)
    {
        // Under the profile root, so a portable run (VOIDCODE_STORAGE_ROOT) and the test suite keep their
        // Indexes out of the developer's %APPDATA%\VoidCode too.
        var dir = Path.Combine(StorageService.Root, "index");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, WorkspaceKey(workspace) + ".json");
    }

    private static string SerializeIndex(CodebaseIndexData indexData) =>
        JsonSerializer.Serialize(indexData, new JsonSerializerOptions { WriteIndented = false });

    private static async Task PersistIndexAsync(string workspace, CodebaseIndexData indexData, CancellationToken ct)
    {
        string? tempPath = null;
        try
        {
            var json = SerializeIndex(indexData);

            if (!_useFallback.ContainsKey(workspace))
            {
                var dir = Path.Combine(workspace, ".voidcode");
                var indexPath = Path.Combine(dir, "index.json");
                try
                {
                    Directory.CreateDirectory(dir);
                    tempPath = indexPath + ".tmp";
                    await File.WriteAllTextAsync(tempPath, json, ct);
                    File.Move(tempPath, indexPath, true);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    _useFallback[workspace] = true;
                    if (_warnedFallback.TryAdd(workspace, true))
                    {
                        LoggingService.LogInfo($"Workspace is not writable; using app-data index cache for '{workspace}'.", nameof(CodebaseIndexService));
                    }
                }
            }

            // Fall back to app-data so the index still persists on read-only/denied workspaces.
            var fallbackPath = GetFallbackIndexPath(workspace);
            tempPath = fallbackPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, fallbackPath, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_warnedFallback.TryAdd(workspace, true))
            {
                LoggingService.LogWarning($"Could not persist the code index: {ex.Message}", nameof(CodebaseIndexService));
            }
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception) { /* temp file already removed or locked */ }
        }
    }

    private static List<CodeChunk> ChunkFile(string workspace, string fullPath)
    {
        var chunks = new List<CodeChunk>();
        var relPath = Path.GetRelativePath(workspace, fullPath);
        var lines = File.ReadAllLines(fullPath);
        if (lines.Length == 0) return chunks;

        // Symbol-boundary chunking: start new chunks at class/function/method declarations when
        // the current chunk already has >= minChunkLines, capping any single chunk at maxChunkLines.
        const int minChunkLines = 14;
        const int maxChunkLines = 52;
        const int overlap = 6;

        int i = 0;
        while (i < lines.Length)
        {
            int start = i;
            int end = Math.Min(lines.Length, start + maxChunkLines);

            // Look for a clean symbol declaration boundary between start + minChunkLines and end.
            for (int probe = start + minChunkLines; probe < end; probe++)
            {
                if (SymbolDefRegex.IsMatch(lines[probe]))
                {
                    end = probe;
                    break;
                }
            }

            int count = Math.Max(1, end - start);
            var slice = lines.Skip(start).Take(count).ToArray();
            var previewText = string.Join("\n", slice);
            var symbols = new List<string>();

            foreach (var line in slice)
            {
                var match = SymbolDefRegex.Match(line);
                if (match.Success && match.Groups[1].Success)
                {
                    var name = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(name) && !symbols.Contains(name))
                    {
                        symbols.Add(name);
                    }
                }
            }

            var semanticSource = previewText + " " + string.Join(" ", symbols) + " " + relPath;
            chunks.Add(new CodeChunk
            {
                Id = $"{relPath}:{start + 1}",
                FilePath = relPath,
                StartLine = start + 1,
                EndLine = start + count,
                Symbols = symbols,
                Preview = previewText,
                Terms = ExtractTerms(semanticSource),
                SemanticVector = EmbeddingService.ComputeLocalVector(semanticSource)
            });

            if (end >= lines.Length) break;
            // If we split on a symbol boundary, start right at that declaration; otherwise keep small overlap.
            i = (end < start + maxChunkLines) ? end : Math.Max(start + 1, end - overlap);
        }

        return chunks;
    }

    private static HashSet<string> ExtractTerms(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return set;

        var matches = TokenRegex.Matches(text);
        foreach (Match m in matches)
        {
            var raw = m.Value;
            if (raw.Length >= 2) set.Add(raw);

            // Split CamelCase into individual subwords
            var parts = CamelSplitRegex.Split(raw);
            if (parts.Length > 1)
            {
                foreach (var p in parts)
                {
                    if (p.Length >= 2) set.Add(p);
                }
            }
        }

        return set;
    }
}

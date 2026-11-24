using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VoidCode.Services;

public sealed class SymbolMatch
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "symbol";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";
}

public sealed class SymbolReference
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("snippet")]
    public string Snippet { get; init; } = "";
}

public sealed class SymbolGraphReport
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = "";

    [JsonPropertyName("definitions")]
    public List<SymbolMatch> Definitions { get; init; } = new();

    [JsonPropertyName("references")]
    public List<SymbolReference> References { get; init; } = new();

    [JsonPropertyName("indexedFiles")]
    public int IndexedFiles { get; init; }
}

/// <summary>
/// Fast incremental SQLite-backed workspace symbol &amp; call-graph indexer (.voidcode/symbols.db).
/// Powers both human Ctrl+T Workspace Symbol Jump and VoidAgent's <c>find_symbol_graph</c> tool.
/// </summary>
public static class SymbolGraphService
{
    private const int MaxFilesToIndex = 800;
    private const long MaxFileSizeBytes = 384 * 1024;
    private static readonly object DbGate = new();

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".rs", ".go",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".kt", ".swift", ".rb", ".php"
    };

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target", ".next",
        ".venv", "venv", "__pycache__", ".vs", ".idea", ".voidcode", "vendor",
        "TestResults", "playwright-report", "test-results"
    };

    private static readonly (Regex Pattern, string Kind)[] DeclarationPatterns =
    {
        // C# / Java / Kotlin / Swift / TS classes, interfaces, structs, records, enums
        (new Regex(@"^\s*(?:export\s+(?:default\s+)?)?(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|final|open|data)\s+)*(class|interface|struct|record|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled), "$1"),
        // TS/JS exported or top-level functions & arrow exports
        (new Regex(@"^\s*(?:export\s+(?:default\s+)?)?(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(", RegexOptions.Compiled), "function"),
        (new Regex(@"^\s*export\s+(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][A-Za-z0-9_$]*)\s*=>", RegexOptions.Compiled), "function"),
        (new Regex(@"^\s*(?:export\s+)?type\s+([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Compiled), "type"),
        // Python def / class
        (new Regex(@"^\s*(?:async\s+)?def\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled), "function"),
        // Rust fn / struct / enum / trait / impl
        (new Regex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?(?:unsafe\s+)?(fn|trait)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled), "$1"),
        // Go func (with optional receiver)
        (new Regex(@"^\s*func\s+(?:\([^)]*\)\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled), "function"),
        // C# / Java / C++ methods (requiring explicit visibility or static modifier to avoid matching control-flow)
        (new Regex(@"^\s*(?:public|private|protected|internal)\s+(?:(?:static|async|virtual|override|sealed|extern|unsafe|new|readonly)\s+)*(?:[A-Za-z0-9_<>,\.\?\s\[\]\(\)]+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*\(", RegexOptions.Compiled), "method"),
    };

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed", "return", "new", "throw", "sizeof", "typeof", "nameof"
    };

    private static string GetDbPath(string workspacePath)
    {
        var dir = Path.Combine(workspacePath, ".voidcode");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "symbols.db");
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS files (
    rel_path TEXT PRIMARY KEY,
    mtime_ticks INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS symbols (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    name_lower TEXT NOT NULL,
    kind TEXT NOT NULL,
    rel_path TEXT NOT NULL,
    line INTEGER NOT NULL,
    signature TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_symbols_name_lower ON symbols(name_lower);
CREATE INDEX IF NOT EXISTS idx_symbols_rel_path ON symbols(rel_path);
";
        cmd.ExecuteNonQuery();
    }

    public static int EnsureIndexed(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return 0;

        lock (DbGate)
        {
            var dbPath = GetDbPath(workspacePath);
            using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            EnsureSchema(conn);

            var existingMtimes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT rel_path, mtime_ticks FROM files";
                using var rdr = sel.ExecuteReader();
                while (rdr.Read())
                {
                    existingMtimes[rdr.GetString(0)] = rdr.GetInt64(1);
                }
            }

            var currentFiles = CollectCodeFiles(workspacePath);
            var currentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var tx = conn.BeginTransaction();
            foreach (var (fullPath, relPath, mtimeTicks) in currentFiles)
            {
                currentSet.Add(relPath);
                if (existingMtimes.TryGetValue(relPath, out var prev) && prev == mtimeTicks)
                    continue;

                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM symbols WHERE rel_path = $p";
                    del.Parameters.AddWithValue("$p", relPath);
                    del.ExecuteNonQuery();
                }

                var extracted = ExtractFileSymbols(fullPath, relPath);
                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO symbols(name, name_lower, kind, rel_path, line, signature) VALUES($n, $nl, $k, $p, $l, $s)";
                    var pN = ins.Parameters.Add("$n", SqliteType.Text);
                    var pNl = ins.Parameters.Add("$nl", SqliteType.Text);
                    var pK = ins.Parameters.Add("$k", SqliteType.Text);
                    var pP = ins.Parameters.Add("$p", SqliteType.Text);
                    var pL = ins.Parameters.Add("$l", SqliteType.Integer);
                    var pS = ins.Parameters.Add("$s", SqliteType.Text);
                    foreach (var sym in extracted)
                    {
                        pN.Value = sym.Name;
                        pNl.Value = sym.Name.ToLowerInvariant();
                        pK.Value = sym.Kind;
                        pP.Value = sym.Path;
                        pL.Value = sym.Line;
                        pS.Value = sym.Signature;
                        ins.ExecuteNonQuery();
                    }
                }

                using (var up = conn.CreateCommand())
                {
                    up.Transaction = tx;
                    up.CommandText = "INSERT INTO files(rel_path, mtime_ticks) VALUES($p, $m) ON CONFLICT(rel_path) DO UPDATE SET mtime_ticks = excluded.mtime_ticks";
                    up.Parameters.AddWithValue("$p", relPath);
                    up.Parameters.AddWithValue("$m", mtimeTicks);
                    up.ExecuteNonQuery();
                }
            }

            // Prune deleted files from index.
            foreach (var oldPath in existingMtimes.Keys)
            {
                if (currentSet.Contains(oldPath)) continue;
                using var delSym = conn.CreateCommand();
                delSym.Transaction = tx;
                delSym.CommandText = "DELETE FROM symbols WHERE rel_path = $p; DELETE FROM files WHERE rel_path = $p;";
                delSym.Parameters.AddWithValue("$p", oldPath);
                delSym.ExecuteNonQuery();
            }

            tx.Commit();
            return currentFiles.Count;
        }
    }

    public static List<SymbolMatch> QuerySymbols(string? workspacePath, string? query, int limit = 60)
    {
        var results = new List<SymbolMatch>();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return results;

        EnsureIndexed(workspacePath);
        var clean = (query ?? "").Trim().ToLowerInvariant();
        var cap = Math.Clamp(limit, 1, 200);

        lock (DbGate)
        {
            var dbPath = GetDbPath(workspacePath);
            using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            if (string.IsNullOrEmpty(clean))
            {
                cmd.CommandText = "SELECT name, kind, rel_path, line, signature FROM symbols ORDER BY rel_path, line LIMIT $lim";
                cmd.Parameters.AddWithValue("$lim", cap);
            }
            else
            {
                cmd.CommandText = @"
SELECT name, kind, rel_path, line, signature
FROM symbols
WHERE name_lower LIKE $contains
ORDER BY
    CASE
        WHEN name_lower = $exact THEN 0
        WHEN name_lower LIKE $prefix THEN 1
        ELSE 2
    END,
    LENGTH(name) ASC,
    rel_path ASC,
    line ASC
LIMIT $lim";
                cmd.Parameters.AddWithValue("$exact", clean);
                cmd.Parameters.AddWithValue("$prefix", clean + "%");
                cmd.Parameters.AddWithValue("$contains", "%" + clean + "%");
                cmd.Parameters.AddWithValue("$lim", cap);
            }

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                results.Add(new SymbolMatch
                {
                    Name = rdr.GetString(0),
                    Kind = rdr.GetString(1),
                    Path = rdr.GetString(2),
                    Line = rdr.GetInt32(3),
                    Signature = rdr.GetString(4)
                });
            }
        }

        return results;
    }

    public static SymbolGraphReport FindSymbolGraph(string? workspacePath, string symbolName, int maxReferences = 35)
    {
        var clean = (symbolName ?? "").Trim();
        var report = new SymbolGraphReport { Symbol = clean };
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath) || string.IsNullOrEmpty(clean))
            return report;

        var indexedCount = EnsureIndexed(workspacePath);
        var defs = QuerySymbols(workspacePath, clean, 25)
            .Where(s => string.Equals(s.Name, clean, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (defs.Count == 0)
        {
            defs = QuerySymbols(workspacePath, clean, 12);
        }

        var defLocations = new HashSet<string>(defs.Select(d => $"{d.Path}:{d.Line}"), StringComparer.OrdinalIgnoreCase);
        var refs = new List<SymbolReference>();
        var refRegex = new Regex($@"\b{Regex.Escape(clean)}\b", RegexOptions.Compiled);
        var files = CollectCodeFiles(workspacePath);

        foreach (var (fullPath, relPath, _) in files)
        {
            if (refs.Count >= maxReferences) break;
            try
            {
                var lines = File.ReadAllLines(fullPath);
                for (int i = 0; i < lines.Length && refs.Count < maxReferences; i++)
                {
                    if (!refRegex.IsMatch(lines[i])) continue;
                    var lineNo = i + 1;
                    if (defLocations.Contains($"{relPath}:{lineNo}")) continue;
                    var snippet = lines[i].Trim();
                    if (snippet.Length > 140) snippet = snippet[..140] + "…";
                    refs.Add(new SymbolReference
                    {
                        Path = relPath,
                        Line = lineNo,
                        Snippet = snippet
                    });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "SymbolGraphService.ScanReferences");
            }
        }

        return new SymbolGraphReport
        {
            Symbol = clean,
            Definitions = defs,
            References = refs,
            IndexedFiles = indexedCount
        };
    }

    public static string FormatGraphForAgent(SymbolGraphReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Symbol Graph for `{report.Symbol}` ({report.IndexedFiles} indexed files):");
        if (report.Definitions.Count == 0)
        {
            sb.AppendLine("Definitions: none found.");
        }
        else
        {
            sb.AppendLine($"Definitions ({report.Definitions.Count}):");
            foreach (var d in report.Definitions)
            {
                sb.AppendLine($"  - [{d.Kind}] {d.Path}:{d.Line}  `{d.Signature}`");
            }
        }

        if (report.References.Count == 0)
        {
            sb.AppendLine("Callers / References: none found.");
        }
        else
        {
            sb.AppendLine($"Callers / References ({report.References.Count}):");
            foreach (var r in report.References)
            {
                sb.AppendLine($"  - {r.Path}:{r.Line}  {r.Snippet}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<SymbolMatch> ExtractFileSymbols(string fullPath, string relPath)
    {
        var list = new List<SymbolMatch>();
        try
        {
            var lines = File.ReadAllLines(fullPath);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.Length > 400) continue;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var (pattern, kindSpec) in DeclarationPatterns)
                {
                    var m = pattern.Match(line);
                    if (!m.Success) continue;

                    string kind;
                    string name;
                    if (kindSpec == "$1" && m.Groups.Count >= 3)
                    {
                        kind = m.Groups[1].Value.ToLowerInvariant();
                        name = m.Groups[2].Value;
                    }
                    else
                    {
                        kind = kindSpec;
                        name = m.Groups[1].Value;
                    }

                    if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || ControlKeywords.Contains(name))
                        break;

                    var sig = trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
                    list.Add(new SymbolMatch
                    {
                        Name = name,
                        Kind = kind,
                        Path = relPath,
                        Line = i + 1,
                        Signature = sig
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "SymbolGraphService.ExtractFileSymbols");
        }
        return list;
    }

    private static List<(string FullPath, string RelPath, long MtimeTicks)> CollectCodeFiles(string workspacePath)
    {
        var files = new List<(string, string, long)>();
        var queue = new Queue<string>();
        queue.Enqueue(workspacePath);

        while (queue.Count > 0 && files.Count < MaxFilesToIndex)
        {
            var dir = queue.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "SymbolGraphService.EnumerateDir");
                continue;
            }

            foreach (var entry in entries)
            {
                if (files.Count >= MaxFilesToIndex) break;
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!IgnoredDirs.Contains(name)) queue.Enqueue(entry);
                    continue;
                }

                var ext = Path.GetExtension(entry);
                if (!CodeExtensions.Contains(ext)) continue;

                try
                {
                    var fi = new FileInfo(entry);
                    if (fi.Length > MaxFileSizeBytes) continue;
                    var rel = Path.GetRelativePath(workspacePath, entry).Replace('\\', '/');
                    files.Add((entry, rel, fi.LastWriteTimeUtc.Ticks));
                }
                catch (Exception ex)
                {
                    LoggingService.Swallowed(ex, "SymbolGraphService.FileInfo");
                }
            }
        }

        return files;
    }
}

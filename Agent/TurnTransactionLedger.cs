using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// Atomic per-turn transaction ledger.
/// Snapshots every mutated path (create, edit, delete, move) to disk BEFORE the first mutation in
/// The turn — copying exact bytes via File.Copy so files > 4 MB and binaries are always reversible.
/// Also records non-reversible actions (commands/network) and computes line-level hunks for
/// Per-file and per-hunk rejection.
/// </summary>
public sealed class TurnFileSnapshot
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "edit";

    [JsonPropertyName("existedBefore")]
    public bool ExistedBefore { get; set; }

    [JsonPropertyName("snapshotFile")]
    public string? SnapshotFile { get; set; }

    [JsonPropertyName("originalLength")]
    public long OriginalLength { get; set; }

    [JsonPropertyName("reversible")]
    public bool Reversible { get; set; } = true;

    [JsonPropertyName("reverted")]
    public bool Reverted { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

public sealed class TurnIrreversibleAction
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("reversible")]
    public bool Reversible { get; set; } = false;
}

public sealed class TurnDiffHunk
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("oldStart")]
    public int OldStart { get; set; }

    [JsonPropertyName("oldCount")]
    public int OldCount { get; set; }

    [JsonPropertyName("newStart")]
    public int NewStart { get; set; }

    [JsonPropertyName("newCount")]
    public int NewCount { get; set; }

    [JsonPropertyName("header")]
    public string Header { get; set; } = "";

    [JsonPropertyName("preview")]
    public string Preview { get; set; } = "";
}

public sealed class TurnFileDiffResult
{
    [JsonPropertyName("turnId")]
    public string TurnId { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("beforeContent")]
    public string BeforeContent { get; set; } = "";

    [JsonPropertyName("afterContent")]
    public string AfterContent { get; set; } = "";

    [JsonPropertyName("isBinary")]
    public bool IsBinary { get; set; }

    [JsonPropertyName("existedBefore")]
    public bool ExistedBefore { get; set; }

    [JsonPropertyName("existsNow")]
    public bool ExistsNow { get; set; }

    [JsonPropertyName("hunks")]
    public List<TurnDiffHunk> Hunks { get; set; } = new();
}

public sealed class TurnRevertResult
{
    [JsonPropertyName("turnId")]
    public string TurnId { get; set; } = "";

    [JsonPropertyName("revertedFiles")]
    public List<string> RevertedFiles { get; set; } = new();

    [JsonPropertyName("failedFiles")]
    public List<string> FailedFiles { get; set; } = new();

    [JsonPropertyName("irreversibleActions")]
    public List<TurnIrreversibleAction> IrreversibleActions { get; set; } = new();
}

public sealed class TurnTransaction
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TurnFileSnapshot> _filesByKey = new(StringComparer.OrdinalIgnoreCase);
    private int _nextOrder;

    [JsonPropertyName("turnId")]
    public string TurnId { get; set; } = "";

    [JsonPropertyName("workspacePath")]
    public string WorkspacePath { get; set; } = "";

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("files")]
    public List<TurnFileSnapshot> Files { get; set; } = new();

    [JsonPropertyName("irreversibleActions")]
    public List<TurnIrreversibleAction> IrreversibleActions { get; set; } = new();

    [JsonIgnore]
    public string TurnDirectory { get; set; } = "";

    internal static string NormalizeKey(string fullPath)
    {
        try { return Path.GetFullPath(fullPath); }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "TurnTransaction.NormalizeKey");
            return fullPath;
        }
    }

    internal void RebuildIndex()
    {
        lock (_gate)
        {
            _filesByKey.Clear();
            foreach (var f in Files)
            {
                _filesByKey[NormalizeKey(f.Path)] = f;
                if (f.Order >= _nextOrder) _nextOrder = f.Order + 1;
            }
        }
    }

    public TurnFileSnapshot SnapshotBeforeMutation(string fullPath, string action)
    {
        var canonical = NormalizeKey(fullPath);
        lock (_gate)
        {
            if (_filesByKey.TryGetValue(canonical, out var existing))
            {
                // Preserve the original pre-turn bytes; only reset reverted flag if touched again.
                existing.Reverted = false;
                if (action == "delete") existing.Action = "delete";
                PersistLocked();
                return existing;
            }

            Directory.CreateDirectory(TurnDirectory);
            var order = _nextOrder++;
            var existed = File.Exists(canonical);
            string? snapshotFile = null;
            long originalLength = 0;

            if (existed)
            {
                var info = new FileInfo(canonical);
                originalLength = info.Length;
                snapshotFile = Path.Combine(TurnDirectory, $"snap_{order:D4}.snap");
                File.Copy(canonical, snapshotFile, overwrite: true);
            }

            var rel = MakeRelative(WorkspacePath, canonical);
            var effectiveAction = !existed && action == "edit" ? "create" : action;

            var snap = new TurnFileSnapshot
            {
                Path = canonical,
                RelativePath = rel,
                Action = effectiveAction,
                ExistedBefore = existed,
                SnapshotFile = snapshotFile,
                OriginalLength = originalLength,
                Reversible = true,
                Reverted = false,
                Order = order
            };

            _filesByKey[canonical] = snap;
            Files.Add(snap);
            PersistLocked();
            return snap;
        }
    }

    public void RecordIrreversible(string toolName, string summary)
    {
        lock (_gate)
        {
            IrreversibleActions.Add(new TurnIrreversibleAction
            {
                Tool = toolName,
                Summary = summary.Length > 200 ? summary[..200] + "..." : summary,
                TimestampUtc = DateTimeOffset.UtcNow,
                Reversible = false
            });
            PersistLocked();
        }
    }

    public bool TryGetSnapshot(string fullPath, out TurnFileSnapshot? snapshot)
    {
        var canonical = NormalizeKey(fullPath);
        lock (_gate)
        {
            return _filesByKey.TryGetValue(canonical, out snapshot);
        }
    }

    public (bool ok, bool existsNow, string? textContent, string? error) RevertFile(string fullPath)
    {
        var canonical = NormalizeKey(fullPath);
        TurnFileSnapshot? snap;
        lock (_gate)
        {
            if (!_filesByKey.TryGetValue(canonical, out snap))
            {
                return (false, File.Exists(canonical), null, "File is not recorded in this turn.");
            }
        }

        try
        {
            if (!snap.ExistedBefore)
            {
                if (File.Exists(canonical)) File.Delete(canonical);
                lock (_gate) { snap.Reverted = true; PersistLocked(); }
                return (true, false, null, null);
            }

            if (string.IsNullOrEmpty(snap.SnapshotFile) || !File.Exists(snap.SnapshotFile))
            {
                return (false, File.Exists(canonical), null, "Snapshot blob is missing on disk.");
            }

            var dir = Path.GetDirectoryName(canonical);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(snap.SnapshotFile, canonical, overwrite: true);

            lock (_gate) { snap.Reverted = true; PersistLocked(); }
            string? text = FileSystemService.IsBinaryFile(canonical) ? null : File.ReadAllText(canonical);
            return (true, true, text, null);
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Failed to revert '{canonical}' in turn '{TurnId}': {ex.Message}", ex, nameof(TurnTransaction));
            return (false, File.Exists(canonical), null, ex.Message);
        }
    }

    public TurnRevertResult RevertWholeTurn()
    {
        List<TurnFileSnapshot> ordered;
        List<TurnIrreversibleAction> irreversible;
        lock (_gate)
        {
            ordered = Files.OrderByDescending(f => f.Order).ToList();
            irreversible = IrreversibleActions.ToList();
        }

        var result = new TurnRevertResult
        {
            TurnId = TurnId,
            IrreversibleActions = irreversible
        };

        foreach (var snap in ordered)
        {
            var (ok, _, _, _) = RevertFile(snap.Path);
            if (ok) result.RevertedFiles.Add(snap.Path);
            else result.FailedFiles.Add(snap.Path);
        }

        return result;
    }

    public TurnFileDiffResult? GetFileDiff(string fullPath)
    {
        if (!TryGetSnapshot(fullPath, out var snap) || snap == null) return null;

        var existsNow = File.Exists(snap.Path);
        var isBinary = (existsNow && FileSystemService.IsBinaryFile(snap.Path))
            || (!existsNow && snap.ExistedBefore && !string.IsNullOrEmpty(snap.SnapshotFile) && File.Exists(snap.SnapshotFile) && FileSystemService.IsBinaryFile(snap.SnapshotFile));

        if (isBinary)
        {
            return new TurnFileDiffResult
            {
                TurnId = TurnId,
                Path = snap.Path,
                RelativePath = snap.RelativePath,
                IsBinary = true,
                ExistedBefore = snap.ExistedBefore,
                ExistsNow = existsNow
            };
        }

        var beforeText = snap.ExistedBefore && !string.IsNullOrEmpty(snap.SnapshotFile) && File.Exists(snap.SnapshotFile)
            ? File.ReadAllText(snap.SnapshotFile)
            : "";
        var afterText = existsNow ? File.ReadAllText(snap.Path) : "";
        var hunks = TurnDiffEngine.ComputeHunks(beforeText, afterText);

        return new TurnFileDiffResult
        {
            TurnId = TurnId,
            Path = snap.Path,
            RelativePath = snap.RelativePath,
            BeforeContent = beforeText,
            AfterContent = afterText,
            IsBinary = false,
            ExistedBefore = snap.ExistedBefore,
            ExistsNow = existsNow,
            Hunks = hunks
        };
    }

    public (bool ok, TurnFileDiffResult? updatedDiff, string? error) RejectHunk(string fullPath, int hunkIndex)
    {
        if (!TryGetSnapshot(fullPath, out var snap) || snap == null)
            return (false, null, "File is not recorded in this turn.");

        var beforeText = snap.ExistedBefore && !string.IsNullOrEmpty(snap.SnapshotFile) && File.Exists(snap.SnapshotFile)
            ? File.ReadAllText(snap.SnapshotFile)
            : "";
        var existsNow = File.Exists(snap.Path);
        var afterText = existsNow ? File.ReadAllText(snap.Path) : "";

        var (ok, updatedText, err) = TurnDiffEngine.RevertSingleHunk(beforeText, afterText, hunkIndex);
        if (!ok || updatedText == null)
            return (false, null, err ?? "Could not reject hunk.");

        if (!snap.ExistedBefore && updatedText.Length == 0)
        {
            if (File.Exists(snap.Path)) File.Delete(snap.Path);
        }
        else
        {
            var dir = Path.GetDirectoryName(snap.Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            FileSystemService.WriteFile(snap.Path, updatedText);
        }

        var refreshed = GetFileDiff(snap.Path);
        if (refreshed != null && refreshed.Hunks.Count == 0)
        {
            lock (_gate) { snap.Reverted = true; PersistLocked(); }
        }
        return (true, refreshed, null);
    }

    public object ToSummaryPayload()
    {
        lock (_gate)
        {
            return new
            {
                turnId = TurnId,
                workspacePath = WorkspacePath,
                files = Files.OrderBy(f => f.Order).Select(f => new
                {
                    path = f.Path,
                    relativePath = f.RelativePath,
                    action = f.Action,
                    existedBefore = f.ExistedBefore,
                    reversible = f.Reversible,
                    reverted = f.Reverted
                }).ToList(),
                irreversibleActions = IrreversibleActions.Select(a => new
                {
                    tool = a.Tool,
                    summary = a.Summary,
                    reversible = a.Reversible
                }).ToList()
            };
        }
    }

    private void PersistLocked()
    {
        try
        {
            Directory.CreateDirectory(TurnDirectory);
            var ledgerPath = Path.Combine(TurnDirectory, "ledger.json");
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ledgerPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "TurnTransaction.PersistLocked");
        }
    }

    private static string MakeRelative(string workspacePath, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return Path.GetFileName(fullPath);
        try
        {
            var rel = Path.GetRelativePath(workspacePath, fullPath);
            return rel.StartsWith("..", StringComparison.Ordinal) ? fullPath : rel;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "TurnTransaction.MakeRelative");
            return Path.GetFileName(fullPath);
        }
    }
}

public static class TurnTransactionLedger
{
    private static readonly ConcurrentDictionary<string, TurnTransaction> Turns = new(StringComparer.OrdinalIgnoreCase);

    public static string CheckpointsRoot => Path.Combine(StorageService.Root, "checkpoints");

    public static TurnTransaction BeginTurn(string workspacePath)
    {
        var turnId = $"turn_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var dir = Path.Combine(CheckpointsRoot, turnId);
        var tx = new TurnTransaction
        {
            TurnId = turnId,
            WorkspacePath = workspacePath,
            CreatedUtc = DateTimeOffset.UtcNow,
            TurnDirectory = dir
        };
        Turns[turnId] = tx;
        return tx;
    }

    public static TurnTransaction? GetTurn(string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId)) return null;
        if (Turns.TryGetValue(turnId, out var cached)) return cached;

        // Reload from disk if the app restarted since the turn ran.
        if (turnId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || turnId.Contains("..", StringComparison.Ordinal))
            return null;

        var dir = Path.Combine(CheckpointsRoot, turnId);
        var ledgerPath = Path.Combine(dir, "ledger.json");
        if (!File.Exists(ledgerPath)) return null;

        try
        {
            var tx = JsonSerializer.Deserialize<TurnTransaction>(File.ReadAllText(ledgerPath));
            if (tx == null) return null;
            tx.TurnDirectory = dir;
            tx.RebuildIndex();
            Turns[turnId] = tx;
            return tx;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "TurnTransactionLedger.LoadFromDisk");
            return null;
        }
    }
}

/// <summary>
/// Line-level diff & single-hunk revert engine used by per-hunk agent review.
/// </summary>
internal static class TurnDiffEngine
{
    private sealed record RawHunk(int OldStart, int OldCount, int NewStart, int NewCount, string[] OldLines, string[] NewLines);

    internal static List<TurnDiffHunk> ComputeHunks(string beforeText, string afterText)
    {
        var raw = ComputeRawHunks(beforeText, afterText);
        var result = new List<TurnDiffHunk>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var h = raw[i];
            var preview = BuildPreview(h);
            var header = $"@@ -{h.OldStart + 1},{h.OldCount} +{h.NewStart + 1},{h.NewCount} @@";
            result.Add(new TurnDiffHunk
            {
                Index = i,
                OldStart = h.OldStart + 1,
                OldCount = h.OldCount,
                NewStart = h.NewStart + 1,
                NewCount = h.NewCount,
                Header = header,
                Preview = preview
            });
        }
        return result;
    }

    internal static (bool ok, string? updatedText, string? error) RevertSingleHunk(string beforeText, string afterText, int hunkIndex)
    {
        var usesCrLf = afterText.Contains("\r\n", StringComparison.Ordinal)
            || (!afterText.Contains('\n') && beforeText.Contains("\r\n", StringComparison.Ordinal));
        var trailingNewline = afterText.EndsWith('\n') || (afterText.Length == 0 && beforeText.EndsWith('\n'));

        var raw = ComputeRawHunks(beforeText, afterText);
        if (hunkIndex < 0 || hunkIndex >= raw.Count)
            return (false, null, $"Hunk index {hunkIndex} is out of range (found {raw.Count}).");

        var target = raw[hunkIndex];
        var newLines = SplitLines(afterText);
        var rebuilt = new List<string>(newLines.Length - target.NewCount + target.OldCount);

        for (int i = 0; i < target.NewStart; i++) rebuilt.Add(newLines[i]);
        rebuilt.AddRange(target.OldLines);
        for (int i = target.NewStart + target.NewCount; i < newLines.Length; i++) rebuilt.Add(newLines[i]);

        if (rebuilt.Count == 0) return (true, "", null);
        var eol = usesCrLf ? "\r\n" : "\n";
        var joined = string.Join(eol, rebuilt) + (trailingNewline ? eol : "");
        return (true, joined, null);
    }

    private static List<RawHunk> ComputeRawHunks(string beforeText, string afterText)
    {
        var a = SplitLines(beforeText);
        var b = SplitLines(afterText);

        // Trim common prefix and suffix so large files with small edits stay O(changed^2) and fast.
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
            prefix++;

        int suffix = 0;
        while (suffix < (a.Length - prefix) && suffix < (b.Length - prefix) &&
               string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        int aMidLen = a.Length - prefix - suffix;
        int bMidLen = b.Length - prefix - suffix;
        if (aMidLen == 0 && bMidLen == 0) return new List<RawHunk>();

        // Guard against quadratic memory on huge completely-rewritten files: fall back to a single whole-region hunk.
        if ((long)aMidLen * bMidLen > 4_000_000L)
        {
            return new List<RawHunk>
            {
                new(prefix, aMidLen, prefix, bMidLen, a[prefix..(prefix + aMidLen)], b[prefix..(prefix + bMidLen)])
            };
        }

        // Standard LCS DP over the middle slice.
        var dp = new int[aMidLen + 1, bMidLen + 1];
        for (int i = aMidLen - 1; i >= 0; i--)
        {
            for (int j = bMidLen - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[prefix + i], b[prefix + j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        // Walk ops and group contiguous non-equal operations into hunks.
        var hunks = new List<RawHunk>();
        int ai = 0, bi = 0;
        while (ai < aMidLen || bi < bMidLen)
        {
            if (ai < aMidLen && bi < bMidLen && string.Equals(a[prefix + ai], b[prefix + bi], StringComparison.Ordinal))
            {
                ai++;
                bi++;
                continue;
            }

            int oldStart = prefix + ai;
            int newStart = prefix + bi;
            var oldChunk = new List<string>();
            var newChunk = new List<string>();

            while (ai < aMidLen || bi < bMidLen)
            {
                if (ai < aMidLen && bi < bMidLen && string.Equals(a[prefix + ai], b[prefix + bi], StringComparison.Ordinal))
                    break;

                if (ai < aMidLen && (bi == bMidLen || dp[ai + 1, bi] >= dp[ai, bi + 1]))
                {
                    oldChunk.Add(a[prefix + ai]);
                    ai++;
                }
                else
                {
                    newChunk.Add(b[prefix + bi]);
                    bi++;
                }
            }

            hunks.Add(new RawHunk(oldStart, oldChunk.Count, newStart, newChunk.Count, oldChunk.ToArray(), newChunk.ToArray()));
        }

        return hunks;
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var norm = text.Replace("\r\n", "\n");
        if (norm.EndsWith('\n')) norm = norm[..^1];
        return norm.Length == 0 ? Array.Empty<string>() : norm.Split('\n');
    }

    private static string BuildPreview(RawHunk hunk)
    {
        var sb = new StringBuilder();
        int shown = 0;
        foreach (var line in hunk.OldLines)
        {
            if (shown++ >= 4) { sb.AppendLine("  ..."); break; }
            sb.Append("- ").AppendLine(line.Length > 100 ? line[..100] + "..." : line);
        }
        shown = 0;
        foreach (var line in hunk.NewLines)
        {
            if (shown++ >= 4) { sb.AppendLine("  ..."); break; }
            sb.Append("+ ").AppendLine(line.Length > 100 ? line[..100] + "..." : line);
        }
        return sb.ToString().TrimEnd();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoidCode.Agent;

/// <summary>
/// Server-side turn continuity &amp; mid-run steering queue for VoidAgent v2.
/// Remembers compact tool-call/file-edit summaries across chat turns (so follow-up prompts
/// never suffer tool amnesia when frontend chatHistory sends prose) and queues mid-run
/// operator steering messages cleanly between agent steps.
/// </summary>
internal static class AgentSessionMemory
{
    private const int MaxEntriesPerWorkspace = 24;
    private static readonly ConcurrentDictionary<string, List<string>> WorkspaceTraces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> SteerQueue = new();
    private static readonly object Gate = new();

    private static string NormalizeKey(string? workspacePath)
        => string.IsNullOrWhiteSpace(workspacePath) ? "__default__" : workspacePath.Trim().TrimEnd('\\', '/');

    internal static void RecordAction(string? workspacePath, string summaryLine)
    {
        if (string.IsNullOrWhiteSpace(summaryLine)) return;
        var key = NormalizeKey(workspacePath);
        lock (Gate)
        {
            var list = WorkspaceTraces.GetOrAdd(key, _ => new List<string>());
            list.Add(summaryLine.Trim());
            if (list.Count > MaxEntriesPerWorkspace)
                list.RemoveRange(0, list.Count - MaxEntriesPerWorkspace);
        }
    }

    internal static void ClearWorkspace(string? workspacePath)
    {
        WorkspaceTraces.TryRemove(NormalizeKey(workspacePath), out _);
    }

    internal static string FormatRecentWorkForPrompt(string? workspacePath)
    {
        var key = NormalizeKey(workspacePath);
        List<string> snapshot;
        lock (Gate)
        {
            if (!WorkspaceTraces.TryGetValue(key, out var list) || list.Count == 0)
                return "";
            snapshot = list.ToList();
        }

        var sb = new StringBuilder();
        sb.AppendLine("\n\n=== RECENT SESSION TOOL TRACE (PRIOR TURNS) ===");
        sb.AppendLine("Files already inspected, edited, or verified earlier in this session (avoid redundant full re-reads unless modified):");
        foreach (var line in snapshot)
        {
            sb.Append("- ").AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    internal static void EnqueueSteer(string message)
    {
        var clean = (message ?? "").Trim();
        if (!string.IsNullOrEmpty(clean))
            SteerQueue.Enqueue(clean);
    }

    internal static List<string> DrainSteers()
    {
        var drained = new List<string>();
        while (SteerQueue.TryDequeue(out var msg))
            drained.Add(msg);
        return drained;
    }

    internal static void ClearSteers()
    {
        while (SteerQueue.TryDequeue(out _)) { /* drain stale steers on new run or cancel */ }
    }
}

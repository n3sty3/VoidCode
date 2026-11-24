using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace VoidCode.Agent;

/// <summary>
/// Server-side turn transcript continuity &amp; mid-run steering queue for VoidAgent v2.
/// Preserves real compacted tool-call/result messages across turns (so follow-up prompts
/// remember exact code read &amp; edited without busting the system prompt cache) and queues
/// mid-run operator steering messages cleanly between agent steps.
/// </summary>
internal static class AgentSessionMemory
{
    private const int MaxEntriesPerWorkspace = 24;
    private const int MaxStoredExchanges = 12;
    private const int MaxToolContentChars = 2400;

    private static readonly ConcurrentDictionary<string, List<string>> WorkspaceTraces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<JsonObject>> WorkspaceToolTranscript = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Stores a real assistant(tool_calls) + tool(results) exchange from a completed step
    /// so the next user prompt can replay exact prior code reads/edits cleanly.
    /// </summary>
    internal static void RecordToolExchange(string? workspacePath, JsonObject assistantToolMsg, IReadOnlyList<JsonObject> toolResultMsgs)
    {
        if (assistantToolMsg == null || toolResultMsgs == null || toolResultMsgs.Count == 0) return;
        var key = NormalizeKey(workspacePath);
        lock (Gate)
        {
            var list = WorkspaceToolTranscript.GetOrAdd(key, _ => new List<JsonObject>());
            var clonedAssistant = JsonNode.Parse(assistantToolMsg.ToJsonString())?.AsObject();
            if (clonedAssistant != null) list.Add(clonedAssistant);

            foreach (var tr in toolResultMsgs)
            {
                var cloned = JsonNode.Parse(tr.ToJsonString())?.AsObject();
                if (cloned == null) continue;
                var raw = cloned["content"]?.GetValue<string>() ?? "";
                if (raw.Length > MaxToolContentChars)
                {
                    cloned["content"] = raw[..MaxToolContentChars] + "\n... [prior turn tool result compacted]";
                }
                list.Add(cloned);
            }

            // Cap total stored messages while preserving assistant->tool pairing boundaries.
            while (list.Count > MaxStoredExchanges * 3)
            {
                list.RemoveAt(0);
                while (list.Count > 0 && string.Equals(list[0]["role"]?.GetValue<string>(), "tool", StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(0);
            }
        }
    }

    internal static List<JsonObject> GetRecentToolTranscript(string? workspacePath)
    {
        var key = NormalizeKey(workspacePath);
        lock (Gate)
        {
            if (!WorkspaceToolTranscript.TryGetValue(key, out var list) || list.Count == 0)
                return new List<JsonObject>();

            return list
                .Select(m => JsonNode.Parse(m.ToJsonString())?.AsObject())
                .Where(m => m != null)
                .Cast<JsonObject>()
                .ToList();
        }
    }

    internal static void ClearWorkspace(string? workspacePath)
    {
        var key = NormalizeKey(workspacePath);
        WorkspaceTraces.TryRemove(key, out _);
        WorkspaceToolTranscript.TryRemove(key, out _);
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace VoidCode.Agent;

/// <summary>
/// Maintains two distinct per-session/workspace structures:
/// 1. The authoritative, chronologically ordered conversation transcript
///    (<c>user</c>, <c>assistant</c> with <c>reasoning_content</c>/<c>_thinking_blocks</c>/<c>tool_calls</c>,
///    and <c>tool</c> results) so multi-turn follow-ups replay exact history in order without detaching
///    tool calls from the turns that produced them.
/// 2. A lightweight mid-run operator steering queue and recent action trace.
/// </summary>
internal static class AgentSessionMemory
{
    private const int MaxEntriesPerWorkspace = 24;
    private const int MaxOrderedTranscriptMessages = 240;

    private static readonly ConcurrentDictionary<string, List<string>> WorkspaceTraces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<JsonObject>> SessionTranscripts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> SteerQueue = new();
    private static readonly object Gate = new();

    private static string NormalizeKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? "_default" : key.Trim().TrimEnd('\\', '/');

    internal static string ResolveSessionKey(string? workspacePath, string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"session:{sessionId.Trim()}";
        return NormalizeKey(workspacePath);
    }

    internal static void RecordAction(string? workspacePath, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return;
        var key = NormalizeKey(workspacePath);
        lock (Gate)
        {
            var list = WorkspaceTraces.GetOrAdd(key, _ => new List<string>());
            list.Add(summary.Trim());
            if (list.Count > MaxEntriesPerWorkspace)
                list.RemoveRange(0, list.Count - MaxEntriesPerWorkspace);
        }
    }

    /// <summary>
    /// Saves the full chronologically ordered message list (excluding the leading system prompt)
    /// for the active session so subsequent turns replay real user/assistant/tool_calls/tool order.
    /// Tool results are kept intact — dynamic context-window budgeting is handled by ContextBudget.
    /// </summary>
    internal static void SaveOrderedTranscript(string? sessionKey, IReadOnlyList<JsonObject> messagesWithoutSystem)
    {
        if (messagesWithoutSystem == null) return;
        var key = NormalizeKey(sessionKey);
        var cloned = new List<JsonObject>(messagesWithoutSystem.Count);
        foreach (var msg in messagesWithoutSystem)
        {
            if (msg == null) continue;
            var role = msg["role"]?.GetValue<string>() ?? "";
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) continue;
            var copy = JsonNode.Parse(msg.ToJsonString())?.AsObject();
            if (copy != null) cloned.Add(copy);
        }

        // Trim from the front only at clean user-message boundaries if a marathon session grows huge.
        while (cloned.Count > MaxOrderedTranscriptMessages)
        {
            int nextUser = -1;
            for (int i = 1; i < cloned.Count; i++)
            {
                if (string.Equals(cloned[i]["role"]?.GetValue<string>(), "user", StringComparison.OrdinalIgnoreCase))
                {
                    nextUser = i;
                    break;
                }
            }
            if (nextUser <= 0) break;
            cloned.RemoveRange(0, nextUser);
        }

        lock (Gate)
        {
            SessionTranscripts[key] = cloned;
        }
    }

    internal static List<JsonObject> GetOrderedTranscript(string? sessionKey)
    {
        var key = NormalizeKey(sessionKey);
        lock (Gate)
        {
            if (!SessionTranscripts.TryGetValue(key, out var list) || list.Count == 0)
                return new List<JsonObject>();

            return list
                .Select(m => JsonNode.Parse(m.ToJsonString())?.AsObject())
                .Where(m => m != null)
                .Cast<JsonObject>()
                .ToList();
        }
    }

    /// <summary>
    /// Counts real user turns in a transcript (excluding synthetic view_image multimodal injection turns).
    /// </summary>
    internal static int CountUserTurns(IReadOnlyList<JsonObject> messages)
    {
        int count = 0;
        foreach (var m in messages)
        {
            if (!string.Equals(m["role"]?.GetValue<string>(), "user", StringComparison.OrdinalIgnoreCase))
                continue;
            if (m["content"] is JsonArray parts &&
                parts.Count > 0 &&
                (parts[0]?["text"]?.GetValue<string>() ?? "").StartsWith("Images loaded via view_image", StringComparison.Ordinal))
            {
                continue;
            }
            if (m["content"] is JsonValue v && v.TryGetValue<string>(out var s) &&
                s.StartsWith("[Operator Mid-Run Steering Guidance]", StringComparison.Ordinal))
            {
                continue;
            }
            count++;
        }
        return count;
    }

    internal static void ClearSession(string? sessionKey)
    {
        var key = NormalizeKey(sessionKey);
        SessionTranscripts.TryRemove(key, out _);
    }

    internal static void ClearWorkspace(string? workspacePath)
    {
        var key = NormalizeKey(workspacePath);
        WorkspaceTraces.TryRemove(key, out _);
        SessionTranscripts.TryRemove(key, out _);
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

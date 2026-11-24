using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Tools;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// Fast token estimation and context-window budgeting / compaction applied before
/// Every model turn so long chats and multi-step agent runs never blow past the context limit.
/// </summary>
internal static class TokenEstimator
{
    private const int TokensPerImage = 1000;
    private const int MessageOverheadTokens = 8;

    internal static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // Code, JSON, and stack traces tokenize denser (~3.0-3.2 chars/token) than English prose (~4.0 chars/token).
        // Sample up to 2048 chars to detect code/punctuation density cheaply without allocations.
        int sampleLen = Math.Min(text.Length, 2048);
        int denseChars = 0;
        for (int i = 0; i < sampleLen; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '{' || c == '}' || c == '(' || c == ')' || c == '[' || c == ']' ||
                c == ';' || c == ':' || c == '=' || c == '<' || c == '>' || c == '"' || c == '\'' ||
                c == '/' || c == '\\' || c == '.' || c == ',' || c == '_')
            {
                denseChars++;
            }
        }
        bool isCodeHeavy = sampleLen >= 24 && (denseChars * 100 / sampleLen) >= 10;
        return isCodeHeavy
            ? (int)Math.Ceiling(text.Length / 3.1)
            : (text.Length + 3) / 4;
    }

    internal static int EstimateMessage(JsonObject? message)
    {
        if (message == null) return 0;
        int tokens = MessageOverheadTokens;

        var content = message["content"];
        if (content is JsonValue val && val.TryGetValue<string>(out var str))
        {
            tokens += EstimateText(str);
        }
        else if (content is JsonArray parts)
        {
            foreach (var part in parts)
            {
                var type = part?["type"]?.GetValue<string>() ?? "";
                if (type == "image_url" || type == "image")
                {
                    tokens += TokensPerImage;
                }
                else
                {
                    tokens += EstimateText(part?["text"]?.GetValue<string>() ?? part?.ToJsonString());
                }
            }
        }
        else if (content != null)
        {
            tokens += EstimateText(content.ToJsonString());
        }

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            tokens += EstimateText(toolCalls.ToJsonString());
        }

        if (message["reasoning_content"] is JsonValue reasoningVal && reasoningVal.TryGetValue<string>(out var reasoningStr))
        {
            tokens += EstimateText(reasoningStr);
        }

        if (message["_thinking_blocks"] is JsonArray thinkingBlocks)
        {
            tokens += EstimateText(thinkingBlocks.ToJsonString());
        }

        return tokens;
    }

    internal static int EstimateMessages(IEnumerable<JsonObject> messages)
    {
        int total = 0;
        foreach (var m in messages) total += EstimateMessage(m);
        return total;
    }
}

internal sealed record CompactionResult(
    List<JsonObject> Messages,
    bool Compacted,
    int OriginalTokens,
    int FinalTokens,
    string? Note);

internal static class ContextBudget
{
    internal const int DefaultKeepRecentToolResults = 6;
    private const int MinToolResultCharsToStub = 240;
    private const int MaxSummarizerInputChars = 14000;

    internal static int ComputeInputBudget(int contextWindow, int maxOutputTokens)
    {
        var window = Math.Max(4096, contextWindow);
        var reservedOutput = Math.Clamp(maxOutputTokens, 512, window / 2);
        return Math.Max(1536, window - reservedOutput);
    }

    internal static async Task<CompactionResult> CompactAsync(
        IReadOnlyList<JsonObject> sourceMessages,
        int contextWindow,
        int maxOutputTokens,
        int keepRecentToolResults = DefaultKeepRecentToolResults,
        Func<string, CancellationToken, Task<string>>? summarizeAsync = null,
        CancellationToken cancellationToken = default)
    {
        var working = CloneAll(sourceMessages);
        var originalTokens = TokenEstimator.EstimateMessages(working);
        var inputBudget = ComputeInputBudget(contextWindow, maxOutputTokens);
        var threshold = (int)(inputBudget * 0.80);

        if (originalTokens <= threshold)
        {
            return new CompactionResult(working, false, originalTokens, originalTokens, null);
        }

        var toolCallLookup = BuildToolCallLookup(working);
        var stubbedCount = StubOldToolResults(working, keepRecentToolResults, toolCallLookup);
        var afterStubTokens = TokenEstimator.EstimateMessages(working);
        if (afterStubTokens <= threshold)
        {
            return new CompactionResult(
                working,
                true,
                originalTokens,
                afterStubTokens,
                $"Compacted {stubbedCount} older tool results ({originalTokens:N0} -> {afterStubTokens:N0} est. tokens).");
        }

        // Pass 2: summarize the middle while preserving system prompt, first user message (task),
        // And the latest user turn + active tail.
        var (headEndExclusive, tailStartInclusive) = FindPinnedBounds(working);
        var middleCount = Math.Max(0, tailStartInclusive - headEndExclusive);

        if (middleCount > 0)
        {
            var middle = working.GetRange(headEndExclusive, middleCount);
            var summaryText = await SummarizeMiddleAsync(middle, summarizeAsync, cancellationToken);

            var compacted = new List<JsonObject>(working.Count - middleCount + 1);
            for (int i = 0; i < headEndExclusive; i++) compacted.Add(working[i]);
            compacted.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"[Conversation summary — {middleCount} earlier messages compacted]\n{summaryText}"
            });
            for (int i = tailStartInclusive; i < working.Count; i++) compacted.Add(working[i]);

            working = SanitizeToolPairs(compacted);
        }

        var afterSummaryTokens = TokenEstimator.EstimateMessages(working);
        if (afterSummaryTokens <= inputBudget)
        {
            return new CompactionResult(
                working,
                true,
                originalTokens,
                afterSummaryTokens,
                $"Compacted conversation history ({originalTokens:N0} -> {afterSummaryTokens:N0} est. tokens).");
        }

        // Pass 3: hard-fit guarantee (e.g., 300k synthetic history targeting an 8k window).
        // Stub even the remaining recent tool outputs, then progressively clip oversized non-task messages.
        StubOldToolResults(working, keepRecentToolResults: 0, toolCallLookup);
        EnforceHardBudget(working, inputBudget);
        working = SanitizeToolPairs(working);

        var finalTokens = TokenEstimator.EstimateMessages(working);
        return new CompactionResult(
            working,
            true,
            originalTokens,
            finalTokens,
            $"Compacted context to fit window ({originalTokens:N0} -> {finalTokens:N0} est. tokens).");
    }

    private static List<JsonObject> CloneAll(IReadOnlyList<JsonObject> messages)
    {
        var list = new List<JsonObject>(messages.Count);
        foreach (var msg in messages)
        {
            var cloned = JsonNode.Parse(msg.ToJsonString())?.AsObject();
            if (cloned != null) list.Add(cloned);
        }
        return list;
    }

    private static Dictionary<string, (string name, string target)> BuildToolCallLookup(List<JsonObject> messages)
    {
        var map = new Dictionary<string, (string name, string target)>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (msg["tool_calls"] is not JsonArray tcs) continue;
            foreach (var tc in tcs)
            {
                var id = tc?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                var name = tc?["function"]?["name"]?.GetValue<string>() ?? "tool";
                var argsRaw = tc?["function"]?["arguments"]?.GetValue<string>() ?? "";
                var target = ExtractToolTarget(argsRaw);
                map[id] = (name, target);
            }
        }
        return map;
    }

    private static string ExtractToolTarget(string argsRaw)
    {
        if (string.IsNullOrWhiteSpace(argsRaw)) return "";
        try
        {
            var node = JsonNode.Parse(argsRaw);
            foreach (var key in new[] { "path", "source_path", "pattern", "query", "command", "url" })
            {
                var val = node?[key]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    var compact = val.Replace("\r", " ").Replace("\n", " ").Trim();
                    return compact.Length > 60 ? compact[..60] + "..." : compact;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "ContextBudget.ExtractToolTarget");
        }
        return "";
    }

    private static int StubOldToolResults(
        List<JsonObject> messages,
        int keepRecentToolResults,
        Dictionary<string, (string name, string target)> toolCallLookup)
    {
        var toolIndices = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (string.Equals(messages[i]["role"]?.GetValue<string>(), "tool", StringComparison.OrdinalIgnoreCase))
            {
                toolIndices.Add(i);
            }
        }

        var stubLimit = Math.Max(0, toolIndices.Count - Math.Max(0, keepRecentToolResults));
        int stubbed = 0;
        for (int k = 0; k < stubLimit; k++)
        {
            var msg = messages[toolIndices[k]];
            var content = GetStringContent(msg);
            if (content.Length <= MinToolResultCharsToStub) continue;

            var callId = msg["tool_call_id"]?.GetValue<string>() ?? "";
            var label = toolCallLookup.TryGetValue(callId, out var info)
                ? (string.IsNullOrEmpty(info.target) ? info.name : $"{info.name} {info.target}")
                : "tool";

            var byteSize = Encoding.UTF8.GetByteCount(content);
            var sizeStr = byteSize >= 1024 ? $"{byteSize / 1024.0:0.0} KB" : $"{byteSize} B";
            msg["content"] = $"[{label} -> {sizeStr} omitted]";
            stubbed++;
        }
        return stubbed;
    }

    private static (int headEndExclusive, int tailStartInclusive) FindPinnedBounds(List<JsonObject> messages)
    {
        // Head: leading system prompt(s) + the first user message (original task).
        int headEnd = 0;
        while (headEnd < messages.Count && IsRole(messages[headEnd], "system"))
        {
            headEnd++;
        }
        for (int i = headEnd; i < messages.Count; i++)
        {
            if (IsRole(messages[i], "user"))
            {
                headEnd = i + 1;
                break;
            }
        }

        // Tail: start from the latest user message, or keep the last few messages so active
        // Tool execution retains immediate state.
        int latestUser = -1;
        for (int i = messages.Count - 1; i >= headEnd; i--)
        {
            if (IsRole(messages[i], "user"))
            {
                latestUser = i;
                break;
            }
        }

        int tailStart = latestUser >= headEnd
            ? latestUser
            : Math.Max(headEnd, messages.Count - 4);

        // Never start tail in the middle of a tool-result sequence (would orphan tool messages).
        while (tailStart > headEnd && IsRole(messages[tailStart], "tool"))
        {
            tailStart--;
        }

        // Also make sure headEnd does not stop right before tool results belonging to headEnd-1.
        while (headEnd < tailStart && IsRole(messages[headEnd], "tool"))
        {
            headEnd++;
        }

        return (headEnd, Math.Max(headEnd, tailStart));
    }

    private static async Task<string> SummarizeMiddleAsync(
        List<JsonObject> middle,
        Func<string, CancellationToken, Task<string>>? summarizeAsync,
        CancellationToken cancellationToken)
    {
        var transcript = BuildTranscriptExcerpt(middle, MaxSummarizerInputChars);
        if (summarizeAsync != null)
        {
            try
            {
                var summary = await summarizeAsync(transcript, cancellationToken);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    return ToolOutputPolicy.Truncate(summary.Trim(), 3000, "summary clipped");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "ContextBudget.SummarizeMiddleAsync");
            }
        }

        return BuildDeterministicSummary(middle);
    }

    private static string BuildTranscriptExcerpt(List<JsonObject> messages, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg["role"]?.GetValue<string>() ?? "unknown";
            var content = GetStringContent(msg).Trim();
            if (content.Length > 600) content = ToolOutputPolicy.Truncate(content, 600);

            sb.Append('[').Append(role).Append("] ");
            if (!string.IsNullOrEmpty(content)) sb.Append(content);
            if (msg["tool_calls"] is JsonArray tcs && tcs.Count > 0)
            {
                var names = tcs.Select(t => t?["function"]?["name"]?.GetValue<string>() ?? "tool");
                sb.Append(" (called tools: ").Append(string.Join(", ", names)).Append(')');
            }
            sb.AppendLine();
            if (sb.Length >= maxChars) break;
        }
        return ToolOutputPolicy.Truncate(sb.ToString(), maxChars);
    }

    private static string BuildDeterministicSummary(List<JsonObject> middle)
    {
        var sb = new StringBuilder();
        var toolsUsed = new List<string>();
        int userTurns = 0;
        int assistantTurns = 0;

        foreach (var msg in middle)
        {
            var role = msg["role"]?.GetValue<string>() ?? "";
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                userTurns++;
                var snippet = FirstLine(GetStringContent(msg), 160);
                if (!string.IsNullOrEmpty(snippet)) sb.AppendLine($"- User: {snippet}");
            }
            else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                assistantTurns++;
                if (msg["tool_calls"] is JsonArray tcs)
                {
                    foreach (var tc in tcs)
                    {
                        var name = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrEmpty(name)) toolsUsed.Add(name);
                    }
                }
                var snippet = FirstLine(GetStringContent(msg), 160);
                if (!string.IsNullOrEmpty(snippet)) sb.AppendLine($"- Assistant: {snippet}");
            }
        }

        if (toolsUsed.Count > 0)
        {
            var grouped = toolsUsed
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key} x{g.Count()}");
            sb.AppendLine($"- Tools invoked: {string.Join(", ", grouped)}");
        }

        if (sb.Length == 0)
        {
            sb.AppendLine($"- Earlier context ({userTurns} user, {assistantTurns} assistant turns) compacted.");
        }

        return ToolOutputPolicy.Truncate(sb.ToString().Trim(), 2000);
    }

    private static void EnforceHardBudget(List<JsonObject> messages, int inputBudget)
    {
        // Step A: drop non-pinned interior messages (keeping index 0 system, first user, latest user).
        int firstUserIdx = FindFirstUserIndex(messages);
        int lastUserIdx = FindLastUserIndex(messages);

        while (TokenEstimator.EstimateMessages(messages) > inputBudget && messages.Count > 3)
        {
            int dropIdx = -1;
            for (int i = 1; i < messages.Count - 1; i++)
            {
                if (i == firstUserIdx || i == lastUserIdx || IsRole(messages[i], "system")) continue;
                dropIdx = i;
                break;
            }
            if (dropIdx == -1) break;

            // If dropping an assistant with tool_calls, drop its matching following tool messages too.
            if (IsRole(messages[dropIdx], "assistant") && messages[dropIdx]["tool_calls"] is JsonArray)
            {
                messages.RemoveAt(dropIdx);
                while (dropIdx < messages.Count && IsRole(messages[dropIdx], "tool"))
                {
                    messages.RemoveAt(dropIdx);
                }
            }
            else
            {
                messages.RemoveAt(dropIdx);
            }

            firstUserIdx = FindFirstUserIndex(messages);
            lastUserIdx = FindLastUserIndex(messages);
        }

        // Step B: if still over budget, trim large editor-context blocks or oversized individual messages
        // While keeping the opening of the first user task intact.
        int current = TokenEstimator.EstimateMessages(messages);
        if (current <= inputBudget) return;

        // First strip embedded EDITOR CONTEXT inside system prompt if present.
        for (int i = 0; i < messages.Count && current > inputBudget; i++)
        {
            if (!IsRole(messages[i], "system")) continue;
            var sys = GetStringContent(messages[i]);
            var marker = "=== EDITOR CONTEXT";
            var idx = sys.IndexOf(marker, StringComparison.Ordinal);
            if (idx > 0)
            {
                messages[i]["content"] = sys[..idx].TrimEnd() + "\n[Editor context omitted to fit context window; use read_file as needed.]";
                current = TokenEstimator.EstimateMessages(messages);
            }
        }

        // Finally proportion-cap any remaining huge message strings so total <= inputBudget.
        if (current > inputBudget)
        {
            var maxCharsPerMessage = Math.Max(400, (inputBudget * 4) / Math.Max(1, messages.Count) - 80);
            for (int i = messages.Count - 1; i >= 0 && TokenEstimator.EstimateMessages(messages) > inputBudget; i--)
            {
                var text = GetStringContent(messages[i]);
                if (text.Length > maxCharsPerMessage)
                {
                    messages[i]["content"] = ToolOutputPolicy.Truncate(text, maxCharsPerMessage, "clipped to fit context budget");
                }
            }
        }
    }

    private static List<JsonObject> SanitizeToolPairs(List<JsonObject> messages)
    {
        var result = new List<JsonObject>(messages.Count);
        var validToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (IsRole(msg, "assistant") && msg["tool_calls"] is JsonArray tcs && tcs.Count > 0)
            {
                // Verify at least one following tool result exists for these ids; otherwise strip dangling tool_calls.
                var expectedIds = new HashSet<string>(
                    tcs.Select(t => t?["id"]?.GetValue<string>() ?? "").Where(id => !string.IsNullOrEmpty(id)),
                    StringComparer.Ordinal);

                var presentIds = new HashSet<string>(StringComparer.Ordinal);
                for (int j = i + 1; j < messages.Count && IsRole(messages[j], "tool"); j++)
                {
                    var tid = messages[j]["tool_call_id"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(tid)) presentIds.Add(tid);
                }

                if (expectedIds.Count > 0 && expectedIds.IsSubsetOf(presentIds))
                {
                    foreach (var id in expectedIds) validToolCallIds.Add(id);
                    result.Add(msg);
                }
                else
                {
                    var clone = JsonNode.Parse(msg.ToJsonString())!.AsObject();
                    clone.Remove("tool_calls");
                    if (!string.IsNullOrWhiteSpace(GetStringContent(clone)))
                    {
                        result.Add(clone);
                    }
                }
            }
            else if (IsRole(msg, "tool"))
            {
                var tid = msg["tool_call_id"]?.GetValue<string>() ?? "";
                if (validToolCallIds.Contains(tid))
                {
                    result.Add(msg);
                }
            }
            else
            {
                result.Add(msg);
            }
        }

        return result;
    }

    private static int FindFirstUserIndex(List<JsonObject> messages)
    {
        for (int i = 0; i < messages.Count; i++)
            if (IsRole(messages[i], "user")) return i;
        return -1;
    }

    private static int FindLastUserIndex(List<JsonObject> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (IsRole(messages[i], "user")) return i;
        return -1;
    }

    private static bool IsRole(JsonObject msg, string role)
        => string.Equals(msg["role"]?.GetValue<string>(), role, StringComparison.OrdinalIgnoreCase);

    private static string GetStringContent(JsonObject msg)
    {
        if (msg["content"] is JsonValue val && val.TryGetValue<string>(out var s)) return s ?? string.Empty;
        if (msg["content"] is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var p in arr)
            {
                var t = p?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(t))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(t);
                }
            }
            return sb.ToString();
        }
        return string.Empty;
    }

    private static string FirstLine(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var line = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length > maxLen ? line[..maxLen] + "..." : line;
    }
}

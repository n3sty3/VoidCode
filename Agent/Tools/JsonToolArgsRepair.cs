using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VoidCode.Agent.Tools;

/// <summary>
/// Tolerant JSON argument parser &amp; auto-repairer for LLM tool calls.
/// Handles trailing commas, markdown fences, and truncated closing braces commonly emitted
/// by smaller or local models (Ollama/Qwen/Llama) so tool calls don't needlessly crash.
/// </summary>
internal static class JsonToolArgsRepair
{
    private static readonly JsonDocumentOptions LenientDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Regex TrailingCommaRegex = new(@",\s*([}\]])", RegexOptions.Compiled);

    internal static JsonNode? ParseOrRepair(string? rawJson)
    {
        var text = (rawJson ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return new JsonObject();

        // Fast path: valid JSON.
        try { return JsonNode.Parse(text, documentOptions: LenientDocOptions); }
        catch (JsonException) { /* attempt repair below */ }

        // Strip markdown code fences if a model wrapped arguments in ```json ... ```.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = text.IndexOf('\n');
            if (nl > 0) text = text[(nl + 1)..];
            var endFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence >= 0) text = text[..endFence];
            text = text.Trim();
        }

        // Remove trailing commas before } or ].
        text = TrailingCommaRegex.Replace(text, "$1");

        try { return JsonNode.Parse(text, documentOptions: LenientDocOptions); }
        catch (JsonException) { /* attempt structural balance repair */ }

        // Balance unclosed quotes / braces / brackets if the stream truncated near the end.
        var repaired = BalanceJsonTail(text);
        try { return JsonNode.Parse(repaired, documentOptions: LenientDocOptions); }
        catch (JsonException) { return null; }
    }

    private static string BalanceJsonTail(string input)
    {
        var sb = new StringBuilder(input.Length + 16);
        var stack = new System.Collections.Generic.Stack<char>();
        bool inString = false;
        bool escaped = false;

        foreach (char c in input)
        {
            sb.Append(c);
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (!inString)
            {
                if (c == '{') stack.Push('}');
                else if (c == '[') stack.Push(']');
                else if ((c == '}' || c == ']') && stack.Count > 0 && stack.Peek() == c) stack.Pop();
            }
        }

        if (escaped) sb.Append('\\');
        if (inString) sb.Append('"');

        var cleaned = TrailingCommaRegex.Replace(sb.ToString().TrimEnd(), "$1").TrimEnd(',');
        var tail = new StringBuilder(cleaned);
        while (stack.Count > 0)
            tail.Append(stack.Pop());

        return tail.ToString();
    }
}

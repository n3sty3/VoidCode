using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoidCode.Services;

public sealed class MemoryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "project";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "fact";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("createdAtUtc")]
    public string CreatedAtUtc { get; set; } = "";
}

public sealed class MemoryActionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("item")]
    public MemoryItem? Item { get; set; }
}

/// <summary>
/// Cross-session persistent memory bank for VoidAgent.
/// Project memories live in &lt;workspace&gt;/.voidcode/memory.json; global operator preferences
/// live in &lt;StorageService.Root&gt;/memory.json. Both are injected compactly into the system prompt.
/// </summary>
public static class AgentMemoryService
{
    private static readonly object Gate = new();
    private const int MaxItemsPerScope = 60;
    private const int MaxPromptChars = 3600;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string GlobalPath => Path.Combine(StorageService.Root, "memory.json");

    private static string? ProjectPath(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return null;
        return Path.Combine(workspacePath, ".voidcode", "memory.json");
    }

    public static List<MemoryItem> List(string? workspacePath)
    {
        lock (Gate)
        {
            var result = new List<MemoryItem>();
            var projPath = ProjectPath(workspacePath);
            if (projPath != null)
            {
                foreach (var item in ReadFile(projPath))
                {
                    item.Scope = "project";
                    result.Add(item);
                }
            }

            foreach (var item in ReadFile(GlobalPath))
            {
                item.Scope = "global";
                result.Add(item);
            }

            return result
                .OrderByDescending(i => i.CreatedAtUtc, StringComparer.Ordinal)
                .ToList();
        }
    }

    public static MemoryActionResult Save(string? workspacePath, string? scope, string? category, string? content)
    {
        var cleanContent = (content ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanContent))
            return new MemoryActionResult { Ok = false, Message = "Memory content cannot be empty." };

        if (cleanContent.Length > 600)
            cleanContent = cleanContent[..600].Trim();

        var normalizedScope = string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase)
            ? "global"
            : "project";

        var targetPath = normalizedScope == "project" ? ProjectPath(workspacePath) : GlobalPath;
        if (targetPath == null)
        {
            // Fall back to global if no workspace is currently open.
            normalizedScope = "global";
            targetPath = GlobalPath;
        }

        var normalizedCategory = NormalizeCategory(category);

        lock (Gate)
        {
            var items = ReadFile(targetPath);
            var existing = items.FirstOrDefault(i =>
                string.Equals(i.Content.Trim(), cleanContent, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Category = normalizedCategory;
                existing.CreatedAtUtc = DateTime.UtcNow.ToString("O");
                WriteFile(targetPath, items);
                return new MemoryActionResult
                {
                    Ok = true,
                    Message = $"Updated existing {normalizedScope} memory ({existing.Id}).",
                    Item = existing
                };
            }

            var item = new MemoryItem
            {
                Id = "mem_" + Guid.NewGuid().ToString("N")[..10],
                Scope = normalizedScope,
                Category = normalizedCategory,
                Content = cleanContent,
                CreatedAtUtc = DateTime.UtcNow.ToString("O")
            };

            items.Insert(0, item);
            if (items.Count > MaxItemsPerScope)
                items = items.Take(MaxItemsPerScope).ToList();

            WriteFile(targetPath, items);
            return new MemoryActionResult
            {
                Ok = true,
                Message = $"Saved {normalizedScope} memory ({item.Id}).",
                Item = item
            };
        }
    }

    public static MemoryActionResult Delete(string? workspacePath, string? id)
    {
        var cleanId = (id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanId))
            return new MemoryActionResult { Ok = false, Message = "Memory ID is required." };

        lock (Gate)
        {
            bool removed = false;
            var projPath = ProjectPath(workspacePath);
            if (projPath != null && RemoveFromFile(projPath, cleanId))
                removed = true;

            if (RemoveFromFile(GlobalPath, cleanId))
                removed = true;

            return removed
                ? new MemoryActionResult { Ok = true, Message = $"Deleted memory '{cleanId}'." }
                : new MemoryActionResult { Ok = false, Message = $"Memory '{cleanId}' not found." };
        }
    }

    public static string FormatForPrompt(string? workspacePath)
    {
        var items = List(workspacePath);
        if (items.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("\n\n=== PERSISTENT AGENT MEMORY BANK ===");
        sb.AppendLine("Facts, conventions, and operator preferences saved from previous sessions:");

        foreach (var item in items)
        {
            var line = $"- [{item.Scope}/{item.Category}] ({item.Id}): {item.Content}";
            if (sb.Length + line.Length + 2 > MaxPromptChars) break;
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string NormalizeCategory(string? category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            "preference" or "convention" or "architecture" or "command" or "pitfall" or "fact" => c,
            _ => "fact"
        };
    }

    private static List<MemoryItem> ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<MemoryItem>();
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<MemoryItem>>(json, JsonOpts) ?? new List<MemoryItem>();
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "AgentMemoryService.ReadFile");
            return new List<MemoryItem>();
        }
    }

    private static void WriteFile(string path, List<MemoryItem> items)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOpts), Encoding.UTF8);
        File.Move(temp, path, true);
    }

    private static bool RemoveFromFile(string path, string id)
    {
        if (!File.Exists(path)) return false;
        var items = ReadFile(path);
        var before = items.Count;
        items.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (items.Count == before) return false;
        WriteFile(path, items);
        return true;
    }
}

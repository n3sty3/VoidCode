using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VoidCode.Agent.Providers;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// Builds an exact, token-estimated breakdown of everything that will be sent on the
/// Next agent turn (system prompt, project rules, active editor/selection, attached items, history).
/// </summary>
public sealed class ContextInspectorItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("tokens")]
    public int Tokens { get; set; }

    [JsonPropertyName("removable")]
    public bool Removable { get; set; }
}

public sealed class ContextInspectorSnapshot
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; set; }

    [JsonPropertyName("inputBudget")]
    public int InputBudget { get; set; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("percentUsed")]
    public double PercentUsed { get; set; }

    [JsonPropertyName("willCompact")]
    public bool WillCompact { get; set; }

    [JsonPropertyName("items")]
    public List<ContextInspectorItem> Items { get; set; } = new();
}

internal static class ContextInspector
{
    internal static ContextInspectorSnapshot Inspect(
        string workspacePath,
        AppSettings settings,
        JsonObject? editorContext,
        JsonArray? history,
        JsonArray? attachments,
        string? promptDraft,
        string? modeOverride)
    {
        var items = new List<ContextInspectorItem>();
        var isPlan = string.Equals(modeOverride ?? settings.AgentMode, "plan", StringComparison.OrdinalIgnoreCase);

        // 1. Base system prompt
        var baseSys = Prompts.SystemPrompt(settings, workspacePath, isPlan).ToString();
        var sysTokens = TokenEstimator.EstimateText(baseSys);
        items.Add(new ContextInspectorItem
        {
            Id = "system",
            Category = "system",
            Label = "System prompt",
            Detail = isPlan ? "Plan mode (read-only)" : $"Act mode ({AgentPolicy.ResolveAuthorityMode(settings)})",
            Tokens = sysTokens,
            Removable = false
        });

        // 2. Project rules (AGENTS.md + .voidcode/rules/*.md)
        var rulesBlock = ContextBuilder.LoadProjectRules(workspacePath);
        if (!string.IsNullOrEmpty(rulesBlock))
        {
            var ruleNames = ListLoadedRuleNames(workspacePath);
            items.Add(new ContextInspectorItem
            {
                Id = "project-rules",
                Category = "rules",
                Label = "Project rules",
                Detail = ruleNames.Count > 0 ? string.Join(", ", ruleNames) : "AGENTS.md / .voidcode/rules",
                Tokens = TokenEstimator.EstimateText(rulesBlock),
                Removable = false
            });
        }

        // 3. Editor context (active file, selection, open/workspace file list)
        var editorBlock = ContextBuilder.BuildEditorContextBlock(editorContext);
        if (!string.IsNullOrEmpty(editorBlock))
        {
            var activePath = editorContext?["activePath"]?.GetValue<string>() ?? "";
            var label = string.IsNullOrEmpty(activePath)
                ? "Editor & workspace context"
                : $"Active editor ({Path.GetFileName(activePath)})";
            var openCount = (editorContext?["openFiles"] as JsonArray)?.Count ?? 0;
            var wsCount = (editorContext?["workspaceFiles"] as JsonArray)?.Count ?? 0;
            items.Add(new ContextInspectorItem
            {
                Id = "editor-context",
                Category = "editor",
                Label = label,
                Detail = $"{openCount} open tab{(openCount == 1 ? "" : "s")}, {Math.Min(wsCount, ContextBuilder.MaxWorkspaceFiles)} listed files",
                Tokens = TokenEstimator.EstimateText(editorBlock),
                Removable = false
            });
        }

        // 4. User-attached chips (@file, @problems, @git, @terminal, @selection)
        if (attachments != null)
        {
            foreach (var node in attachments)
            {
                if (node is not JsonObject att) continue;
                var id = att["id"]?.GetValue<string>() ?? "";
                var type = att["type"]?.GetValue<string>() ?? "attachment";
                var label = att["label"]?.GetValue<string>() ?? id;
                var path = att["path"]?.GetValue<string>() ?? "";
                var content = att["content"]?.GetValue<string>() ?? "";

                int tok;
                if (!string.IsNullOrEmpty(content))
                {
                    tok = TokenEstimator.EstimateText(content);
                }
                else if (type == "file" && !string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    tok = EstimateAttachedFileTokens(path);
                }
                else
                {
                    tok = Math.Max(20, TokenEstimator.EstimateText(label));
                }

                items.Add(new ContextInspectorItem
                {
                    Id = id,
                    Category = "attachment",
                    Label = label,
                    Detail = string.IsNullOrEmpty(path) ? $"@{type}" : path,
                    Tokens = tok,
                    Removable = true
                });
            }
        }

        // 5. Chat history
        int historyMsgCount = 0;
        int historyTokens = 0;
        if (history != null)
        {
            foreach (var node in history)
            {
                if (node is JsonObject msg)
                {
                    historyMsgCount++;
                    historyTokens += TokenEstimator.EstimateMessage(msg);
                }
            }
        }
        if (historyMsgCount > 0)
        {
            items.Add(new ContextInspectorItem
            {
                Id = "history",
                Category = "history",
                Label = "Conversation history",
                Detail = $"{historyMsgCount} message{(historyMsgCount == 1 ? "" : "s")}",
                Tokens = historyTokens,
                Removable = true
            });
        }

        // 6. Current prompt draft
        if (!string.IsNullOrWhiteSpace(promptDraft))
        {
            items.Add(new ContextInspectorItem
            {
                Id = "prompt-draft",
                Category = "draft",
                Label = "Current prompt draft",
                Detail = $"{promptDraft.Length:N0} chars",
                Tokens = TokenEstimator.EstimateText(promptDraft),
                Removable = false
            });
        }

        var caps = ProviderCapabilityCache.Shared.Get(settings);
        var inputBudget = ContextBudget.ComputeInputBudget(caps.ContextWindow, settings.MaxTokens);
        var totalTokens = items.Sum(i => i.Tokens);
        var percent = inputBudget > 0 ? Math.Round(totalTokens * 100.0 / inputBudget, 1) : 0.0;

        return new ContextInspectorSnapshot
        {
            Model = settings.Model ?? "",
            Provider = settings.Provider ?? "",
            ContextWindow = caps.ContextWindow,
            InputBudget = inputBudget,
            TotalTokens = totalTokens,
            PercentUsed = percent,
            WillCompact = totalTokens > (int)(inputBudget * 0.80),
            Items = items
        };
    }

    private static int EstimateAttachedFileTokens(string fullPath)
    {
        try
        {
            if (FileSystemService.IsImageFile(fullPath)) return 1000;
            if (FileSystemService.IsBinaryFile(fullPath)) return 400;
            var text = FileSystemService.ReadFileWithLineNumbers(fullPath, 1, 300);
            return TokenEstimator.EstimateText(text);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "ContextInspector.EstimateAttachedFileTokens");
            return 0;
        }
    }

    private static List<string> ListLoadedRuleNames(string workspacePath)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(workspacePath)) return names;
        if (File.Exists(Path.Combine(workspacePath, "AGENTS.md"))) names.Add("AGENTS.md");
        var rulesDir = Path.Combine(workspacePath, ".voidcode", "rules");
        if (Directory.Exists(rulesDir))
        {
            try
            {
                foreach (var f in Directory.GetFiles(rulesDir, "*.md").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add($".voidcode/rules/{Path.GetFileName(f)}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "ContextInspector.ListLoadedRuleNames");
            }
        }
        return names;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Agent.Providers;
using VoidCode.Agent.Tools;

namespace VoidCode.Services;

public class StreamedToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public StringBuilder Arguments { get; set; } = new();
}

public class AgentEngine
{
    // Exposed for tests: plan mode must never allow a mutating or process-running tool.
    public static bool IsAllowedInPlanMode(string toolName) => ToolCatalog.IsAllowedInPlanMode(toolName);

    public static void ResolveToolApproval(string toolId, bool approved, bool approveTurn = false, string? reason = null) => AgentLoop.ResolveToolApproval(toolId, approved, approveTurn, reason);

    public static Task<(bool success, string message, List<string> models)> TestConnectionAsync(AppSettings settings)
        => ProviderFactory.Create(settings).TestConnectionAsync(settings);

    public static Task RunAgentLoopAsync(
        string userPrompt,
        List<JsonObject> conversationHistory,
        string workspacePath,
        AppSettings settings,
        Action<string, object> emitEvent,
        CancellationToken cancellationToken = default,
        JsonObject? editorContext = null,
        string? modeOverride = null)
        => AgentLoop.RunAsync(userPrompt, conversationHistory, workspacePath, settings, emitEvent, cancellationToken, editorContext, modeOverride);

    public static async Task RunInlineAssistAsync(
        string instruction,
        string selectedCode,
        string fileContent,
        string relativePath,
        string language,
        AppSettings settings,
        Action<string, object> emitEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            emitEvent("inline_assist_status", new { status = "Connecting to model..." });

            var surroundingContext = ToolOutputPolicy.Truncate(fileContent, 8000, "context clipped");
            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"File: {relativePath} ({language})");
            if (!string.IsNullOrWhiteSpace(surroundingContext))
            {
                userPrompt.AppendLine("\n--- ENCLOSING FILE CONTEXT ---");
                userPrompt.AppendLine(surroundingContext);
            }
            userPrompt.AppendLine("\n--- SELECTED CODE TO TRANSFORM ---");
            userPrompt.AppendLine(string.IsNullOrEmpty(selectedCode) ? "// (Cursor position: generate new code here)" : selectedCode);
            userPrompt.AppendLine("\n--- INSTRUCTION ---");
            userPrompt.AppendLine(instruction);

            var request = new ChatRequest
            {
                Provider = settings.Provider,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                Model = settings.Model,
                Temperature = 0.2,
                MaxTokens = 4096,
                Stream = true,
                Messages = new List<JsonObject>
                {
                    new() { ["role"] = "system", ["content"] = Prompts.InlineAssist },
                    new() { ["role"] = "user", ["content"] = userPrompt.ToString() }
                }
            };

            var provider = ProviderFactory.Create(settings);
            var contentBuilder = new StringBuilder();
            bool generatingAnnounced = false;

            await foreach (var evt in provider.StreamAsync(request, cancellationToken))
            {
                switch (evt)
                {
                    case ChatStatusEvent s:
                        emitEvent("inline_assist_status", new { status = s.Status });
                        break;
                    case ChatErrorEvent err:
                        emitEvent("inline_assist_error", new { message = err.Message });
                        return;
                    case ChatContentDelta c:
                        if (!generatingAnnounced)
                        {
                            generatingAnnounced = true;
                            emitEvent("inline_assist_status", new { status = "Generating code..." });
                        }
                        contentBuilder.Append(c.Text);
                        emitEvent("inline_assist_delta", new { delta = c.Text });
                        break;
                }
            }

            var resultText = StripCodeFences(contentBuilder.ToString());
            emitEvent("inline_assist_complete", new
            {
                replacement = resultText,
                original = selectedCode
            });
        }
        catch (OperationCanceledException)
        {
            emitEvent("inline_assist_cancelled", new { });
        }
        catch (Exception ex)
        {
            emitEvent("inline_assist_error", new { message = ex.Message });
        }
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline != -1) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3].TrimEnd();
        }
        return trimmed;
    }

    public static async Task<string> GenerateCommitMessageAsync(
        string workspacePath,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var (_, stagedDiff, _) = await GitService.RunGitAsync(workspacePath, "diff --cached");
        string diffToAnalyze = stagedDiff;
        if (string.IsNullOrWhiteSpace(diffToAnalyze))
        {
            var (_, workingDiff, _) = await GitService.RunGitAsync(workspacePath, "diff");
            diffToAnalyze = workingDiff;
        }

        if (string.IsNullOrWhiteSpace(diffToAnalyze))
        {
            var status = await GitService.GetStatusAsync(workspacePath);
            if (status.WorkingChanges.Count > 0 || status.StagedChanges.Count > 0)
            {
                var files = status.StagedChanges.Concat(status.WorkingChanges).Select(c => $"{c.Status} {c.Path}");
                diffToAnalyze = "Changed files:\n" + string.Join("\n", files);
            }
            else
            {
                return "chore: update project files";
            }
        }

        diffToAnalyze = ToolOutputPolicy.Truncate(diffToAnalyze, 12000, "diff truncated");

        var request = new ChatRequest
        {
            Provider = settings.Provider,
            BaseUrl = settings.BaseUrl,
            ApiKey = settings.ApiKey,
            Model = settings.Model,
            Temperature = 0.2,
            MaxTokens = 100,
            Stream = false,
            Messages = new List<JsonObject>
            {
                new() { ["role"] = "system", ["content"] = Prompts.CommitMessage },
                new() { ["role"] = "user", ["content"] = $"Generate a Conventional Commit message for this diff:\n\n{diffToAnalyze}" }
            }
        };

        var text = await ProviderFactory.Create(settings).CompleteAsync(request, cancellationToken);
        return CleanCommitText(string.IsNullOrWhiteSpace(text) ? "chore: update files" : text);
    }

    private static string CleanCommitText(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = cleaned.IndexOf('\n');
            if (firstNl != -1) cleaned = cleaned[(firstNl + 1)..];
            if (cleaned.EndsWith("```", StringComparison.Ordinal)) cleaned = cleaned[..^3];
        }
        return cleaned.Trim().Trim('`', '"', '\'');
    }
}

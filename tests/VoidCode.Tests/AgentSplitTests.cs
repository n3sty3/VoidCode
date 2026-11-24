using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VoidCode.Agent;
using VoidCode.Agent.Tools;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The agent split moved four units behind `internal` seams, so this file pins what each
// One promises - above all that plan mode can never offer the model a mutating tool, and that the
// Approval gate and the tool schema cannot drift apart.
public class AgentSplitTests
{
    private static List<string> ToolNames(bool planOnly = false) => ToolCatalog.Definitions(planOnly)
        .Select(node => node?["function"]?["name"]?.GetValue<string>() ?? "")
        .ToList();

    [Fact]
    public void ToolCatalog_OffersEveryToolExactlyOnce_AndThePromptAnnouncesAllOfThem()
    {
        var expected = new[]
        {
            "read_file", "view_image", "write_file", "patch_file", "multi_patch_file", "delete_file", "move_file",
            "list_dir", "glob_files", "search_code", "search_codebase", "run_command", "git_status_diff",
            "get_diagnostics", "get_lsp_diagnostics", "inspect_binary", "web_fetch", "save_memory", "delete_memory",
            "delegate_research"
        };
        var names = ToolNames();

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));

        // The system prompt names the tools too; a tool added to the catalog but not here would be one
        // The model is never told it has.
        var announced = Prompts.SystemPrompt(new AppSettings { SystemPrompt = "" }, "workspace", isPlanMode: false)
            .ToString()
            .Split("Available Tools: ")[1]
            .Split('\n')[0]
            .Split(',')
            .Select(name => name.Trim())
            .ToList();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), announced.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ToolCatalog_InPlanModeOffersOnlyReadOnlyTools()
    {
        var planNames = ToolNames(planOnly: true);

        Assert.NotEmpty(planNames);
        Assert.All(planNames, name => Assert.True(ToolCatalog.IsAllowedInPlanMode(name), $"{name} leaked into plan mode"));
        Assert.Contains("read_file", planNames);
        Assert.Contains("search_codebase", planNames);
        Assert.DoesNotContain("write_file", planNames);
        Assert.DoesNotContain("patch_file", planNames);
        Assert.DoesNotContain("run_command", planNames);
        Assert.DoesNotContain("web_fetch", planNames);
    }

    [Fact]
    public void ToolCatalog_GateAndSchemaCannotDrift()
    {
        var names = ToolNames();

        // Every approval-gated tool must actually be offered, or the gate silently guards nothing.
        Assert.All(ToolCatalog.ApprovalRequiredTools, gated => Assert.Contains(gated, names));

        // A read-only tool must never need approval, and vice versa: the two sets are disjoint.
        Assert.Empty(ToolCatalog.PlanModeTools.Intersect(ToolCatalog.ApprovalRequiredTools, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToolExecutor_ResolvePath_RootsRelativeAndKeepsAbsolute()
    {
        var root = Path.Combine(Path.GetTempPath(), "voidcode-agent-root");

        var absPath = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/hosts";
        Assert.Equal(Path.Combine(root, "src", "Program.cs"), ToolExecutor.ResolvePath(root, Path.Combine("src", "Program.cs")));
        Assert.Equal(absPath, ToolExecutor.ResolvePath(root, absPath));
        Assert.Equal(root, ToolExecutor.ResolvePath(root, "  "));
    }

    [Fact]
    public void ToolExecutor_OutsideWorkspaceCheck_ReviewsEscapesOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "voidcode-agent-root");
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(ToolExecutor.AccessesOutsideWorkspace("read_file", "{\"path\":\"Program.cs\"}", root));
            Assert.True(ToolExecutor.AccessesOutsideWorkspace("read_file", "{\"path\":\"..\\\\escape.txt\"}", root));
            Assert.True(ToolExecutor.AccessesOutsideWorkspace("list_dir", "{\"path\":\"C:\\\\Windows\"}", root));

            // Tools that take no path are never gated by this check, and a broken payload fails closed.
            Assert.False(ToolExecutor.AccessesOutsideWorkspace("run_command", "{\"command\":\"ls\"}", root));
            Assert.True(ToolExecutor.AccessesOutsideWorkspace("read_file", "not-json", root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void ContextBuilder_ClipsTheActiveFileAndCapsTheListing()
    {
        var context = new JsonObject
        {
            ["activePath"] = "Program.cs",
            ["language"] = "csharp",
            ["content"] = new string('x', ContextBuilder.MaxContextFileChars + 500),
            ["workspaceFiles"] = new JsonArray(Enumerable.Range(0, ContextBuilder.MaxWorkspaceFiles + 20)
                .Select(i => (JsonNode)JsonValue.Create($"file{i}.cs"))
                .ToArray())
        };

        var block = ContextBuilder.BuildEditorContextBlock(context);

        Assert.Contains("truncated - use read_file for the rest", block);
        Assert.Contains($"file{ContextBuilder.MaxWorkspaceFiles - 1}.cs", block);
        Assert.DoesNotContain($"file{ContextBuilder.MaxWorkspaceFiles}.cs", block);
        Assert.Equal("", ContextBuilder.BuildEditorContextBlock(null));
    }

    [Fact]
    public void Prompts_SystemPrompt_CarriesTheWorkspaceAndPlanRulesOnlyInPlanMode()
    {
        var settings = new AppSettings { SystemPrompt = "BASE RULES" };
        const string workspace = @"C:\work\project";

        var act = Prompts.SystemPrompt(settings, workspace, isPlanMode: false).ToString();
        var plan = Prompts.SystemPrompt(settings, workspace, isPlanMode: true).ToString();

        Assert.StartsWith("BASE RULES", act, StringComparison.Ordinal);
        Assert.Contains(workspace, act);
        Assert.Contains("Available Tools:", act);
        Assert.DoesNotContain("PLAN MODE", act);

        Assert.Contains("=== PLAN MODE (READ-ONLY) ===", plan);
        Assert.Contains("Do NOT modify files", plan);
    }

    [Fact]
    public void Prompts_KeepTheirHardConstraints()
    {
        Assert.Contains("OUTPUT RULES:", Prompts.InlineAssist);
        Assert.Contains("Return ONLY the replacement code", Prompts.InlineAssist);
        Assert.Contains("First line MUST be <= 72 characters.", Prompts.CommitMessage);
        Assert.Contains("Conventional Commit", Prompts.CommitMessage);
    }
}
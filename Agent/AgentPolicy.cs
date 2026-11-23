using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using VoidCode.Agent.Tools;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// Central authority decision engine for agent tools.
/// Supports three modes:
///   - "review": read-only workspace tools auto-run; mutations, commands, network & outside-workspace ask.
///   - "workspace_auto": in-workspace file mutations auto-run; commands, network & outside-workspace ask.
///   - "full_system": all tools auto-run (unless WorkspaceTrust restricts the folder).
/// After untrusted web content is fetched (web_fetch), mutating/command tools escalate to require
/// Explicit operator approval in every mode except "full_system".
/// </summary>
internal static class AgentPolicy
{
    internal const string ModeReview = "review";
    internal const string ModeWorkspaceAuto = "workspace_auto";
    internal const string ModeFullSystem = "full_system";

    private static readonly HashSet<string> FileMutationTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "patch_file",
        "delete_file",
        "move_file"
    };

    internal static string ResolveAuthorityMode(AppSettings settings)
    {
        if (WorkspaceTrustService.ForcesReview)
            return ModeReview;

        if (settings.AutoApproveTools ||
            string.Equals(settings.AuthorityMode, ModeFullSystem, StringComparison.OrdinalIgnoreCase))
        {
            return ModeFullSystem;
        }

        if (string.Equals(settings.AuthorityMode, ModeWorkspaceAuto, StringComparison.OrdinalIgnoreCase))
        {
            return ModeWorkspaceAuto;
        }

        return ModeReview;
    }

    internal static bool RequiresApproval(
        string toolName,
        string argumentsJson,
        string workspacePath,
        AppSettings settings,
        bool taintedByUntrustedWeb)
    {
        var mode = ResolveAuthorityMode(settings);
        if (mode == ModeFullSystem)
            return false;

        var outsideRead = ToolExecutor.AccessesOutsideWorkspace(toolName, argumentsJson, workspacePath);
        if (outsideRead)
            return true;

        var isGatedTool = ToolCatalog.ApprovalRequiredTools.Contains(toolName)
            || Mcp.McpManager.IsMcpTool(toolName);
        if (!isGatedTool)
            return false;

        if (mode == ModeWorkspaceAuto)
        {
            // Once untrusted web content entered the context, require approval before any mutation.
            if (taintedByUntrustedWeb)
                return true;

            // Only strictly in-workspace file mutations auto-run; commands/network/diagnostics/escapes still ask.
            if (FileMutationTools.Contains(toolName) && AreAllMutationPathsInsideWorkspace(toolName, argumentsJson, workspacePath))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreAllMutationPathsInsideWorkspace(string toolName, string argumentsJson, string workspacePath)
    {
        try
        {
            var args = JsonNode.Parse(argumentsJson);
            if (args == null) return false;

            if (string.Equals(toolName, "move_file", StringComparison.OrdinalIgnoreCase))
            {
                var src = args["source_path"]?.GetValue<string>() ?? "";
                var dst = args["destination_path"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst)) return false;
                return PathPolicy.IsInsideWorkspace(workspacePath, ToolExecutor.ResolvePath(workspacePath, src))
                    && PathPolicy.IsInsideWorkspace(workspacePath, ToolExecutor.ResolvePath(workspacePath, dst));
            }

            var relPath = args["path"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(relPath)) return false;
            return PathPolicy.IsInsideWorkspace(workspacePath, ToolExecutor.ResolvePath(workspacePath, relPath));
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "AgentPolicy.AreAllMutationPathsInsideWorkspace");
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

public static class V110Routes
{
    public const string MemoryList = "memory_list";
    public const string MemorySave = "memory_save";
    public const string MemoryDelete = "memory_delete";
    public const string BranchReviewRun = "branch_review_run";
    public const string HttpClientSend = "http_client_send";
    public const string IdeImportDetect = "ide_import_detect";
    public const string IdeImportApply = "ide_import_apply";
    public const string DbDiscover = "db_discover";
    public const string DbSchema = "db_schema";
    public const string DbQuery = "db_query";
    public const string AgentSteer = "agent_steer";
    public const string AgentClearTrace = "agent_clear_trace";
    public const string GitCloneRepo = "git_clone_repo";
    public const string GitCommitGraph = "git_commit_graph";
    public const string GitCommitShow = "git_commit_show";
    public const string GitStashList = "git_graph_stash_list";
    public const string GitStashPush = "git_graph_stash_push";
    public const string GitStashPop = "git_graph_stash_pop";
    public const string GitHunkApply = "git_hunk_apply";
    public const string SymbolIndexQuery = "symbol_index_query";
    public const string SymbolGraphFind = "symbol_graph_find";
}

/// <summary>
/// Typed envelope bridge handlers for VoidCode v1.1.0 flagship features:
/// Agent Memory Bank, AI Pre-PR Branch Reviewer, API/HTTP Client, VS Code/Cursor Importer,
/// and SQLite Database Explorer.
/// </summary>
public sealed class V110Handlers
{
    private readonly Func<string?> _workspace;
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _setSettings;
    private readonly Action<string, object> _send;
    private readonly Action<string>? _activateWorkspace;

    public V110Handlers(
        Func<string?> workspace,
        Func<AppSettings> settings,
        Action<AppSettings> setSettings,
        Action<string, object> send,
        Action<string>? activateWorkspace = null)
    {
        _workspace = workspace;
        _settings = settings;
        _setSettings = setSettings;
        _send = send;
        _activateWorkspace = activateWorkspace;
    }

    public sealed class EmptyPayload { }

    public sealed class MemorySaveRequest
    {
        public string? Scope { get; init; }
        public string? Category { get; init; }
        public required string Content { get; init; }
    }

    public sealed class MemoryDeleteRequest
    {
        public required string Id { get; init; }
    }

    public sealed class BranchReviewRequest
    {
        public string? BaseRef { get; init; }
    }

    public sealed class IdeImportApplyRequest
    {
        public required string SourceId { get; init; }
    }

    public sealed class DbPathRequest
    {
        public required string Path { get; init; }
    }

    public sealed class DbQueryRequest
    {
        public required string Path { get; init; }
        public required string Sql { get; init; }
        public int MaxRows { get; init; } = 200;
    }

    public sealed class AgentSteerRequest
    {
        public required string Message { get; init; }
    }

    public sealed class AgentClearTraceRequest
    {
        public string? SessionId { get; init; }
    }

    public sealed class SimpleOkResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("ok")]
        public bool Ok { get; init; } = true;
    }

    public sealed class GitCloneRequest
    {
        public required string Url { get; init; }
        public string? DestinationDir { get; init; }
        public string? FolderName { get; init; }
    }

    public sealed class GitCommitGraphRequest
    {
        public int MaxCount { get; init; } = 60;
    }

    public sealed class GitCommitShowRequest
    {
        public required string Hash { get; init; }
    }

    public sealed class GitStashPushRequest
    {
        public string? Message { get; init; }
    }

    public sealed class GitStashPopRequest
    {
        public string? StashRef { get; init; }
    }

    public sealed class GitHunkApplyRequest
    {
        public required string Patch { get; init; }
        public bool Cached { get; init; } = true;
        public bool Reverse { get; init; }
    }

    public sealed class SymbolIndexQueryRequest
    {
        public string? Query { get; init; }
        public int Limit { get; init; } = 60;
    }

    public sealed class SymbolGraphFindRequest
    {
        public required string Symbol { get; init; }
        public int MaxReferences { get; init; } = 35;
    }

    public void Register(BridgeRouter router)
    {
        router.Register<EmptyPayload, List<MemoryItem>>(V110Routes.MemoryList, MemoryListAsync, envelope: true);
        router.Register<MemorySaveRequest, MemoryActionResult>(V110Routes.MemorySave, MemorySaveAsync, envelope: true);
        router.Register<MemoryDeleteRequest, MemoryActionResult>(V110Routes.MemoryDelete, MemoryDeleteAsync, envelope: true);
        router.Register<BranchReviewRequest, BranchReviewReport>(V110Routes.BranchReviewRun, BranchReviewRunAsync, envelope: true);
        router.Register<HttpRunRequest, HttpRunResponse>(V110Routes.HttpClientSend, HttpClientSendAsync, envelope: true);
        router.Register<EmptyPayload, List<DetectedIdeSource>>(V110Routes.IdeImportDetect, IdeImportDetectAsync, envelope: true);
        router.Register<IdeImportApplyRequest, IdeImportResult>(V110Routes.IdeImportApply, IdeImportApplyAsync, envelope: true);
        router.Register<EmptyPayload, List<DiscoveredDatabase>>(V110Routes.DbDiscover, DbDiscoverAsync, envelope: true);
        router.Register<DbPathRequest, DbSchemaReport>(V110Routes.DbSchema, DbSchemaAsync, envelope: true);
        router.Register<DbQueryRequest, DbQueryResult>(V110Routes.DbQuery, DbQueryAsync, envelope: true);
        router.Register<AgentSteerRequest, SimpleOkResult>(V110Routes.AgentSteer, AgentSteerAsync, envelope: true);
        router.Register<AgentClearTraceRequest, SimpleOkResult>(V110Routes.AgentClearTrace, AgentClearTraceAsync, envelope: true);
        router.Register<GitCloneRequest, GitPowerResult>(V110Routes.GitCloneRepo, GitCloneRepoAsync, envelope: true);
        router.Register<GitCommitGraphRequest, List<GitGraphCommit>>(V110Routes.GitCommitGraph, GitCommitGraphAsync, envelope: true);
        router.Register<GitCommitShowRequest, GitPowerResult>(V110Routes.GitCommitShow, GitCommitShowAsync, envelope: true);
        router.Register<EmptyPayload, List<GitStashItem>>(V110Routes.GitStashList, GitStashListAsync, envelope: true);
        router.Register<GitStashPushRequest, GitPowerResult>(V110Routes.GitStashPush, GitStashPushAsync, envelope: true);
        router.Register<GitStashPopRequest, GitPowerResult>(V110Routes.GitStashPop, GitStashPopAsync, envelope: true);
        router.Register<GitHunkApplyRequest, GitPowerResult>(V110Routes.GitHunkApply, GitHunkApplyAsync, envelope: true);
        router.Register<SymbolIndexQueryRequest, List<SymbolMatch>>(V110Routes.SymbolIndexQuery, SymbolIndexQueryAsync, envelope: true);
        router.Register<SymbolGraphFindRequest, SymbolGraphReport>(V110Routes.SymbolGraphFind, SymbolGraphFindAsync, envelope: true);
    }

    private Task<List<MemoryItem>> MemoryListAsync(EmptyPayload _, BridgeContext __)
        => Task.FromResult(AgentMemoryService.List(_workspace()));

    private Task<MemoryActionResult> MemorySaveAsync(MemorySaveRequest req, BridgeContext _)
        => Task.FromResult(AgentMemoryService.Save(_workspace(), req.Scope, req.Category, req.Content));

    private Task<MemoryActionResult> MemoryDeleteAsync(MemoryDeleteRequest req, BridgeContext _)
        => Task.FromResult(AgentMemoryService.Delete(_workspace(), req.Id));

    private Task<BranchReviewReport> BranchReviewRunAsync(BranchReviewRequest req, BridgeContext _)
    {
        var ws = _workspace() ?? "";
        var effective = WorkspaceSettingsService.Resolve(_settings(), ws).Effective;
        return BranchReviewService.ReviewAsync(ws, effective, req.BaseRef);
    }

    private Task<HttpRunResponse> HttpClientSendAsync(HttpRunRequest req, BridgeContext _)
        => HttpClientRunnerService.SendAsync(req);

    private Task<List<DetectedIdeSource>> IdeImportDetectAsync(EmptyPayload _, BridgeContext __)
        => Task.Run(() => IdeImportService.DetectSources());

    private Task<IdeImportResult> IdeImportApplyAsync(IdeImportApplyRequest req, BridgeContext _)
    {
        return Task.Run(() =>
        {
            var current = _settings();
            var result = IdeImportService.ImportFromSource(req.SourceId, current);
            if (result.Ok && result.Settings != null)
            {
                _setSettings(result.Settings);
                var scoped = WorkspaceSettingsService.Resolve(result.Settings, _workspace());
                _send("settings_saved", new { settings = scoped.Effective, settingScopes = scoped.Scopes });
            }
            return result;
        });
    }

    private Task<List<DiscoveredDatabase>> DbDiscoverAsync(EmptyPayload _, BridgeContext __)
        => Task.Run(() => DatabaseExplorerService.Discover(_workspace()));

    private Task<DbSchemaReport> DbSchemaAsync(DbPathRequest req, BridgeContext _)
    {
        var fullPath = ResolveDbPath(req.Path);
        return Task.Run(() => DatabaseExplorerService.InspectSchema(fullPath));
    }

    private Task<DbQueryResult> DbQueryAsync(DbQueryRequest req, BridgeContext _)
    {
        var fullPath = ResolveDbPath(req.Path);
        return Task.Run(() => DatabaseExplorerService.ExecuteQuery(fullPath, req.Sql, req.MaxRows));
    }

    private Task<SimpleOkResult> AgentSteerAsync(AgentSteerRequest req, BridgeContext _)
    {
        VoidCode.Agent.AgentSessionMemory.EnqueueSteer(req.Message);
        return Task.FromResult(new SimpleOkResult { Ok = true });
    }

    private Task<SimpleOkResult> AgentClearTraceAsync(AgentClearTraceRequest req, BridgeContext __)
    {
        if (!string.IsNullOrWhiteSpace(req.SessionId))
            VoidCode.Agent.AgentSessionMemory.ClearSession(VoidCode.Agent.AgentSessionMemory.ResolveSessionKey(_workspace(), req.SessionId));
        VoidCode.Agent.AgentSessionMemory.ClearWorkspace(_workspace());
        VoidCode.Agent.AgentSessionMemory.ClearSteers();
        return Task.FromResult(new SimpleOkResult { Ok = true });
    }

    private async Task<GitPowerResult> GitCloneRepoAsync(GitCloneRequest req, BridgeContext _)
    {
        var result = await GitPowerService.CloneAsync(req.Url, req.DestinationDir ?? "", req.FolderName);
        if (result.Ok && !string.IsNullOrEmpty(result.Path))
        {
            _activateWorkspace?.Invoke(result.Path);
        }
        return result;
    }

    private Task<List<GitGraphCommit>> GitCommitGraphAsync(GitCommitGraphRequest req, BridgeContext _)
        => GitPowerService.GetCommitGraphAsync(_workspace() ?? "", req.MaxCount);

    private Task<GitPowerResult> GitCommitShowAsync(GitCommitShowRequest req, BridgeContext _)
        => GitPowerService.ShowCommitAsync(_workspace() ?? "", req.Hash);

    private Task<List<GitStashItem>> GitStashListAsync(EmptyPayload _, BridgeContext __)
        => GitPowerService.ListStashesAsync(_workspace() ?? "");

    private Task<GitPowerResult> GitStashPushAsync(GitStashPushRequest req, BridgeContext _)
        => GitPowerService.PushStashAsync(_workspace() ?? "", req.Message);

    private Task<GitPowerResult> GitStashPopAsync(GitStashPopRequest req, BridgeContext _)
        => GitPowerService.PopStashAsync(_workspace() ?? "", req.StashRef);

    private Task<GitPowerResult> GitHunkApplyAsync(GitHunkApplyRequest req, BridgeContext _)
        => GitPowerService.ApplyHunkPatchAsync(_workspace() ?? "", req.Patch, req.Cached, req.Reverse);

    private Task<List<SymbolMatch>> SymbolIndexQueryAsync(SymbolIndexQueryRequest req, BridgeContext _)
        => Task.Run(() => SymbolGraphService.QuerySymbols(_workspace(), req.Query, req.Limit));

    private Task<SymbolGraphReport> SymbolGraphFindAsync(SymbolGraphFindRequest req, BridgeContext _)
        => Task.Run(() => SymbolGraphService.FindSymbolGraph(_workspace(), req.Symbol, req.MaxReferences));

    private string ResolveDbPath(string rawPath)
    {
        var clean = (rawPath ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return "";
        if (Path.IsPathRooted(clean)) return clean;
        var ws = _workspace();
        return string.IsNullOrEmpty(ws) ? clean : Path.Combine(ws, clean);
    }
}

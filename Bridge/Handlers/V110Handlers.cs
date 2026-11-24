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

    public V110Handlers(
        Func<string?> workspace,
        Func<AppSettings> settings,
        Action<AppSettings> setSettings,
        Action<string, object> send)
    {
        _workspace = workspace;
        _settings = settings;
        _setSettings = setSettings;
        _send = send;
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

    private string ResolveDbPath(string rawPath)
    {
        var clean = (rawPath ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return "";
        if (Path.IsPathRooted(clean)) return clean;
        var ws = _workspace();
        return string.IsNullOrEmpty(ws) ? clean : Path.Combine(ws, clean);
    }
}

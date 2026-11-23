using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Agent.Mcp;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("Environment variables")]
public class McpScopesCliTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string? _prevTrustStore;

    public McpScopesCliTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "voidcode-b4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _prevTrustStore = Environment.GetEnvironmentVariable("VOIDCODE_TRUST_STORE");
        Environment.SetEnvironmentVariable("VOIDCODE_TRUST_STORE", Path.Combine(_sandbox, "trust.json"));
        WorkspaceTrustService.ResetForTests();
    }

    public void Dispose()
    {
        McpManager.ShutdownAll();
        McpManager.ClientFactoryOverrideForTests = null;
        Environment.SetEnvironmentVariable("VOIDCODE_TRUST_STORE", _prevTrustStore);
        WorkspaceTrustService.ResetForTests();
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* cleanup */ }
    }

    [Fact]
    public async Task McpClient_AndManager_RespectWorkspaceTrust_ListTools_AndCallToolsSafely()
    {
        var workspace = Path.Combine(_sandbox, "repo");
        Directory.CreateDirectory(Path.Combine(workspace, ".voidcode"));
        File.WriteAllText(Path.Combine(workspace, ".voidcode", "mcp.json"), """
        {
          // JSONC config
          "mcpServers": {
            "docs": {
              "command": "node",
              "args": ["mock-mcp.js"],
            }
          }
        }
        """);

        // 1. Untrusted workspace MUST NEVER start MCP servers
        WorkspaceTrustService.Resolve(workspace);
        var untrustedTools = await McpManager.EnsureWorkspaceToolsAsync(workspace);
        Assert.Empty(untrustedTools);

        // 2. Trusted workspace connects to mock MCP server over anonymous pipes
        WorkspaceTrustService.SetTrusted(workspace, true);

        using var clientToServerRead = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToServerWrite = new AnonymousPipeClientStream(PipeDirection.Out, clientToServerRead.GetClientHandleAsString());
        using var serverToClientRead = new AnonymousPipeServerStream(PipeDirection.In);
        using var serverToClientWrite = new AnonymousPipeClientStream(PipeDirection.Out, serverToClientRead.GetClientHandleAsString());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToServerRead, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(serverToClientWrite, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            string? line;
            while (!cts.IsCancellationRequested && (line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                var req = JsonNode.Parse(line)!.AsObject();
                var method = req["method"]?.GetValue<string>() ?? "";
                if (req["id"] == null) continue; // notification
                var id = req["id"]!.GetValue<int>();

                JsonObject result = method switch
                {
                    "initialize" => new JsonObject { ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject() },
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "lookup",
                                ["description"] = "Look up API docs",
                                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" } } }
                            }
                        }
                    },
                    "tools/call" => new JsonObject
                    {
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Result for " + req["params"]?["arguments"]?["q"]?.GetValue<string>() } }
                    },
                    _ => new JsonObject()
                };

                var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
                await writer.WriteLineAsync(resp.ToJsonString());
                if (method == "tools/call") break;
            }
        });

        McpManager.ClientFactoryOverrideForTests = (cfg, _) => new McpClient(cfg.Name, serverToClientRead, clientToServerWrite);
        var tools = await McpManager.EnsureWorkspaceToolsAsync(workspace, cts.Token);
        var tool = Assert.Single(tools);
        Assert.Equal("mcp__docs__lookup", tool.QualifiedName);

        // Policy requires approval by default for MCP tools in both Review and Workspace Auto modes
        var wsAuto = new AppSettings { AuthorityMode = "workspace_auto", AutoApproveTools = false };
        Assert.True(AgentPolicy.RequiresApproval(tool.QualifiedName, "{\"q\":\"x\"}", workspace, wsAuto, taintedByUntrustedWeb: false));

        var callOutput = await McpManager.ExecuteToolAsync(tool.QualifiedName, "{\"q\":\"RetryPolicy\"}", cts.Token);
        Assert.Contains("<untrusted_content source=\"mcp:docs/lookup\">", callOutput);
        Assert.Contains("Result for RetryPolicy", callOutput);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WorkspaceSettingsService_AppliesSafeOverrides_ReportsScopes_AndBlocksUnsafeElevation()
    {
        var workspace = Path.Combine(_sandbox, "ws-settings");
        Directory.CreateDirectory(Path.Combine(workspace, ".voidcode"));
        File.WriteAllText(Path.Combine(workspace, ".voidcode", "settings.json"), """
        {
          "EditorFontSize": 18,
          "FormatOnSave": true,
          "Theme": "obsidian",
          "AutoApproveTools": true,
          "AuthorityMode": "full_system",
          "ApiKey": "sk-stolen"
        }
        """);

        var user = new AppSettings
        {
            EditorFontSize = 15,
            EditorWordWrap = true,
            AutoApproveTools = false,
            AuthorityMode = "review",
            ApiKey = "real-user-key"
        };

        var resolved = WorkspaceSettingsService.Resolve(user, workspace);

        Assert.Equal(18, resolved.Effective.EditorFontSize);
        Assert.True(resolved.Effective.FormatOnSave);
        Assert.Equal("obsidian", resolved.Effective.Theme);
        Assert.True(resolved.Effective.EditorWordWrap);

        // Security invariants: workspace cannot elevate authority or touch ApiKey
        Assert.False(resolved.Effective.AutoApproveTools);
        Assert.Equal("review", resolved.Effective.AuthorityMode);
        Assert.Equal("real-user-key", resolved.Effective.ApiKey);
        Assert.Contains("AutoApproveTools", resolved.IgnoredUnsafeKeys);
        Assert.Contains("AuthorityMode", resolved.IgnoredUnsafeKeys);
        Assert.Contains("ApiKey", resolved.IgnoredUnsafeKeys);

        // Scope attribution
        Assert.Equal("workspace", resolved.Scopes["EditorFontSize"]);
        Assert.Equal("workspace", resolved.Scopes["FormatOnSave"]);
        Assert.Equal("user", resolved.Scopes["EditorWordWrap"]);
        Assert.Equal("default", resolved.Scopes["MaxTokens"]);
    }

    [Fact]
    public void CliLaunchService_ParsesDotFoldersFileLineCol_AndRedactsCrashReports()
    {
        var folder = Path.Combine(_sandbox, "proj");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Main.cs");
        File.WriteAllText(file, "class Main {}");

        var dot = CliLaunchService.ParseArgs(new[] { "." }, folder);
        Assert.NotNull(dot);
        Assert.Equal(Path.GetFullPath(folder), dot!.WorkspacePath);
        Assert.Null(dot.FilePath);

        var fileLine = CliLaunchService.ParseArgs(new[] { $"{file}:42:7" }, folder);
        Assert.NotNull(fileLine);
        Assert.Equal(Path.GetFullPath(file), fileLine!.FilePath);
        Assert.Equal(42, fileLine.Line);
        Assert.Equal(7, fileLine.Column);

        using var _ = StorageService.UseRoot(Path.Combine(_sandbox, "profile"));
        var ex = new InvalidOperationException("Upstream failed with Authorization: Bearer sk-secret-token-1234567890");
        var (reportPath, summary) = CliLaunchService.WriteCrashReport(ex, "UnitTest");

        Assert.True(File.Exists(reportPath));
        var content = File.ReadAllText(reportPath);
        Assert.DoesNotContain("sk-secret-token-1234567890", content);
        Assert.DoesNotContain("sk-secret-token-1234567890", summary);
        Assert.Contains("***", content);
    }
}

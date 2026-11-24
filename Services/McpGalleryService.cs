using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VoidCode.Agent.Mcp;

namespace VoidCode.Services;

public sealed class McpGalleryItem
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("transport")]
    public string Transport { get; init; } = "stdio";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; init; } = new();

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("envHint")]
    public string EnvHint { get; init; } = "";

    [JsonPropertyName("installed")]
    public bool Installed { get; init; }
}

public sealed class McpGalleryActionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("items")]
    public List<McpGalleryItem> Items { get; init; } = new();
}

/// <summary>
/// Curated 1-click MCP server gallery (stdio &amp; Streamable HTTP/SSE) backed by workspace `.voidcode/mcp.json`.
/// </summary>
public static class McpGalleryService
{
    private static readonly IReadOnlyList<McpGalleryItem> Catalog = new[]
    {
        new McpGalleryItem
        {
            Id = "context7",
            Name = "Context7 Docs",
            Category = "Docs",
            Transport = "http",
            Description = "Live, version-accurate library & framework documentation directly in VoidAgent.",
            Url = "https://mcp.context7.com/mcp"
        },
        new McpGalleryItem
        {
            Id = "github",
            Name = "GitHub",
            Category = "Git & PRs",
            Transport = "stdio",
            Description = "Inspect issues, pull requests, branches, and GitHub Actions runs.",
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-github" },
            EnvHint = "GITHUB_PERSONAL_ACCESS_TOKEN"
        },
        new McpGalleryItem
        {
            Id = "playwright",
            Name = "Playwright Browser",
            Category = "Testing & UI",
            Transport = "stdio",
            Description = "Drive a real browser to test web apps, capture DOM snapshots, and verify UI changes.",
            Command = "npx",
            Args = new() { "-y", "@playwright/mcp@latest" }
        },
        new McpGalleryItem
        {
            Id = "memory",
            Name = "Knowledge Graph Memory",
            Category = "Memory",
            Transport = "stdio",
            Description = "Persistent local entity/relation memory graph shared across VoidAgent sessions.",
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-memory" }
        },
        new McpGalleryItem
        {
            Id = "postgres",
            Name = "PostgreSQL",
            Category = "Database",
            Transport = "stdio",
            Description = "Read-only schema inspection and SQL queries against a PostgreSQL database.",
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-postgres", "postgresql://localhost/mydb" }
        },
        new McpGalleryItem
        {
            Id = "sqlite",
            Name = "SQLite",
            Category = "Database",
            Transport = "stdio",
            Description = "Query and inspect local SQLite databases directly from VoidAgent.",
            Command = "uvx",
            Args = new() { "mcp-server-sqlite", "--db-path", "${workspaceFolder}/app.db" }
        },
        new McpGalleryItem
        {
            Id = "sequential-thinking",
            Name = "Sequential Thinking",
            Category = "Reasoning",
            Transport = "stdio",
            Description = "Structured multi-step architectural reasoning scratchpad for complex refactors.",
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-sequential-thinking" }
        }
    };

    public static List<McpGalleryItem> List(string? workspacePath)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var customItems = new List<McpGalleryItem>();

        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            foreach (var cfg in McpManager.LoadConfig(workspacePath))
            {
                installed.Add(cfg.Name);
                if (!Catalog.Any(c => string.Equals(c.Id, cfg.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    customItems.Add(new McpGalleryItem
                    {
                        Id = cfg.Name,
                        Name = cfg.Name,
                        Category = "Custom",
                        Transport = cfg.IsHttp ? "http" : "stdio",
                        Description = cfg.IsHttp ? cfg.Url : $"{cfg.Command} {string.Join(' ', cfg.Args)}",
                        Command = cfg.Command,
                        Args = cfg.Args,
                        Url = cfg.Url,
                        Installed = true
                    });
                }
            }
        }

        var result = Catalog.Select(c => new McpGalleryItem
        {
            Id = c.Id,
            Name = c.Name,
            Category = c.Category,
            Transport = c.Transport,
            Description = c.Description,
            Command = c.Command,
            Args = new List<string>(c.Args),
            Url = c.Url,
            EnvHint = c.EnvHint,
            Installed = installed.Contains(c.Id)
        }).ToList();

        result.AddRange(customItems);
        return result;
    }

    public static McpGalleryActionResult Install(
        string? workspacePath,
        string serverId,
        string? customUrl = null,
        string? customCommand = null,
        List<string>? customArgs = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return new McpGalleryActionResult { Ok = false, Message = "Open a workspace folder first to install MCP servers." };
        }
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return new McpGalleryActionResult { Ok = false, Message = "Server id is required." };
        }

        var preset = Catalog.FirstOrDefault(c => string.Equals(c.Id, serverId, StringComparison.OrdinalIgnoreCase));
        JsonObject entry;
        if (preset != null && string.IsNullOrWhiteSpace(customUrl) && string.IsNullOrWhiteSpace(customCommand))
        {
            if (preset.Transport == "http")
            {
                entry = new JsonObject { ["url"] = preset.Url };
            }
            else
            {
                var argsArr = new JsonArray();
                foreach (var a in preset.Args) argsArr.Add(a);
                entry = new JsonObject { ["command"] = preset.Command, ["args"] = argsArr };
            }
        }
        else if (!string.IsNullOrWhiteSpace(customUrl))
        {
            entry = new JsonObject { ["url"] = customUrl.Trim() };
        }
        else if (!string.IsNullOrWhiteSpace(customCommand))
        {
            var argsArr = new JsonArray();
            if (customArgs != null)
            {
                foreach (var a in customArgs) argsArr.Add(a);
            }
            entry = new JsonObject { ["command"] = customCommand.Trim(), ["args"] = argsArr };
        }
        else
        {
            return new McpGalleryActionResult { Ok = false, Message = $"Unknown MCP server preset '{serverId}'." };
        }

        try
        {
            var (root, serversObj, configFile) = LoadOrCreateJson(workspacePath);
            serversObj[serverId.Trim()] = entry;
            WriteJsonAtomic(configFile, root);
            McpManager.ShutdownAll();
            return new McpGalleryActionResult
            {
                Ok = true,
                Message = $"Installed MCP server '{serverId}' into .voidcode/mcp.json.",
                Items = List(workspacePath)
            };
        }
        catch (Exception ex)
        {
            return new McpGalleryActionResult { Ok = false, Message = $"Failed to update .voidcode/mcp.json: {ex.Message}" };
        }
    }

    public static McpGalleryActionResult Remove(string? workspacePath, string serverId)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return new McpGalleryActionResult { Ok = false, Message = "Open a workspace folder first." };
        }
        try
        {
            var (root, serversObj, configFile) = LoadOrCreateJson(workspacePath);
            serversObj.Remove(serverId);
            WriteJsonAtomic(configFile, root);
            McpManager.ShutdownAll();
            return new McpGalleryActionResult
            {
                Ok = true,
                Message = $"Removed MCP server '{serverId}' from .voidcode/mcp.json.",
                Items = List(workspacePath)
            };
        }
        catch (Exception ex)
        {
            return new McpGalleryActionResult { Ok = false, Message = $"Failed to remove MCP server: {ex.Message}" };
        }
    }

    private static (JsonObject root, JsonObject servers, string configFile) LoadOrCreateJson(string workspacePath)
    {
        var dir = Path.Combine(workspacePath, ".voidcode");
        Directory.CreateDirectory(dir);
        var configFile = Path.Combine(dir, "mcp.json");

        JsonObject root;
        if (File.Exists(configFile))
        {
            root = JsonNode.Parse(File.ReadAllText(configFile, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        return (root, servers, configFile);
    }

    private static void WriteJsonAtomic(string path, JsonObject root)
    {
        var tmp = path + ".tmp";
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(tmp, root.ToJsonString(opts), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}

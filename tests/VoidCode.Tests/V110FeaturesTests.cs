using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VoidCode.Agent.Providers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("StorageRoot")]
public class V110FeaturesTests
{
    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voidcode-v110-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AgentMemoryService_SavesListsDeduplicatesFormatsAndDeletes()
    {
        var storageRoot = CreateTempDir("mem-store");
        var workspace = CreateTempDir("mem-ws");
        using var scope = StorageService.UseRoot(storageRoot);
        try
        {
            var r1 = AgentMemoryService.Save(workspace, "project", "command", "Always run dotnet test -c Debug");
            Assert.True(r1.Ok);
            Assert.NotNull(r1.Item);
            Assert.Equal("project", r1.Item!.Scope);

            // Deduplicating identical content updates instead of duplicating.
            var r1Dup = AgentMemoryService.Save(workspace, "project", "convention", "Always run dotnet test -c Debug");
            Assert.True(r1Dup.Ok);
            Assert.Equal(r1.Item.Id, r1Dup.Item!.Id);

            var r2 = AgentMemoryService.Save(workspace, "global", "preference", "Prefer concise answers");
            Assert.True(r2.Ok);
            Assert.Equal("global", r2.Item!.Scope);

            var list = AgentMemoryService.List(workspace);
            Assert.Equal(2, list.Count);

            var promptBlock = AgentMemoryService.FormatForPrompt(workspace);
            Assert.Contains("PERSISTENT AGENT MEMORY BANK", promptBlock);
            Assert.Contains("dotnet test -c Debug", promptBlock);
            Assert.Contains("Prefer concise answers", promptBlock);

            var del = AgentMemoryService.Delete(workspace, r1.Item.Id);
            Assert.True(del.Ok);
            Assert.Single(AgentMemoryService.List(workspace));
        }
        finally
        {
            try { Directory.Delete(storageRoot, true); } catch (IOException) { /* cleanup */ }
            try { Directory.Delete(workspace, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void BranchReviewService_ParsesFencedJsonAndSortsBySeverity()
    {
        var raw = """
        ```json
        {
          "summary": "Two findings detected before merge.",
          "findings": [
            { "severity": "suggestion", "file": "src/a.cs", "line": 12, "title": "Rename var", "detail": "Unclear name", "suggestedFix": "Use userId" },
            { "severity": "critical", "file": "src/auth.cs", "line": 48, "title": "SQL injection", "detail": "Raw string interpolation", "suggestedFix": "Use parameterized command" },
            { "severity": "warning", "file": "src/b.cs", "line": 7, "title": "Unchecked null", "detail": "May throw NRE", "suggestedFix": "Add null check" }
          ]
        }
        ```
        """;

        var report = BranchReviewService.ParseReviewResponse(raw, "main", "Branch commits (main...HEAD)", "3 files changed");
        Assert.True(report.Ok);
        Assert.Equal("Two findings detected before merge.", report.Summary);
        Assert.Equal(3, report.Findings.Count);
        Assert.Equal("critical", report.Findings[0].Severity);
        Assert.Equal("warning", report.Findings[1].Severity);
        Assert.Equal("suggestion", report.Findings[2].Severity);
        Assert.Equal(48, report.Findings[0].Line);
    }

    [Fact]
    public async Task HttpClientRunnerService_ParsesDotHttpAndRejectsInvalidUrls()
    {
        var dotHttp = """
        # Fetch sample user
        POST https://example.com/api/users
        Content-Type: application/json
        Authorization: Bearer secret-token

        {"name": "Ada"}
        ###
        GET https://example.com/ignored
        """;

        var parsed = HttpClientRunnerService.ParseDotHttp(dotHttp);
        Assert.NotNull(parsed);
        Assert.Equal("POST", parsed!.Method);
        Assert.Equal("https://example.com/api/users", parsed.Url);
        Assert.Equal("Bearer secret-token", parsed.Headers!["Authorization"]);
        Assert.Contains("\"Ada\"", parsed.Body);

        var bad = await HttpClientRunnerService.SendAsync(new HttpRunRequest { Url = "file:///etc/passwd" });
        Assert.False(bad.Ok);
        Assert.Contains("http://", bad.Error);
    }

    [Fact]
    public void IdeImportService_ParsesJsoncSettingsIntoAppSettings()
    {
        var jsonc = """
        {
          // VS Code user settings with comments and trailing comma
          "editor.fontSize": 16,
          "editor.fontFamily": "'Fira Code', monospace",
          "editor.tabSize": 2,
          "editor.insertSpaces": false,
          "editor.wordWrap": "on",
          "editor.minimap.enabled": false,
          "editor.formatOnSave": true,
          "editor.lineNumbers": "relative",
          "editor.renderWhitespace": "all",
          "editor.cursorStyle": "block",
          "editor.fontLigatures": false,
          "editor.smoothScrolling": false,
          "editor.stickyScroll.enabled": false,
        }
        """;

        var target = new AppSettings();
        var imported = IdeImportService.ApplySettingsJson(jsonc, target);

        Assert.Equal(13, imported.Count);
        Assert.Equal(16, target.EditorFontSize);
        Assert.Equal("'Fira Code', monospace", target.EditorFontFamily);
        Assert.Equal(2, target.EditorTabSize);
        Assert.False(target.EditorInsertSpaces);
        Assert.True(target.EditorWordWrap);
        Assert.False(target.EditorMinimap);
        Assert.True(target.FormatOnSave);
        Assert.Equal("relative", target.EditorLineNumbers);
        Assert.Equal("all", target.EditorRenderWhitespace);
        Assert.Equal("block", target.EditorCursorStyle);
        Assert.False(target.EditorFontLigatures);
        Assert.False(target.EditorSmoothScrolling);
        Assert.False(target.EditorStickyScroll);
    }

    [Fact]
    public void DatabaseExplorerService_DiscoversInspectsAndQueriesRealSqlite()
    {
        var dir = CreateTempDir("sqlite");
        var dbPath = Path.Combine(dir, "app.sqlite");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, role TEXT);
                    INSERT INTO users (name, role) VALUES ('Ada', 'admin'), ('Linus', 'maintainer');
                """;
                cmd.ExecuteNonQuery();
            }

            var discovered = DatabaseExplorerService.Discover(dir);
            Assert.Single(discovered);
            Assert.Equal("app.sqlite", discovered[0].RelativePath);

            var schema = DatabaseExplorerService.InspectSchema(dbPath);
            Assert.True(schema.Ok);
            Assert.Single(schema.Tables);
            Assert.Equal("users", schema.Tables[0].Name);
            Assert.Equal(2, schema.Tables[0].RowCount);
            Assert.Equal(3, schema.Tables[0].Columns.Count);
            Assert.True(schema.Tables[0].Columns[0].PrimaryKey);
            Assert.Contains("users", schema.SchemaSummary);

            var query = DatabaseExplorerService.ExecuteQuery(dbPath, "SELECT name, role FROM users ORDER BY id;");
            Assert.True(query.Ok);
            Assert.Equal(new[] { "name", "role" }, query.Columns);
            Assert.Equal(2, query.Rows.Count);
            Assert.Equal("Ada", query.Rows[0][0]);
            Assert.Equal("maintainer", query.Rows[1][1]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void ReasoningEffort_ActivatesForOpenAiReasoningAndAnthropicExtendedThinking()
    {
        Assert.True(OpenAiCompatibleProvider.IsOpenAiReasoningModel("o4-mini"));
        Assert.True(OpenAiCompatibleProvider.IsOpenAiReasoningModel("openai/o3-mini"));
        Assert.True(OpenAiCompatibleProvider.IsOpenAiReasoningModel("gpt-5"));
        Assert.False(OpenAiCompatibleProvider.IsOpenAiReasoningModel("gpt-4.1"));

        var req = new ChatRequest
        {
            Provider = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 8192,
            Temperature = 0.2,
            ReasoningEffort = "high",
            Messages = new List<JsonObject>
            {
                new() { ["role"] = "user", ["content"] = "Analyze this architecture." }
            }
        };

        var payload = AnthropicProvider.BuildMessagesPayload(req);
        Assert.NotNull(payload["thinking"]);
        Assert.Equal("enabled", payload["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(1, payload["temperature"]!.GetValue<int>());
    }
}

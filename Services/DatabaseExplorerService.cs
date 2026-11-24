using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace VoidCode.Services;

public sealed class DiscoveredDatabase
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

public sealed class DbColumnInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("notNull")]
    public bool NotNull { get; set; }

    [JsonPropertyName("primaryKey")]
    public bool PrimaryKey { get; set; }
}

public sealed class DbTableInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "table";

    [JsonPropertyName("rowCount")]
    public long RowCount { get; set; }

    [JsonPropertyName("columns")]
    public List<DbColumnInfo> Columns { get; set; } = new();
}

public sealed class DbSchemaReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("tables")]
    public List<DbTableInfo> Tables { get; set; } = new();

    [JsonPropertyName("schemaSummary")]
    public string SchemaSummary { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class DbQueryResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("columns")]
    public List<string> Columns { get; set; } = new();

    [JsonPropertyName("rows")]
    public List<List<string?>> Rows { get; set; } = new();

    [JsonPropertyName("rowsAffected")]
    public int RowsAffected { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Built-in cross-platform SQLite Database Explorer using Microsoft.Data.Sqlite.
/// Auto-discovers .db/.sqlite files, inspects tables/columns/row-counts, runs SQL queries,
/// and produces compact schema summaries for VoidAgent.
/// </summary>
public static class DatabaseExplorerService
{
    private static readonly HashSet<string> DbExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3", ".db3"
    };

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".venv", "venv", "target", ".voidcode"
    };

    public static List<DiscoveredDatabase> Discover(string? workspacePath, int maxResults = 25)
    {
        var results = new List<DiscoveredDatabase>();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return results;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((workspacePath, 0));

        while (queue.Count > 0 && results.Count < maxResults)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > 5) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (results.Count >= maxResults) break;
                    var ext = Path.GetExtension(file);
                    if (!DbExtensions.Contains(ext)) continue;
                    try
                    {
                        var fi = new FileInfo(file);
                        results.Add(new DiscoveredDatabase
                        {
                            Path = file,
                            RelativePath = Path.GetRelativePath(workspacePath, file),
                            SizeBytes = fi.Length
                        });
                    }
                    catch (IOException) { /* skip unreadable file */ }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (SkippedDirs.Contains(name)) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (UnauthorizedAccessException) { /* skip protected directory */ }
            catch (IOException) { /* skip unreadable directory */ }
        }

        return results.OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static DbSchemaReport InspectSchema(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return new DbSchemaReport { Ok = false, Path = dbPath ?? "", Error = "Database file not found." };

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            var tables = new List<DbTableInfo>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(new DbTableInfo
                    {
                        Name = reader.GetString(0),
                        Kind = reader.GetString(1)
                    });
                }
            }

            var summary = new StringBuilder();
            summary.AppendLine($"SQLite Database: {Path.GetFileName(dbPath)} ({tables.Count} tables/views)");

            foreach (var tbl in tables)
            {
                var safeName = EscapeIdentifier(tbl.Name);
                using (var colCmd = conn.CreateCommand())
                {
                    colCmd.CommandText = $"PRAGMA table_info({safeName});";
                    using var r = colCmd.ExecuteReader();
                    while (r.Read())
                    {
                        tbl.Columns.Add(new DbColumnInfo
                        {
                            Name = r.GetString(1),
                            Type = r.IsDBNull(2) ? "" : r.GetString(2),
                            NotNull = r.GetInt32(3) != 0,
                            PrimaryKey = r.GetInt32(5) != 0
                        });
                    }
                }

                try
                {
                    using var countCmd = conn.CreateCommand();
                    countCmd.CommandText = $"SELECT COUNT(*) FROM {safeName};";
                    var scalar = countCmd.ExecuteScalar();
                    tbl.RowCount = scalar != null && scalar != DBNull.Value ? Convert.ToInt64(scalar) : 0;
                }
                catch (SqliteException)
                {
                    // Views with missing dependencies may fail COUNT(*); keep 0.
                    tbl.RowCount = 0;
                }

                var colsDesc = string.Join(", ", tbl.Columns.Select(c =>
                    $"{c.Name}{(string.IsNullOrEmpty(c.Type) ? "" : $" {c.Type}")}{(c.PrimaryKey ? " PK" : "")}"));
                summary.AppendLine($"- {tbl.Kind} {tbl.Name} (~{tbl.RowCount} rows): {colsDesc}");
            }

            return new DbSchemaReport
            {
                Ok = true,
                Path = dbPath,
                Tables = tables,
                SchemaSummary = summary.ToString().TrimEnd()
            };
        }
        catch (Exception ex)
        {
            return new DbSchemaReport { Ok = false, Path = dbPath, Error = ex.Message };
        }
    }

    public static DbQueryResult ExecuteQuery(string dbPath, string sql, int maxRows = 200)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return new DbQueryResult { Ok = false, Error = "Database file not found." };
        if (string.IsNullOrWhiteSpace(sql))
            return new DbQueryResult { Ok = false, Error = "SQL query cannot be empty." };

        var cap = Math.Clamp(maxRows <= 0 ? 200 : maxRows, 1, 1000);
        var sw = Stopwatch.StartNew();
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 10;

            using var reader = cmd.ExecuteReader();
            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<List<string?>>();
            bool truncated = false;
            while (reader.Read())
            {
                if (rows.Count >= cap)
                {
                    truncated = true;
                    break;
                }
                var row = new List<string?>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        row.Add(null);
                    }
                    else
                    {
                        var val = reader.GetValue(i);
                        if (val is byte[] bytes)
                            row.Add($"<BLOB {bytes.Length} B>");
                        else
                            row.Add(Convert.ToString(val));
                    }
                }
                rows.Add(row);
            }

            sw.Stop();
            return new DbQueryResult
            {
                Ok = true,
                Columns = columns,
                Rows = rows,
                RowsAffected = Math.Max(0, reader.RecordsAffected),
                Truncated = truncated,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DbQueryResult { Ok = false, ElapsedMs = sw.ElapsedMilliseconds, Error = ex.Message };
        }
    }

    private static string EscapeIdentifier(string identifier)
        => "\"" + (identifier ?? "").Replace("\"", "\"\"") + "\"";
}

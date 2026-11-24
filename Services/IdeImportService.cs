using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoidCode.Services;

public sealed class DetectedIdeSource
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("settingsPath")]
    public string SettingsPath { get; set; } = "";

    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("previewSettingsCount")]
    public int PreviewSettingsCount { get; set; }

    [JsonPropertyName("recentWorkspacesCount")]
    public int RecentWorkspacesCount { get; set; }
}

public sealed class IdeImportResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("sourceName")]
    public string SourceName { get; set; } = "";

    [JsonPropertyName("importedKeys")]
    public List<string> ImportedKeys { get; set; } = new();

    [JsonPropertyName("importedWorkspaces")]
    public int ImportedWorkspaces { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("settings")]
    public AppSettings? Settings { get; set; }
}

/// <summary>
/// 1-Click "Import from VS Code / Cursor" service.
/// Reads real VS Code, VS Code Insiders, and Cursor settings.json (JSONC) &amp; recent folders
/// across Windows, macOS, and Linux and maps safe editor ergonomics into VoidCode AppSettings.
/// </summary>
public static class IdeImportService
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly (string Id, string Name, string FolderName)[] KnownSources =
    {
        ("vscode", "Visual Studio Code", "Code"),
        ("cursor", "Cursor", "Cursor"),
        ("vscode-insiders", "VS Code Insiders", "Code - Insiders")
    };

    public static List<DetectedIdeSource> DetectSources()
    {
        var results = new List<DetectedIdeSource>();
        foreach (var src in KnownSources)
        {
            var userDir = ResolveIdeUserDir(src.FolderName);
            var settingsPath = Path.Combine(userDir, "settings.json");
            var found = File.Exists(settingsPath);
            int previewCount = 0;
            int recentsCount = 0;

            if (found)
            {
                try
                {
                    var preview = new AppSettings();
                    previewCount = ApplySettingsJson(File.ReadAllText(settingsPath, Encoding.UTF8), preview).Count;
                }
                catch (Exception ex)
                {
                    LoggingService.Swallowed(ex, $"IdeImportService.Preview.{src.Id}");
                }
            }

            if (Directory.Exists(userDir))
            {
                recentsCount = ReadRecentWorkspaces(userDir).Count;
            }

            results.Add(new DetectedIdeSource
            {
                Id = src.Id,
                Name = src.Name,
                SettingsPath = settingsPath,
                Found = found || recentsCount > 0,
                PreviewSettingsCount = previewCount,
                RecentWorkspacesCount = recentsCount
            });
        }

        return results;
    }

    public static IdeImportResult ImportFromSource(string sourceId, AppSettings targetSettings, string? customSettingsPath = null)
    {
        var src = KnownSources.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        var name = string.IsNullOrEmpty(src.Name) ? sourceId : src.Name;

        var userDir = !string.IsNullOrEmpty(src.FolderName) ? ResolveIdeUserDir(src.FolderName) : "";
        var settingsPath = !string.IsNullOrWhiteSpace(customSettingsPath)
            ? customSettingsPath
            : Path.Combine(userDir, "settings.json");

        var importedKeys = new List<string>();
        if (File.Exists(settingsPath))
        {
            try
            {
                var raw = File.ReadAllText(settingsPath, Encoding.UTF8);
                importedKeys = ApplySettingsJson(raw, targetSettings);
            }
            catch (Exception ex)
            {
                return new IdeImportResult
                {
                    Ok = false,
                    SourceName = name,
                    Message = $"Failed to parse {name} settings.json: {ex.Message}"
                };
            }
        }

        int importedWs = 0;
        if (!string.IsNullOrEmpty(userDir) && Directory.Exists(userDir))
        {
            var recents = ReadRecentWorkspaces(userDir);
            targetSettings.RecentWorkspaces ??= new List<string>();
            foreach (var dir in recents)
            {
                if (!targetSettings.RecentWorkspaces.Any(r => string.Equals(r, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    targetSettings.RecentWorkspaces.Add(dir);
                    importedWs++;
                }
            }
            if (targetSettings.RecentWorkspaces.Count > 20)
                targetSettings.RecentWorkspaces = targetSettings.RecentWorkspaces.Take(20).ToList();
        }

        if (importedKeys.Count == 0 && importedWs == 0)
        {
            return new IdeImportResult
            {
                Ok = false,
                SourceName = name,
                Message = $"No compatible settings or recent workspaces found for {name}."
            };
        }

        SettingsService.Save(targetSettings);
        return new IdeImportResult
        {
            Ok = true,
            SourceName = name,
            ImportedKeys = importedKeys,
            ImportedWorkspaces = importedWs,
            Settings = targetSettings,
            Message = $"Imported {importedKeys.Count} editor setting{(importedKeys.Count == 1 ? "" : "s")} and {importedWs} recent folder{(importedWs == 1 ? "" : "s")} from {name}."
        };
    }

    internal static List<string> ApplySettingsJson(string jsonc, AppSettings target)
    {
        var imported = new List<string>();
        using var doc = JsonDocument.Parse(jsonc, JsoncOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return imported;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "editor.fontSize":
                    if (prop.Value.TryGetInt32(out var fs))
                    {
                        target.EditorFontSize = Math.Clamp(fs, 10, 28);
                        imported.Add("EditorFontSize");
                    }
                    break;
                case "editor.fontFamily":
                    if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    {
                        target.EditorFontFamily = prop.Value.GetString()!.Trim();
                        imported.Add("EditorFontFamily");
                    }
                    break;
                case "editor.tabSize":
                    if (prop.Value.TryGetInt32(out var ts))
                    {
                        target.EditorTabSize = Math.Clamp(ts, 1, 8);
                        imported.Add("EditorTabSize");
                    }
                    break;
                case "editor.insertSpaces":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        target.EditorInsertSpaces = prop.Value.GetBoolean();
                        imported.Add("EditorInsertSpaces");
                    }
                    break;
                case "editor.wordWrap":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString()?.Trim().ToLowerInvariant();
                        target.EditorWordWrap = val is "on" or "wordwrapcolumn" or "bounded";
                        imported.Add("EditorWordWrap");
                    }
                    break;
                case "editor.minimap.enabled":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        target.EditorMinimap = prop.Value.GetBoolean();
                        imported.Add("EditorMinimap");
                    }
                    break;
                case "editor.formatOnSave":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        target.FormatOnSave = prop.Value.GetBoolean();
                        imported.Add("FormatOnSave");
                    }
                    break;
                case "editor.lineNumbers":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var ln = prop.Value.GetString()?.Trim().ToLowerInvariant();
                        if (ln is "on" or "off" or "relative")
                        {
                            target.EditorLineNumbers = ln;
                            imported.Add("EditorLineNumbers");
                        }
                    }
                    break;
                case "editor.renderWhitespace":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var ws = prop.Value.GetString()?.Trim().ToLowerInvariant();
                        if (ws is "none" or "boundary" or "selection" or "all")
                        {
                            target.EditorRenderWhitespace = ws;
                            imported.Add("EditorRenderWhitespace");
                        }
                    }
                    break;
                case "editor.cursorStyle":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var cs = prop.Value.GetString()?.Trim().ToLowerInvariant();
                        if (cs is "line" or "block" or "underline")
                        {
                            target.EditorCursorStyle = cs;
                            imported.Add("EditorCursorStyle");
                        }
                    }
                    break;
                case "editor.fontLigatures":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        target.EditorFontLigatures = prop.Value.GetBoolean();
                        imported.Add("EditorFontLigatures");
                    }
                    break;
                case "editor.smoothScrolling":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        target.EditorSmoothScrolling = prop.Value.GetBoolean();
                        imported.Add("EditorSmoothScrolling");
                    }
                    break;
                case "editor.stickyScroll.enabled":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        target.EditorStickyScroll = prop.Value.GetBoolean();
                        imported.Add("EditorStickyScroll");
                    }
                    break;
            }
        }

        return imported;
    }

    private static string ResolveIdeUserDir(string productFolder)
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", productFolder, "User");
        }
        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", productFolder, "User");
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, productFolder, "User");
    }

    private static List<string> ReadRecentWorkspaces(string userDir)
    {
        var folders = new List<string>();
        try
        {
            var storagePath = Path.Combine(userDir, "globalStorage", "storage.json");
            if (!File.Exists(storagePath)) return folders;

            using var doc = JsonDocument.Parse(File.ReadAllText(storagePath, Encoding.UTF8), JsoncOptions);
            if (!doc.RootElement.TryGetProperty("backupWorkspaces", out var bw)) return folders;
            if (!bw.TryGetProperty("folders", out var arr) || arr.ValueKind != JsonValueKind.Array) return folders;

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("folderUri", out var uriEl) || uriEl.ValueKind != JsonValueKind.String) continue;
                var rawUri = uriEl.GetString();
                if (string.IsNullOrWhiteSpace(rawUri)) continue;
                if (Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) && uri.IsFile)
                {
                    var local = uri.LocalPath;
                    if (Directory.Exists(local) && !folders.Contains(local, StringComparer.OrdinalIgnoreCase))
                        folders.Add(local);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "IdeImportService.ReadRecentWorkspaces");
        }
        return folders;
    }
}

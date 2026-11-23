using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoidCode.Services;

/// <summary>
/// Layered settings resolution (defaults < user < .voidcode/settings.json) with
/// Per-key winning-scope attribution and a strict allowlist so a repository can never elevate
/// AutoApproveTools, AuthorityMode, or steal/override ApiKey.
/// </summary>
public sealed class ScopedSettingsResult
{
    [JsonPropertyName("effective")]
    public AppSettings Effective { get; init; } = new();

    [JsonPropertyName("scopes")]
    public Dictionary<string, string> Scopes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("workspaceFile")]
    public string? WorkspaceFile { get; init; }

    [JsonPropertyName("ignoredUnsafeKeys")]
    public List<string> IgnoredUnsafeKeys { get; init; } = new();
}

public static class WorkspaceSettingsService
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly HashSet<string> BlockedWorkspaceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "AutoApproveTools",
        "AuthorityMode",
        "ApiKey",
        "ApiKeyConfigured",
        "ActiveOperator",
        "RecentWorkspaces",
        "LastWorkspace"
    };

    public static ScopedSettingsResult Resolve(AppSettings userSettings, string? workspacePath)
    {
        var defaults = new AppSettings();
        // Clone userSettings cleanly without losing the JsonIgnore ApiKey.
        var effective = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(userSettings)) ?? new AppSettings();
        effective.ApiKey = userSettings.ApiKey;
        effective.ApiKeyConfigured = userSettings.ApiKeyConfigured;

        var scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Provider"] = effective.Provider != defaults.Provider ? "user" : "default",
            ["BaseUrl"] = effective.BaseUrl != defaults.BaseUrl ? "user" : "default",
            ["Model"] = effective.Model != defaults.Model ? "user" : "default",
            ["Temperature"] = Math.Abs(effective.Temperature - defaults.Temperature) > 0.0001 ? "user" : "default",
            ["MaxTokens"] = effective.MaxTokens != defaults.MaxTokens ? "user" : "default",
            ["MaxAgentTurns"] = effective.MaxAgentTurns != defaults.MaxAgentTurns ? "user" : "default",
            ["EditorFontSize"] = effective.EditorFontSize != defaults.EditorFontSize ? "user" : "default",
            ["EditorWordWrap"] = effective.EditorWordWrap != defaults.EditorWordWrap ? "user" : "default",
            ["EditorMinimap"] = effective.EditorMinimap != defaults.EditorMinimap ? "user" : "default",
            ["FormatOnSave"] = effective.FormatOnSave != defaults.FormatOnSave ? "user" : "default",
            ["Theme"] = effective.Theme != defaults.Theme ? "user" : "default",
            ["AuthorityMode"] = effective.AuthorityMode != defaults.AuthorityMode ? "user" : "default",
            ["AutoApproveTools"] = effective.AutoApproveTools != defaults.AutoApproveTools ? "user" : "default"
        };

        var ignoredUnsafe = new List<string>();
        string? wsFile = null;

        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            var candidate = Path.Combine(workspacePath, ".voidcode", "settings.json");
            if (File.Exists(candidate) && PathPolicy.IsInsideWorkspace(workspacePath, candidate))
            {
                wsFile = candidate;
                ApplyWorkspaceOverrides(candidate, effective, scopes, ignoredUnsafe);
            }
        }

        return new ScopedSettingsResult
        {
            Effective = effective,
            Scopes = scopes,
            WorkspaceFile = wsFile,
            IgnoredUnsafeKeys = ignoredUnsafe
        };
    }

    private static void ApplyWorkspaceOverrides(
        string filePath,
        AppSettings target,
        Dictionary<string, string> scopes,
        List<string> ignoredUnsafe)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath, Encoding.UTF8), JsoncOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (BlockedWorkspaceKeys.Contains(prop.Name))
                {
                    ignoredUnsafe.Add(prop.Name);
                    continue;
                }

                switch (prop.Name.ToLowerInvariant())
                {
                    case "editorfontsize":
                        if (prop.Value.TryGetInt32(out var fs))
                        {
                            target.EditorFontSize = Math.Clamp(fs, 10, 32);
                            scopes["EditorFontSize"] = "workspace";
                        }
                        break;
                    case "editorwordwrap":
                        if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            target.EditorWordWrap = prop.Value.GetBoolean();
                            scopes["EditorWordWrap"] = "workspace";
                        }
                        break;
                    case "editorminimap":
                        if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            target.EditorMinimap = prop.Value.GetBoolean();
                            scopes["EditorMinimap"] = "workspace";
                        }
                        break;
                    case "formatonsave":
                        if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            target.FormatOnSave = prop.Value.GetBoolean();
                            scopes["FormatOnSave"] = "workspace";
                        }
                        break;
                    case "theme":
                        if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                        {
                            target.Theme = prop.Value.GetString()!;
                            scopes["Theme"] = "workspace";
                        }
                        break;
                    case "model":
                        if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                        {
                            target.Model = prop.Value.GetString()!;
                            scopes["Model"] = "workspace";
                        }
                        break;
                    case "temperature":
                        if (prop.Value.TryGetDouble(out var temp))
                        {
                            target.Temperature = Math.Clamp(temp, 0.0, 2.0);
                            scopes["Temperature"] = "workspace";
                        }
                        break;
                    case "maxtokens":
                        if (prop.Value.TryGetInt32(out var mt))
                        {
                            target.MaxTokens = Math.Clamp(mt, 256, 65536);
                            scopes["MaxTokens"] = "workspace";
                        }
                        break;
                    case "maxagentturns":
                        if (prop.Value.TryGetInt32(out var turns))
                        {
                            target.MaxAgentTurns = Math.Clamp(turns, 0, 1000);
                            scopes["MaxAgentTurns"] = "workspace";
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, $"WorkspaceSettingsService.Apply({filePath})");
        }
    }
}

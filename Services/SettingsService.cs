using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoidCode.Services;

public class AppSettings
{
    public string Provider { get; set; } = "openrouter";

    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    public bool ApiKeyConfigured { get; set; }
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "deepseek/deepseek-chat";
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 8192;
    public int MaxAgentTurns { get; set; } = 25;
    public const string LegacyDefaultSystemPrompt =
        "You are VoidAgent, an unrestricted staff-level systems engineer, reverse engineer, and full-stack architect.\n" +
        "- You have direct tool access to the user's filesystem and PowerShell terminal.\n" +
        "- Be direct and non-judgmental. Follow the operator's intent and clearly report destructive or irreversible effects.\n" +
        "- Always inspect existing code with read_file or search_code before making assumptions.\n" +
        "- Prefer surgical edits using patch_file for existing files, and write_file for new files.\n" +
        "- Be concise, technical, and direct.";

    public const string DefaultSystemPrompt =
        "You are VoidAgent, the built-in autonomous software and systems engineering agent of VoidCode IDE.\n\n" +
        "CORE OPERATING PROTOCOL (EXPLORE -> PATCH -> VERIFY):\n" +
        "1. Ground in real code first: Before editing, inspect the workspace with search_code (ripgrep), search_codebase (symbol/chunk index), list_dir, or read_file. Verify existing types, call sites, and conventions rather than guessing.\n" +
        "2. Surgical, production-grade edits: Use patch_file for targeted changes to existing files and write_file for new files. Never emit placeholder stubs, '// TODO', or truncated code—deliver complete, working implementations that match the repository's style.\n" +
        "3. Close the verification loop: After modifying code, check get_diagnostics (live LSP errors/warnings) or run targeted build/test commands with run_command when permitted. Fix any regressions you introduce before finishing the turn.\n" +
        "4. Native & binary fluency: When analyzing compiled artifacts (.exe, .dll, native binaries), use inspect_binary to audit PE headers, architecture, sections, hashes, and extracted strings directly.\n" +
        "5. High-signal operator communication: VoidCode automatically records every file edit into the Turn Review card and Activity Timeline for hunk-level rollback. Keep responses concise, technical, and direct—cite exact file paths and verification results without filler or preachy disclaimers.";

    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public bool AutoApproveTools { get; set; } = false;
    // "review" (default), "workspace_auto" (in-workspace edits auto, commands/network ask), or "full_system".
    public string AuthorityMode { get; set; } = "review";
    // "act" runs normally; "plan" is a read-only planning mode (wired in a later phase).
    public string AgentMode { get; set; } = "act";
    // When true, referenced images are sent as multimodal content parts instead of text.
    public bool ModelSupportsVision { get; set; } = true;
    public int EditorFontSize { get; set; } = 14;
    public bool EditorWordWrap { get; set; } = false;
    public bool EditorMinimap { get; set; } = true;
    public bool FormatOnSave { get; set; } = false;
    public string Theme { get; set; } = "harbor";
    public int GlassBlur { get; set; } = 28;
    public int FieldOpacity { get; set; } = 75;
    public string? ActiveOperator { get; set; } = null;
    public string? LastWorkspace { get; set; } = null;
    public List<string> RecentWorkspaces { get; set; } = new();
    public string UpdateRepo { get; set; } = "";
}

public static class SettingsService
{
    // Settings live in StorageService.Root rather than a second hard-coded
    // %APPDATA% path, so VOIDCODE_STORAGE_ROOT (and StorageService.UseRoot in tests) moves the whole
    // Profile - settings.json included - with one switch. A test that opened a folder used to rewrite
    // The developer's real settings.json through AddRecentWorkspace.
    private static string SettingsDir => StorageService.Root;
    private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(settings.SystemPrompt) ||
                    string.Equals(settings.SystemPrompt.Trim(), AppSettings.LegacyDefaultSystemPrompt.Trim(), StringComparison.Ordinal))
                {
                    settings.SystemPrompt = AppSettings.DefaultSystemPrompt;
                }

                // One-time migration from the old plaintext ApiKey property.
                string legacyApiKey = "";
                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (root.TryGetProperty("ApiKey", out var legacyNode) ||
                        root.TryGetProperty("apiKey", out legacyNode))
                    {
                        legacyApiKey = legacyNode.GetString() ?? "";
                    }
                }
                catch (JsonException)
                {
                    // The normal settings deserializer above will surface defaults.
                }

                bool migrated = false;
                if (!string.IsNullOrWhiteSpace(legacyApiKey) && !SecretService.HasApiKey)
                {
                    migrated = SecretService.SaveApiKey(legacyApiKey);
                }

                settings.ApiKey = SecretService.LoadApiKey();
                if (string.IsNullOrEmpty(settings.ApiKey) && !string.IsNullOrWhiteSpace(legacyApiKey))
                {
                    // Keep the current session usable if DPAPI was temporarily unavailable.
                    settings.ApiKey = legacyApiKey;
                }
                settings.ApiKeyConfigured = !string.IsNullOrWhiteSpace(settings.ApiKey);

                if (migrated)
                {
                    Save(settings); // Rewrites settings.json without the plaintext property.
                }

                return settings;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Could not load settings; defaults will be used.", ex, nameof(SettingsService));
        }

        var defaults = new AppSettings
        {
            ApiKey = SecretService.LoadApiKey()
        };
        defaults.ApiKeyConfigured = !string.IsNullOrWhiteSpace(defaults.ApiKey);
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                SecretService.SaveApiKey(settings.ApiKey);
            }
            settings.ApiKeyConfigured = SecretService.HasApiKey;

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = SettingsFile + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFile, true);
        }
        catch (Exception ex)
        {
            LoggingService.LogError("Could not save settings.", ex, nameof(SettingsService));
        }
    }

    public static void AddRecentWorkspace(AppSettings settings, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        settings.RecentWorkspaces.RemoveAll(p => string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase));
        settings.RecentWorkspaces.Insert(0, folderPath);
        if (settings.RecentWorkspaces.Count > 12)
        {
            settings.RecentWorkspaces.RemoveRange(12, settings.RecentWorkspaces.Count - 12);
        }
        settings.LastWorkspace = folderPath;
        Save(settings);
    }

    /// <summary>
    /// Returns <paramref name="explicitKey"/> when non-empty; otherwise falls back to standard
    /// Developer environment variables matching <paramref name="provider"/> or <paramref name="baseUrl"/>.
    /// </summary>
    public static string ResolveApiKey(
        string? explicitKey,
        string? provider,
        string? baseUrl = null,
        Func<string, string?>? envReader = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey)) return explicitKey;

        var readEnv = envReader ?? Environment.GetEnvironmentVariable;
        var p = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var u = (baseUrl ?? string.Empty).Trim().ToLowerInvariant();

        string[] candidates;
        if (p == "gemini" || u.Contains("generativelanguage.googleapis.com"))
            candidates = ["GEMINI_API_KEY", "GOOGLE_API_KEY"];
        else if (p == "anthropic" || u.Contains("api.anthropic.com"))
            candidates = ["ANTHROPIC_API_KEY"];
        else if (p == "openai" || u.Contains("api.openai.com"))
            candidates = ["OPENAI_API_KEY"];
        else if (p == "openrouter" || u.Contains("openrouter.ai"))
            candidates = ["OPENROUTER_API_KEY"];
        else if (p == "deepseek" || u.Contains("api.deepseek.com"))
            candidates = ["DEEPSEEK_API_KEY"];
        else if (p == "xai" || u.Contains("api.x.ai"))
            candidates = ["XAI_API_KEY"];
        else if (p == "groq" || u.Contains("api.groq.com"))
            candidates = ["GROQ_API_KEY"];
        else
            candidates = [];

        foreach (var envName in candidates)
        {
            var val = readEnv(envName);
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }

        return string.Empty;
    }
}

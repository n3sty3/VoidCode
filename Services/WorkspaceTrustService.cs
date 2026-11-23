using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoidCode.Services;

public enum WorkspaceTrustState
{
    Unknown,
    Trusted,
    Restricted
}

// Workspace Trust.
//
// Opening a folder runs whatever that folder contains - tasks, language servers, and an agent
// That can execute commands. So the first time an unknown folder is opened the user is asked to
// Trust it, and everything risky stays off until they answer:
//   * the agent is forced to Review (auto-approve cannot be used),
//   * run_command is refused,
//   * workspace language servers are not started.
// Decisions are persisted per folder and only as a SHA-256 hash of the canonical path, so the
// Store never records folder names. Absence means Unknown, and Unknown counts as untrusted
// Everywhere (fail closed).
public static class WorkspaceTrustService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> Decisions = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static string? _currentWorkspace;
    private static WorkspaceTrustState _currentState = WorkspaceTrustState.Unknown;

    public static string StorePath
    {
        get
        {
            // VOIDCODE_TRUST_STORE lets a portable/CI run (and the unit tests) keep the store out
            // Of the user's profile.
            var overridePath = Environment.GetEnvironmentVariable("VOIDCODE_TRUST_STORE");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoidCode",
                "workspace-trust.json");
        }
    }

    /// <summary>Drops the in-memory cache and the resolved state (used by the unit tests).</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            Decisions.Clear();
            _loaded = false;
            _currentWorkspace = null;
            _currentState = WorkspaceTrustState.Unknown;
        }
    }

    /// <summary>The trust state of the workspace the app currently has open.</summary>
    public static WorkspaceTrustState CurrentState
    {
        get { lock (Gate) { return _currentState; } }
    }

    /// <summary>True unless the open folder is trusted, i.e. the agent must ask before acting.</summary>
    public static bool ForcesReview => CurrentState != WorkspaceTrustState.Trusted;

    /// <summary>A stable key for a folder: the canonical path, case folded, hashed.</summary>
    public static string HashPath(string? workspacePath)
    {
        var canonical = Normalize(workspacePath);
        if (canonical.Length == 0) return string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static WorkspaceTrustState GetState(string? workspacePath)
    {
        var hash = HashPath(workspacePath);
        if (hash.Length == 0) return WorkspaceTrustState.Unknown;

        lock (Gate)
        {
            Load();
            if (!Decisions.TryGetValue(hash, out var trusted)) return WorkspaceTrustState.Unknown;
            return trusted ? WorkspaceTrustState.Trusted : WorkspaceTrustState.Restricted;
        }
    }

    /// <summary>Fail-closed helper: only an explicit "trusted" decision counts.</summary>
    public static bool IsTrusted(string? workspacePath) => GetState(workspacePath) == WorkspaceTrustState.Trusted;

    public static bool AllowsRunCommand(string? workspacePath) => IsTrusted(workspacePath);

    public static bool AllowsWorkspaceLanguageServers(string? workspacePath) => IsTrusted(workspacePath);

    public static void SetTrusted(string? workspacePath, bool trusted)
    {
        var hash = HashPath(workspacePath);
        if (hash.Length == 0) return;

        lock (Gate)
        {
            Load();
            Decisions[hash] = trusted;
            Save();
            if (string.Equals(HashPath(_currentWorkspace), hash, StringComparison.OrdinalIgnoreCase))
            {
                _currentState = trusted ? WorkspaceTrustState.Trusted : WorkspaceTrustState.Restricted;
            }
        }
    }

    public static void Revoke(string? workspacePath)
    {
        var hash = HashPath(workspacePath);
        if (hash.Length == 0) return;

        lock (Gate)
        {
            Load();
            if (!Decisions.Remove(hash)) return;
            Save();
            if (string.Equals(HashPath(_currentWorkspace), hash, StringComparison.OrdinalIgnoreCase))
            {
                _currentState = WorkspaceTrustState.Unknown;
            }
        }
    }

    /// <summary>Makes <paramref name="workspacePath"/> current and reports its state.</summary>
    public static WorkspaceTrustState Resolve(string? workspacePath)
    {
        var state = GetState(workspacePath);
        lock (Gate)
        {
            _currentWorkspace = workspacePath;
            _currentState = state;
        }
        return state;
    }

    /// <summary>
    /// Resolves the folder, tells the UI what it is, and asks the user when the folder has never
    /// Been opened before.
    /// </summary>
    public static void ApplyTo(string? workspacePath, Action<string, object>? sendToJs)
    {
        var state = Resolve(workspacePath);
        sendToJs?.Invoke("trust_state", new
        {
            path = workspacePath,
            state = Name(state),
            restricted = state != WorkspaceTrustState.Trusted
        });

        if (state == WorkspaceTrustState.Unknown && !string.IsNullOrWhiteSpace(workspacePath))
        {
            sendToJs?.Invoke("trust_prompt", new { path = workspacePath });
        }
    }

    /// <summary>
    /// Handles the trust actions before the main bridge switch. Returns true when the message was
    /// Ours, so the caller stops dispatching.
    /// </summary>
    public static bool TryHandleMessage(string action, JsonObject? node, Action<string, object>? sendToJs)
    {
        switch (action)
        {
            case "set_workspace_trust":
            {
                var path = node?["path"]?.GetValue<string>() ?? _currentWorkspace;
                var trusted = node?["trusted"]?.GetValue<bool>() ?? false;
                SetTrusted(path, trusted);
                ApplyTo(path, sendToJs);
                return true;
            }

            case "get_workspace_trust":
            {
                ApplyTo(node?["path"]?.GetValue<string>() ?? _currentWorkspace, sendToJs);
                return true;
            }

            default:
                return false;
        }
    }

    private static string Name(WorkspaceTrustState state) => state switch
    {
        WorkspaceTrustState.Trusted => "trusted",
        WorkspaceTrustState.Restricted => "restricted",
        _ => "unknown"
    };

    private static string Normalize(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return string.Empty;
        try
        {
            var full = Path.GetFullPath(workspacePath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.ToUpperInvariant();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(StorePath)) return;
            var root = JsonNode.Parse(File.ReadAllText(StorePath))?.AsObject();
            if (root?["workspaces"] is not JsonObject workspaces) return;
            foreach (var entry in workspaces)
            {
                if (entry.Key.Length == 0) continue;
                Decisions[entry.Key] = entry.Value?.GetValue<bool>() ?? false;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "WorkspaceTrustService.load");
        }
    }

    private static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var workspaces = new JsonObject();
            foreach (var pair in Decisions.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                workspaces[pair.Key] = pair.Value;
            }

            var document = new JsonObject
            {
                ["version"] = 1,
                ["workspaces"] = workspaces
            };
            File.WriteAllText(StorePath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "WorkspaceTrustService.save");
        }
    }
}

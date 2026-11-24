using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VoidCode.Services;

public class OperatorProfile
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("passwordHash")]
    public string PasswordHash { get; set; } = "";

    [JsonPropertyName("salt")]
    public string Salt { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "Systems operator";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SavedChatSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "";

    [JsonPropertyName("messages")]
    public JsonArray Messages { get; set; } = new();
}

public class RecoveryBufferState
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("cursorLine")]
    public int CursorLine { get; set; } = 1;

    [JsonPropertyName("cursorColumn")]
    public int CursorColumn { get; set; } = 1;
}

public class WorkspaceRecoveryState
{
    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "";

    [JsonPropertyName("activePath")]
    public string? ActivePath { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("buffers")]
    public List<RecoveryBufferState> Buffers { get; set; } = new();
}

public static class StorageService
{
    private static readonly object SyncLock = new();
    private static readonly object RootGate = new();
    private static string? _rootOverride;

    /// <summary>
    /// Where every store in this class lives: <c>%APPDATA%\VoidCode</c> unless something moves it.
    /// Kept as a property instead of the old static readonly fields so the two overrides below can
    /// Take effect, and resolved per access so a scope that ends mid-run is honoured by the next write.
    /// </summary>
    internal static bool IsRootOverridden
    {
        get
        {
            lock (RootGate)
            {
                if (_rootOverride != null) return true;
            }
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOIDCODE_STORAGE_ROOT"));
        }
    }

    public static string Root
    {
        get
        {
            lock (RootGate)
            {
                if (_rootOverride != null) return _rootOverride;
            }

            // VOIDCODE_STORAGE_ROOT is the portable/CI switch, the same convention as
            // WorkspaceTrustService's VOIDCODE_TRUST_STORE: keep the profile out of the user's home.
            var configured = Environment.GetEnvironmentVariable("VOIDCODE_STORAGE_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    return Path.GetFullPath(configured.Trim());
                }
                catch (Exception)
                {
                    // Fall through to the default rather than logging: LoggingService resolves its own
                    // Directory through this property, so a log entry here could recurse into it.
                }
            }

            return PlatformServices.GetDefaultAppDataRoot();
        }
    }

    /// <summary>
    /// Points every store at <paramref name="root"/> until the returned scope is disposed, then puts
    /// The previous root back, so scopes nest. This exists for tests: before it, exercising
    /// <c>auth_operator</c> or <c>save_session</c> created a real operator profile in the developer's
    /// <c>%APPDATA%</c>. The override is process-wide - callers that share a process must hold it for
    /// The whole scope, which is why these tests sit in their own non-parallel collection.
    /// </summary>
    public static IDisposable UseRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A storage root is required.", nameof(root));

        var resolved = Path.GetFullPath(root);
        lock (RootGate)
        {
            var scope = new RootScope(resolved, _rootOverride);
            _rootOverride = resolved;
            return scope;
        }
    }

    private sealed class RootScope : IDisposable
    {
        private readonly string _mine;
        private readonly string? _previous;
        private bool _disposed;

        public RootScope(string mine, string? previous)
        {
            _mine = mine;
            _previous = previous;
        }

        public void Dispose()
        {
            lock (RootGate)
            {
                if (_disposed) return;
                _disposed = true;
                // Restore the scope we replaced, but only while we are still the active one: an
                // Out-of-order dispose must not clobber a newer scope's root.
                if (string.Equals(_rootOverride, _mine, StringComparison.OrdinalIgnoreCase)) _rootOverride = _previous;
            }
        }
    }

    private static string OperatorsFile => Path.Combine(Root, "operators.json");
    private static string SessionsFile => Path.Combine(Root, "sessions.json");
    private static string CheckpointsFile => Path.Combine(Root, "checkpoints.json");
    private static string RecoveryDirectory => Path.Combine(Root, "recovery");

    /// <summary>Creates the root on the first write; the old static constructor did this eagerly.</summary>
    private static void EnsureRoot() => Directory.CreateDirectory(Root);

    // --- Operator Authentication (PBKDF2-SHA256) ---

    public static (bool success, string message, string username, string role) AuthenticateOrRegister(
        string mode,
        string username,
        string password,
        string role)
    {
        username = (username ?? "").Trim();
        role = string.IsNullOrWhiteSpace(role) ? "Systems operator" : role.Trim();

        if (string.IsNullOrEmpty(username))
        {
            return (false, "Operator handle is required.", "", "");
        }

        lock (SyncLock)
        {
            var profiles = LoadOperatorsInternal();
            var existing = profiles.FirstOrDefault(p =>
                string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));

            if (string.Equals(mode, "register", StringComparison.OrdinalIgnoreCase))
            {
                if (existing != null)
                {
                    return (false, "Profile already exists on disk. Switch to Sign in.", "", "");
                }

                var (hash, salt) = HashPassword(password ?? "");
                var created = new OperatorProfile
                {
                    Username = username,
                    PasswordHash = hash,
                    Salt = salt,
                    Role = role,
                    CreatedAt = DateTime.UtcNow
                };
                profiles.Add(created);
                SaveOperatorsInternal(profiles);
                return (true, "Operator profile created.", created.Username, created.Role);
            }
            else
            {
                if (existing == null)
                {
                    var (hash, salt) = HashPassword(password ?? "");
                    existing = new OperatorProfile
                    {
                        Username = username,
                        PasswordHash = hash,
                        Salt = salt,
                        Role = role,
                        CreatedAt = DateTime.UtcNow
                    };
                    profiles.Add(existing);
                    SaveOperatorsInternal(profiles);
                    return (true, "Operator profile registered.", existing.Username, existing.Role);
                }

                if (!string.IsNullOrEmpty(existing.PasswordHash) && !VerifyPassword(password ?? "", existing.PasswordHash, existing.Salt))
                {
                    return (false, "Passphrase does not match stored PBKDF2 profile.", "", "");
                }

                return (true, "Signed in.", existing.Username, existing.Role);
            }
        }
    }

    private static (string hash, string salt) HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return ("", "");
        byte[] saltBytes = RandomNumberGenerator.GetBytes(16);
        byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            saltBytes,
            100_000,
            HashAlgorithmName.SHA256,
            32
        );
        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    private static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        try
        {
            byte[] saltBytes = Convert.FromBase64String(storedSalt);
            byte[] expectedHash = Convert.FromBase64String(storedHash);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                saltBytes,
                100_000,
                HashAlgorithmName.SHA256,
                32
            );
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    private static List<OperatorProfile> LoadOperatorsInternal()
    {
        try
        {
            if (File.Exists(OperatorsFile))
            {
                var json = File.ReadAllText(OperatorsFile);
                return JsonSerializer.Deserialize<List<OperatorProfile>>(json) ?? new List<OperatorProfile>();
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "StorageService.LoadOperators"); }
        return new List<OperatorProfile>();
    }

    private static void SaveOperatorsInternal(List<OperatorProfile> profiles)
    {
        try
        {
            EnsureRoot();
            var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(OperatorsFile, json);
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "StorageService.SaveOperators"); }
    }

    // --- Persistent Chat Sessions ---

    public static List<SavedChatSession> LoadSessions()
    {
        lock (SyncLock)
        {
            try
            {
                if (File.Exists(SessionsFile))
                {
                    var json = File.ReadAllText(SessionsFile);
                    return JsonSerializer.Deserialize<List<SavedChatSession>>(json) ?? new List<SavedChatSession>();
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "StorageService.LoadSessions"); }
            return new List<SavedChatSession>();
        }
    }

    public static List<SavedChatSession> SaveSession(SavedChatSession session)
    {
        lock (SyncLock)
        {
            var list = LoadSessions();
            var idx = list.FindIndex(s => s.Id == session.Id);
            if (idx >= 0)
            {
                list[idx] = session;
            }
            else
            {
                list.Insert(0, session);
            }

            if (list.Count > 50)
            {
                list = list.Take(50).ToList();
            }

            try
            {
                EnsureRoot();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SessionsFile, json);
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "StorageService.SaveSessions"); }
            return list;
        }
    }

    public static List<SavedChatSession> DeleteSession(string sessionId)
    {
        lock (SyncLock)
        {
            var list = LoadSessions();
            list.RemoveAll(s => s.Id == sessionId);
            try
            {
                EnsureRoot();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SessionsFile, json);
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "StorageService.DeleteSession"); }
            return list;
        }
    }

    // --- Persistent File Checkpoints ---

    public static List<FileCheckpoint> LoadCheckpoints()
    {
        lock (SyncLock)
        {
            try
            {
                if (File.Exists(CheckpointsFile))
                {
                    var json = File.ReadAllText(CheckpointsFile);
                    return JsonSerializer.Deserialize<List<FileCheckpoint>>(json) ?? new List<FileCheckpoint>();
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "StorageService.LoadCheckpoints"); }
            return new List<FileCheckpoint>();
        }
    }

    public static void SaveCheckpoints(IEnumerable<FileCheckpoint> checkpoints)
    {
        lock (SyncLock)
        {
            try
            {
                var trimmed = checkpoints
                    .OrderByDescending(c => c.Timestamp)
                    .Take(120)
                    .ToList();
                EnsureRoot();
                var json = JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CheckpointsFile, json);
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "StorageService.SaveCheckpoints"); }
        }
    }

    // --- Crash recovery journals ---

    public static WorkspaceRecoveryState? LoadRecoveryState(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        lock (SyncLock)
        {
            try
            {
                var file = GetRecoveryFilePath(workspace);
                if (!File.Exists(file)) return null;
                var json = File.ReadAllText(file);
                var state = JsonSerializer.Deserialize<WorkspaceRecoveryState>(json);
                return state?.Buffers.Count > 0 ? state : null;
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Could not load the workspace recovery journal.", ex, nameof(StorageService));
                return null;
            }
        }
    }

    public static void SaveRecoveryState(WorkspaceRecoveryState state)
    {
        if (string.IsNullOrWhiteSpace(state.Workspace)) return;
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(RecoveryDirectory);
                var file = GetRecoveryFilePath(state.Workspace);
                state.UpdatedAtUtc = DateTime.UtcNow;

                const int maxBufferChars = 4 * 1024 * 1024;
                const int maxTotalChars = 12 * 1024 * 1024;
                int totalChars = 0;
                var acceptedBuffers = new List<RecoveryBufferState>();
                foreach (var buffer in state.Buffers.Take(50))
                {
                    if (string.IsNullOrWhiteSpace(buffer.Path) || buffer.Content == null) continue;
                    if (buffer.Content.Length > maxBufferChars) continue;
                    if (totalChars + buffer.Content.Length > maxTotalChars) break;
                    acceptedBuffers.Add(buffer);
                    totalChars += buffer.Content.Length;
                }
                state.Buffers = acceptedBuffers;

                if (state.Buffers.Count == 0)
                {
                    if (File.Exists(file)) File.Delete(file);
                    return;
                }

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = false });
                var temp = file + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                File.Move(temp, file, true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Could not save the workspace recovery journal.", ex, nameof(StorageService));
            }
        }
    }

    public static void ClearRecoveryState(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return;
        lock (SyncLock)
        {
            try
            {
                var file = GetRecoveryFilePath(workspace);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Could not clear the workspace recovery journal.", ex, nameof(StorageService));
            }
        }
    }

    private static string GetRecoveryFilePath(string workspace)
    {
        var normalized = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return Path.Combine(RecoveryDirectory, hash + ".json");
    }
}

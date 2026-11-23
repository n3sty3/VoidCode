using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// Append-only JSONL audit log of every tool decision at
/// %APPDATA%\VoidCode\logs\agent-audit.jsonl (following StorageService.Root so tests isolate cleanly).
/// Arguments are SHA-256 hashed so secrets or file contents are never echoed into audit logs.
/// </summary>
internal static class AgentAuditLog
{
    private static readonly object Sync = new();

    internal static string AuditFilePath => Path.Combine(StorageService.Root, "logs", "agent-audit.jsonl");

    internal static string HashArguments(string? argsJson)
    {
        var bytes = Encoding.UTF8.GetBytes(argsJson ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static void Record(
        string toolName,
        string? argsJson,
        string decision,
        string approver,
        string authorityMode,
        bool taintedByWeb,
        string workspacePath)
    {
        try
        {
            var entry = new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                tool = toolName,
                argsHash = HashArguments(argsJson),
                decision,
                approver,
                authorityMode,
                taintedByWeb,
                workspace = workspacePath
            };
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;

            lock (Sync)
            {
                var path = AuditFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "AgentAuditLog.Record");
        }
    }
}

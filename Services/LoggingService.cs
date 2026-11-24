using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoidCode.Services
{
    public static class LoggingService
    {
        private static readonly object _lock = new();
        private const long MaxLogFileSizeBytes = 10 * 1024 * 1024; // 10 MB

        // Log files are what users paste into bug reports, so credentials are
        // Removed on the way in. Live secrets (the provider key) are registered too, so even an
        // Unexpected echo of one is replaced.
        private static readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
        private static readonly (Regex Pattern, string Replacement)[] Redactions =
        {
            (new Regex(@"(?i)\bAuthorization\s*:\s*[^\r\n""]+", RegexOptions.Compiled), "Authorization: ***"),
            (new Regex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.Compiled), "Bearer ***"),
            (new Regex(@"\bsk-[A-Za-z0-9_\-]{6,}", RegexOptions.Compiled), "sk-***"),
            (new Regex(@"\bAIza[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled), "AIza***"),
            (new Regex(@"(?i)\b((?:x-)?api[_-]?key|access[_-]?token)\b\s*[=:]\s*[^\s""&,;]+", RegexOptions.Compiled), "$1: ***")
        };

        // VOIDCODE_LOG_DIR keeps logs out of the user profile for portable runs, and lets the
        // Tests write into a throwaway directory. Without it the logs sit under the same profile
        // Root as everything else, so a portable run carries its logs with it.
        public static string LogDirectory
        {
            get
            {
                var overrideDir = Environment.GetEnvironmentVariable("VOIDCODE_LOG_DIR");
                if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
                return Path.Combine(StorageService.Root, "logs");
            }
        }

        public static string LogFilePath => Path.Combine(LogDirectory, "voidcode.log");

        public static void LogInfo(string message, string? source = null)
        {
            WriteEntry("INFO", message, source, null);
        }

        public static void LogWarning(string message, string? source = null)
        {
            WriteEntry("WARN", message, source, null);
        }

        public static void LogError(string message, Exception? ex = null, string? source = null)
        {
            WriteEntry("ERROR", message, source, ex);
        }

        /// <summary>
        /// Records an error we deliberately swallow (). It never throws and never
        /// Surfaces a toast - it is the exact, greppable sink that replaces empty catch blocks.
        /// </summary>
        public static void Swallowed(Exception? ex, string where)
        {
            var message = ex != null ? ex.Message : "no exception";
            WriteEntry("WARN", $"Swallowed in {where}: {message}", where, ex);
        }

        /// <summary>
        /// Registers a live secret (for example the provider API key) so it is replaced wherever
        /// It appears in a log entry. Short values are ignored so ordinary words are not mangled.
        /// </summary>
        public static void RegisterSecret(string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 8) return;
            lock (_lock) { _secrets.Add(secret); }
        }

        /// <summary>
        /// Strips credentials from a string. Applied to every message and exception
        /// We write, because the log file is the support bundle users hand over.
        /// </summary>
        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var result = text;
            string[] secrets;
            lock (_lock) { secrets = _secrets.ToArray(); }

            // Longest first, so a key containing another registered value is fully removed
            foreach (var secret in secrets.OrderByDescending(value => value.Length))
            {
                result = result.Replace(secret, "***", StringComparison.Ordinal);
            }

            foreach (var (pattern, replacement) in Redactions)
            {
                result = pattern.Replace(result, replacement);
            }

            return result;
        }

        public static void OpenLogFolder()
        {
            try
            {
                var directory = LogDirectory;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                PlatformServices.RevealInFileManager(directory, out _);
            }
            catch (Exception) { /* could not open the log folder in Explorer */ }
        }

        private static void WriteEntry(string level, string message, string? source, Exception? ex)
        {
            lock (_lock)
            {
                try
                {
                    CheckRotation();
                    Directory.CreateDirectory(LogDirectory);

                    var entry = new
                    {
                        timestamp = DateTime.UtcNow.ToString("o"),
                        level,
                        source = source ?? "VoidCode",
                        message = Redact(message),
                        exception = ex != null ? new
                        {
                            type = ex.GetType().FullName,
                            message = Redact(ex.Message),
                            stackTrace = Redact(ex.StackTrace)
                        } : null
                    };

                    var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                    File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                }
                catch (Exception) { /* logging must never throw (no recursion into this sink) */ }
            }
        }

        private static void CheckRotation()
        {
            try
            {
                var fi = new FileInfo(LogFilePath);
                if (fi.Exists && fi.Length > MaxLogFileSizeBytes)
                {
                    var backup = Path.Combine(LogDirectory, $"voidcode_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
                    File.Move(LogFilePath, backup, true);
                }
            }
            catch (Exception) { /* rotation failed - keep appending to the current file */ }
        }
    }
}

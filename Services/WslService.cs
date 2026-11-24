using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace VoidCode.Services;

public sealed class WslDistroInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }
}

/// <summary>
/// Discovers installed Windows Subsystem for Linux (WSL) distributions on Windows
/// so VoidCode can open WSL terminals or run commands inside Ubuntu/Linux seamlessly.
/// </summary>
public static class WslService
{
    public static async Task<List<WslDistroInfo>> ListDistrosAsync()
    {
        var list = new List<WslDistroInfo>();
        if (!OperatingSystem.IsWindows()) return list;

        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "-l -q")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            };
            using var proc = Process.Start(psi);
            if (proc == null) return list;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* timed out */ }
                return list;
            }

            if (proc.ExitCode != 0) return list;
            var raw = await outputTask;
            var lines = raw.Replace("\0", "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var name = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new WslDistroInfo
                {
                    Name = name,
                    IsDefault = list.Count == 0
                });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "WslService.ListDistrosAsync");
        }

        return list;
    }
}

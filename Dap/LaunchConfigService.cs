using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoidCode.Services;

namespace VoidCode.Dap;

/// <summary>
/// Loads .voidcode/launch.json and .vscode/launch.json (JSONC-tolerant) and
/// Resolves workspace/file variables for debugging configurations.
/// </summary>
public sealed class LaunchConfiguration
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("request")]
    public string Request { get; set; } = "launch";

    [JsonPropertyName("program")]
    public string Program { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; set; } = new();

    [JsonPropertyName("stopOnEntry")]
    public bool StopOnEntry { get; set; }

    [JsonPropertyName("preLaunchTask")]
    public string? PreLaunchTask { get; set; }

    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; } = "";
}

public static class LaunchConfigService
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static List<LaunchConfiguration> LoadConfigurations(string workspacePath, string? activeFile = null)
    {
        var configs = new List<LaunchConfiguration>();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return configs;

        var candidates = new[]
        {
            Path.Combine(workspacePath, ".voidcode", "launch.json"),
            Path.Combine(workspacePath, ".vscode", "launch.json")
        };

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in candidates)
        {
            if (!File.Exists(file) || !PathPolicy.IsInsideWorkspace(workspacePath, file)) continue;
            try
            {
                var raw = File.ReadAllText(file, Encoding.UTF8);
                using var doc = JsonDocument.Parse(raw, JsoncOptions);
                if (!doc.RootElement.TryGetProperty("configurations", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;

                var relSource = Path.GetRelativePath(workspacePath, file);
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) continue;
                    if (!seenNames.Add(name)) continue;

                    var cfg = new LaunchConfiguration
                    {
                        Name = name,
                        Type = type,
                        Request = item.TryGetProperty("request", out var req) ? (req.GetString() ?? "launch") : "launch",
                        Program = Substitute(item.TryGetProperty("program", out var p) ? p.GetString() : "", workspacePath, activeFile),
                        Cwd = Substitute(item.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : workspacePath, workspacePath, activeFile),
                        StopOnEntry = (item.TryGetProperty("stopAtEntry", out var sae) && sae.ValueKind == JsonValueKind.True)
                                      || (item.TryGetProperty("stopOnEntry", out var soe) && soe.ValueKind == JsonValueKind.True),
                        PreLaunchTask = item.TryGetProperty("preLaunchTask", out var plt) ? plt.GetString() : null,
                        SourceFile = relSource
                    };

                    if (item.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in argsEl.EnumerateArray())
                        {
                            if (a.ValueKind == JsonValueKind.String)
                                cfg.Args.Add(Substitute(a.GetString(), workspacePath, activeFile));
                        }
                    }

                    if (item.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in envEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                                cfg.Env[prop.Name] = Substitute(prop.Value.GetString(), workspacePath, activeFile);
                        }
                    }

                    configs.Add(cfg);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, $"LaunchConfigService.LoadConfigurations({file})");
            }
        }

        return configs;
    }

    private static string Substitute(string? value, string workspacePath, string? activeFile)
        => TaskRunnerService.SubstituteVariables(value ?? "", workspacePath, activeFile);
}

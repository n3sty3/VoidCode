using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Agent.Providers;

namespace VoidCode.Services;

public sealed class UpdateCheckResult
{
    [JsonPropertyName("checked")]
    public bool Checked { get; init; }

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; init; }

    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; init; } = "1.0.0";

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; init; } = "1.0.0";

    [JsonPropertyName("releaseUrl")]
    public string ReleaseUrl { get; init; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = "";

    [JsonPropertyName("repo")]
    public string Repo { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("silent")]
    public bool Silent { get; init; }
}

/// <summary>
/// Checks GitHub Releases (/repos/{owner}/{repo}/releases/latest) to notify the user
/// In-app when a newer version of VoidCode has been pushed to GitHub.
/// </summary>
public static class UpdateCheckService
{
    public const string CurrentAppVersion = "1.0.0";
    public const string DefaultGitHubRepo = "n3sty3/VoidCode";

    private static readonly Regex RepoSlugRegex = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex GitRemoteGithubRegex = new(@"github\.com[:/]([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?\s*$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static string ResolveGitHubRepo(string? configuredRepo, string? workspacePath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRepo) && RepoSlugRegex.IsMatch(configuredRepo.Trim()))
        {
            return configuredRepo.Trim();
        }

        var envRepo = Environment.GetEnvironmentVariable("VOIDCODE_GITHUB_REPO");
        if (!string.IsNullOrWhiteSpace(envRepo) && RepoSlugRegex.IsMatch(envRepo.Trim()))
        {
            return envRepo.Trim();
        }

        foreach (var dir in new[] { AppContext.BaseDirectory, workspacePath })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var gitConfig = Path.Combine(dir, ".git", "config");
                if (File.Exists(gitConfig))
                {
                    var text = File.ReadAllText(gitConfig);
                    var match = GitRemoteGithubRegex.Match(text);
                    if (match.Success && RepoSlugRegex.IsMatch(match.Groups[1].Value))
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "UpdateCheckService.ResolveGitHubRepo");
            }
        }

        return DefaultGitHubRepo;
    }

    public static Version ParseCleanVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Version(1, 0, 0);
        var s = raw.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..].Trim();
        var dashIdx = s.IndexOf('-');
        if (dashIdx > 0) s = s[..dashIdx];
        var plusIdx = s.IndexOf('+');
        if (plusIdx > 0) s = s[..plusIdx];

        if (Version.TryParse(s, out var v))
        {
            return new Version(
                Math.Max(0, v.Major),
                Math.Max(0, v.Minor),
                Math.Max(0, v.Build < 0 ? 0 : v.Build));
        }
        return new Version(1, 0, 0);
    }

    public static bool IsNewerVersion(string currentVersion, string latestTag)
    {
        var cur = ParseCleanVersion(currentVersion);
        var lat = ParseCleanVersion(latestTag);
        return lat > cur;
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string? configuredRepo = null,
        bool silent = false,
        HttpClient? http = null,
        string? currentVersionOverride = null,
        CancellationToken cancellationToken = default)
    {
        var currentVer = string.IsNullOrWhiteSpace(currentVersionOverride) ? CurrentAppVersion : currentVersionOverride!;
        var repo = ResolveGitHubRepo(configuredRepo);
        var client = http ?? AgentHttp.Client;
        var apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("VoidCode-IDE", currentVer));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            using var resp = await client.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    Checked = false,
                    UpdateAvailable = false,
                    CurrentVersion = currentVer,
                    LatestVersion = currentVer,
                    Repo = repo,
                    Silent = silent,
                    ReleaseUrl = $"https://github.com/{repo}/releases",
                    Message = $"Could not check GitHub releases for {repo} (HTTP {(int)resp.StatusCode})."
                };
            }

            var node = JsonNode.Parse(body);
            var tagName = node?["tag_name"]?.GetValue<string>() ?? "";
            var htmlUrl = node?["html_url"]?.GetValue<string>() ?? $"https://github.com/{repo}/releases";
            var cleanLatest = ParseCleanVersion(tagName).ToString(3);
            var newer = !string.IsNullOrWhiteSpace(tagName) && IsNewerVersion(currentVer, tagName);

            string downloadUrl = "";
            if (node?["assets"] is JsonArray assets)
            {
                foreach (var asset in assets)
                {
                    var name = asset?["name"]?.GetValue<string>() ?? "";
                    var browserUrl = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(browserUrl)) continue;
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = browserUrl;
                        break;
                    }
                    if (string.IsNullOrEmpty(downloadUrl) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = browserUrl;
                    }
                }
            }

            return new UpdateCheckResult
            {
                Checked = true,
                UpdateAvailable = newer,
                CurrentVersion = currentVer,
                LatestVersion = cleanLatest,
                ReleaseUrl = htmlUrl,
                DownloadUrl = string.IsNullOrEmpty(downloadUrl) ? htmlUrl : downloadUrl,
                Repo = repo,
                Silent = silent,
                Message = newer
                    ? $"VoidCode v{cleanLatest} is available on GitHub (current: v{currentVer})."
                    : $"VoidCode v{currentVer} is up to date."
            };
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "UpdateCheckService.CheckForUpdatesAsync");
            return new UpdateCheckResult
            {
                Checked = false,
                UpdateAvailable = false,
                CurrentVersion = currentVer,
                LatestVersion = currentVer,
                Repo = repo,
                Silent = silent,
                ReleaseUrl = $"https://github.com/{repo}/releases",
                Message = $"Update check could not reach GitHub ({repo})."
            };
        }
    }
}

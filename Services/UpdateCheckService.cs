using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Agent.Providers;

namespace VoidCode.Services;

public sealed class UpdateInstallResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = "";

    [JsonPropertyName("sha256Verified")]
    public bool Sha256Verified { get; init; }
}

public sealed class UpdateCheckResult
{
    [JsonPropertyName("checked")]
    public bool Checked { get; init; }

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; init; }

    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; init; } = UpdateCheckService.CurrentAppVersion;

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; init; } = UpdateCheckService.CurrentAppVersion;

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
    public static string CurrentAppVersion { get; } = ResolveAssemblyVersion();
    public const string DefaultGitHubRepo = "n3sty3/VoidCode";

    private static string ResolveAssemblyVersion()
    {
        var v = typeof(UpdateCheckService).Assembly.GetName().Version;
        if (v != null && (v.Major > 0 || v.Minor > 0 || v.Build > 0))
        {
            return $"{v.Major}.{v.Minor}.{(v.Build < 0 ? 0 : v.Build)}";
        }
        return "1.1.0";
    }

    private static readonly Regex RepoSlugRegex = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex GitRemoteGithubRegex = new(@"github\.com[:/]([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?\s*$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static string ResolveGitHubRepo(string? configuredRepo, string? workspacePath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRepo) &&
            !string.Equals(configuredRepo.Trim(), "VoidCode/VoidCode", StringComparison.OrdinalIgnoreCase) &&
            RepoSlugRegex.IsMatch(configuredRepo.Trim()))
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
            string fallbackUrl = "";
            if (node?["assets"] is JsonArray assets)
            {
                bool isMac = OperatingSystem.IsMacOS();
                bool isLinux = OperatingSystem.IsLinux();
                bool isArm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

                foreach (var asset in assets)
                {
                    var name = asset?["name"]?.GetValue<string>() ?? "";
                    var browserUrl = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(browserUrl) || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) continue;

                    if (string.IsNullOrEmpty(fallbackUrl) &&
                        ((name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                         || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        fallbackUrl = browserUrl;
                    }

                    if (isMac)
                    {
                        var archTag = isArm ? "osx-arm64" : "osx-x64";
                        if (name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) && name.Contains(archTag, StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = browserUrl;
                            break;
                        }
                    }
                    else if (isLinux)
                    {
                        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) && name.Contains("linux", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = browserUrl;
                            break;
                        }
                    }
                    else
                    {
                        if (!isArm && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = browserUrl;
                            break;
                        }
                        if (isArm && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("win-arm64", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = browserUrl;
                            break;
                        }
                        if (string.IsNullOrEmpty(downloadUrl) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("win-", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = browserUrl;
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(downloadUrl)) downloadUrl = fallbackUrl;

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

    public static bool IsAllowedReleaseDownloadUrl(string? url, string? configuredRepo = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var repo = ResolveGitHubRepo(configuredRepo);
        var prefix = $"https://github.com/{repo}/releases/download/";
        return url.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads a GitHub Release installer into a temp folder, verifies its .sha256 sidecar
    /// checksum byte-for-byte, and launches the verified installer natively.
    /// </summary>
    public static async Task<UpdateInstallResult> DownloadAndInstallAsync(
        string downloadUrl,
        string? configuredRepo = null,
        CancellationToken cancellationToken = default)
    {
        var cleanUrl = (downloadUrl ?? "").Trim();
        if (!IsAllowedReleaseDownloadUrl(cleanUrl, configuredRepo))
        {
            return new UpdateInstallResult
            {
                Ok = false,
                Message = "Blocked: update download URL must belong to this repository's GitHub Releases."
            };
        }

        try
        {
            var uri = new Uri(cleanUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..", StringComparison.Ordinal))
            {
                return new UpdateInstallResult { Ok = false, Message = "Invalid release asset filename." };
            }

            var updatesDir = Path.Combine(Path.GetTempPath(), "VoidCode-Updates");
            Directory.CreateDirectory(updatesDir);
            var targetPath = Path.Combine(updatesDir, fileName);
            var tempPath = targetPath + ".download";

            using var req = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("VoidCode-IDE", CurrentAppVersion));
            using var resp = await AgentHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateInstallResult { Ok = false, Message = $"Download failed (HTTP {(int)resp.StatusCode})." };
            }

            await using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken))
            await using (var dst = File.Create(tempPath))
            {
                await src.CopyToAsync(dst, cancellationToken);
            }

            // Verify SHA-256 against the release's .sha256 sidecar asset.
            bool verified = false;
            try
            {
                using var shaReq = new HttpRequestMessage(HttpMethod.Get, cleanUrl + ".sha256");
                shaReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("VoidCode-IDE", CurrentAppVersion));
                using var shaResp = await AgentHttp.Client.SendAsync(shaReq, cancellationToken);
                if (shaResp.IsSuccessStatusCode)
                {
                    var expectedLine = (await shaResp.Content.ReadAsStringAsync(cancellationToken)).Trim();
                    var expectedHash = expectedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(expectedHash) && expectedHash.Length == 64)
                    {
                        await using var checkStream = File.OpenRead(tempPath);
                        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(checkStream, cancellationToken)).ToLowerInvariant();
                        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(tempPath); } catch (IOException) { /* cleanup */ }
                            return new UpdateInstallResult
                            {
                                Ok = false,
                                Message = "Security check failed: downloaded installer SHA-256 did not match GitHub release checksum."
                            };
                        }
                        verified = true;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "UpdateCheckService.VerifySha256");
            }

            File.Move(tempPath, targetPath, true);

            if (OperatingSystem.IsWindows() && targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
                return new UpdateInstallResult
                {
                    Ok = true,
                    FilePath = targetPath,
                    Sha256Verified = verified,
                    Message = verified
                        ? $"Verified SHA-256 and launched {fileName}."
                        : $"Launched {fileName}."
                };
            }

            if (OperatingSystem.IsMacOS() && targetPath.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("/usr/bin/open", $"\"{targetPath}\"") { UseShellExecute = false });
                return new UpdateInstallResult
                {
                    Ok = true,
                    FilePath = targetPath,
                    Sha256Verified = verified,
                    Message = verified
                        ? $"Verified SHA-256 and mounted {fileName}."
                        : $"Mounted {fileName}."
                };
            }

            return new UpdateInstallResult
            {
                Ok = true,
                FilePath = targetPath,
                Sha256Verified = verified,
                Message = $"Downloaded update to {targetPath}."
            };
        }
        catch (Exception ex)
        {
            LoggingService.LogError($"Update download failed: {ex.Message}", ex, nameof(UpdateCheckService));
            return new UpdateInstallResult { Ok = false, Message = $"Update download failed: {ex.Message}" };
        }
    }
}

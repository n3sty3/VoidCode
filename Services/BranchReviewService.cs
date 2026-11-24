using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Providers;

namespace VoidCode.Services;

public sealed class ReviewFinding
{
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "suggestion";

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("suggestedFix")]
    public string SuggestedFix { get; set; } = "";
}

public sealed class BranchReviewReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("baseRef")]
    public string BaseRef { get; set; } = "";

    [JsonPropertyName("diffMode")]
    public string DiffMode { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("diffStat")]
    public string DiffStat { get; set; } = "";

    [JsonPropertyName("findings")]
    public List<ReviewFinding> Findings { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// AI Branch / Pre-PR Code Reviewer. Collects git diff against a base ref (or working tree)
/// and runs a structured staff-engineer review returning severity-tagged findings with file:line anchors.
/// </summary>
public static class BranchReviewService
{
    private const int MaxDiffChars = 36_000;

    private const string ReviewSystemPrompt =
        "You are a staff software engineer performing a rigorous Pre-PR Code Review.\n" +
        "Analyze the provided git diff for bugs, regressions, security flaws, race conditions, missing error handling, and performance issues.\n" +
        "Return STRICT JSON ONLY (no markdown prose outside JSON) matching this schema:\n" +
        "{\n" +
        "  \"summary\": \"1-3 sentence executive summary of the changes and overall merge readiness\",\n" +
        "  \"findings\": [\n" +
        "    {\n" +
        "      \"severity\": \"critical\" | \"warning\" | \"suggestion\",\n" +
        "      \"file\": \"relative/path/to/file.ext\",\n" +
        "      \"line\": 42,\n" +
        "      \"title\": \"Short finding title\",\n" +
        "      \"detail\": \"Concrete explanation of why this is an issue\",\n" +
        "      \"suggestedFix\": \"Actionable fix instruction or snippet\"\n" +
        "    }\n" +
        "  ]\n" +
        "}\n" +
        "If there are no issues, return an empty findings array and state that the diff looks clean in summary.";

    public static async Task<BranchReviewReport> ReviewAsync(
        string workspacePath,
        AppSettings settings,
        string? requestedBaseRef = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            return new BranchReviewReport { Ok = false, Error = "Open a workspace folder first." };

        var (baseRef, diffMode, diffText, diffStat, diffError) = await CollectDiffAsync(workspacePath, requestedBaseRef, ct);
        if (!string.IsNullOrEmpty(diffError))
            return new BranchReviewReport { Ok = false, BaseRef = baseRef, Error = diffError };

        if (string.IsNullOrWhiteSpace(diffText))
        {
            return new BranchReviewReport
            {
                Ok = true,
                BaseRef = baseRef,
                DiffMode = diffMode,
                DiffStat = diffStat,
                Summary = $"No diff changes found ({diffMode}). Make commits on a feature branch or edit working-tree files to run a review.",
                Findings = new List<ReviewFinding>()
            };
        }

        var boundedDiff = diffText.Length > MaxDiffChars
            ? diffText[..MaxDiffChars] + "\n... [diff truncated at 36,000 chars]"
            : diffText;

        try
        {
            var provider = ProviderFactory.Create(settings);
            var req = new ChatRequest
            {
                Provider = settings.Provider,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                Model = settings.Model,
                Temperature = 0.1,
                MaxTokens = Math.Min(Math.Max(settings.MaxTokens, 2048), 4096),
                Stream = false,
                ReasoningEffort = settings.AgentReasoningEffort ?? "medium",
                Messages = new List<JsonObject>
                {
                    new() { ["role"] = "system", ["content"] = ReviewSystemPrompt },
                    new()
                    {
                        ["role"] = "user",
                        ["content"] = $"Base ref: {baseRef} ({diffMode})\n\nDiff stat:\n{diffStat}\n\nGit diff:\n{boundedDiff}"
                    }
                }
            };

            var raw = await provider.CompleteAsync(req, ct);
            var parsed = ParseReviewResponse(raw, baseRef, diffMode, diffStat);
            return parsed;
        }
        catch (Exception ex)
        {
            return new BranchReviewReport
            {
                Ok = false,
                BaseRef = baseRef,
                DiffMode = diffMode,
                DiffStat = diffStat,
                Error = $"AI review failed: {ex.Message}"
            };
        }
    }

    internal static BranchReviewReport ParseReviewResponse(string raw, string baseRef, string diffMode, string diffStat)
    {
        var text = (raw ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = text.IndexOf('\n');
            if (firstNl > 0) text = text[(firstNl + 1)..];
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) text = text[..lastFence];
            text = text.Trim();
        }

        // Extract first balanced JSON object if model wrapped JSON with commentary.
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            text = text[firstBrace..(lastBrace + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            var summary = root.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.String
                ? sumEl.GetString() ?? ""
                : "Review completed.";

            var findings = new List<ReviewFinding>();
            if (root.TryGetProperty("findings", out var fArr) && fArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in fArr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var sev = (GetStr(item, "severity") ?? "suggestion").Trim().ToLowerInvariant();
                    if (sev is not ("critical" or "warning" or "suggestion")) sev = "suggestion";
                    int line = 0;
                    if (item.TryGetProperty("line", out var lineEl))
                    {
                        if (lineEl.ValueKind == JsonValueKind.Number && lineEl.TryGetInt32(out var parsedLine))
                            line = Math.Max(0, parsedLine);
                        else if (lineEl.ValueKind == JsonValueKind.String && int.TryParse(lineEl.GetString(), out var strLine))
                            line = Math.Max(0, strLine);
                    }

                    findings.Add(new ReviewFinding
                    {
                        Severity = sev,
                        File = GetStr(item, "file") ?? "",
                        Line = line,
                        Title = GetStr(item, "title") ?? "Finding",
                        Detail = GetStr(item, "detail") ?? "",
                        SuggestedFix = GetStr(item, "suggestedFix") ?? GetStr(item, "suggested_fix") ?? ""
                    });
                }
            }

            // Order critical -> warning -> suggestion
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["critical"] = 0,
                ["warning"] = 1,
                ["suggestion"] = 2
            };
            findings = findings.OrderBy(f => order.GetValueOrDefault(f.Severity, 3)).ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase).ToList();

            return new BranchReviewReport
            {
                Ok = true,
                BaseRef = baseRef,
                DiffMode = diffMode,
                DiffStat = diffStat,
                Summary = summary,
                Findings = findings
            };
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "BranchReviewService.ParseReviewResponse");
            return new BranchReviewReport
            {
                Ok = true,
                BaseRef = baseRef,
                DiffMode = diffMode,
                DiffStat = diffStat,
                Summary = string.IsNullOrWhiteSpace(raw) ? "Model returned an empty response." : raw.Trim(),
                Findings = new List<ReviewFinding>()
            };
        }
    }

    private static string? GetStr(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static readonly SystemProcessRunner Runner = new();

    private static async Task<ProcessResult> RunGitAsync(string workspacePath, string[] args, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        return await Runner.RunAsync("git", args, workspacePath, null, cts.Token);
    }

    private static async Task<(string baseRef, string diffMode, string diff, string stat, string? error)> CollectDiffAsync(
        string workspacePath,
        string? requestedBaseRef,
        CancellationToken ct)
    {
        var checkGit = await RunGitAsync(workspacePath, new[] { "rev-parse", "--is-inside-work-tree" }, 5000, ct);
        if (checkGit.ExitCode != 0)
            return ("", "", "", "", "This workspace is not a Git repository.");

        var baseRef = (requestedBaseRef ?? "").Trim();
        if (string.IsNullOrEmpty(baseRef))
        {
            foreach (var candidate in new[] { "main", "master", "origin/main", "origin/master" })
            {
                var verify = await RunGitAsync(workspacePath, new[] { "rev-parse", "--verify", "--quiet", candidate }, 4000, ct);
                if (verify.ExitCode == 0)
                {
                    baseRef = candidate;
                    break;
                }
            }
            if (string.IsNullOrEmpty(baseRef)) baseRef = "HEAD";
        }

        // First try branch diff baseRef...HEAD
        if (!string.Equals(baseRef, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var branchDiff = await RunGitAsync(workspacePath, new[] { "diff", "--no-color", $"{baseRef}...HEAD" }, 15000, ct);
            if (branchDiff.ExitCode == 0 && !string.IsNullOrWhiteSpace(branchDiff.Stdout))
            {
                var branchStat = await RunGitAsync(workspacePath, new[] { "diff", "--stat", $"{baseRef}...HEAD" }, 8000, ct);
                return (baseRef, $"Branch commits ({baseRef}...HEAD)", branchDiff.Stdout, branchStat.Stdout.Trim(), null);
            }
        }

        // Fall back to uncommitted working tree + staged diff vs HEAD.
        var wtDiff = await RunGitAsync(workspacePath, new[] { "diff", "--no-color", "HEAD" }, 15000, ct);
        var wtStat = await RunGitAsync(workspacePath, new[] { "diff", "--stat", "HEAD" }, 8000, ct);
        if (wtDiff.ExitCode == 0)
            return (baseRef, "Uncommitted working tree vs HEAD", wtDiff.Stdout, wtStat.Stdout.Trim(), null);

        // Fresh repo without HEAD yet: staged diff.
        var cachedDiff = await RunGitAsync(workspacePath, new[] { "diff", "--no-color", "--cached" }, 15000, ct);
        return (baseRef, "Staged changes", cachedDiff.Stdout, "", null);
    }
}

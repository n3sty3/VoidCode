using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace VoidCode.Services;

public class TestCaseItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("suite")]
    public string Suite { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "passed"; // "passed", "failed", "skipped"

    [JsonPropertyName("duration")]
    public string Duration { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }
}

public class TestRunResult
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("runner")]
    public string Runner { get; set; } = "";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("passed")]
    public int Passed { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("rawOutput")]
    public string RawOutput { get; set; } = "";

    [JsonPropertyName("resultFormat")]
    public string ResultFormat { get; set; } = "console";

    [JsonPropertyName("tests")]
    public List<TestCaseItem> Tests { get; set; } = new();
}

public static class TestService
{
    private static Process? _activeTestProcess;
    private static readonly object _lock = new();

    public static (string Runner, string Command, string Args)? DetectTestRunner(string workspace)
    {
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace)) return null;

        // 1. .NET (csproj or sln)
        var slnFiles = Directory.GetFiles(workspace, "*.sln", SearchOption.TopDirectoryOnly);
        var slnxFiles = Directory.GetFiles(workspace, "*.slnx", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length > 0 || slnxFiles.Length > 0 || ContainsWorkspaceFile(workspace, ".csproj"))
        {
            return ("dotnet test", "dotnet", "test --logger \"console;verbosity=normal\"");
        }

        // 2. Node / JS / TS (package.json with test script)
        var pkgPath = Path.Combine(workspace, "package.json");
        if (File.Exists(pkgPath))
        {
            try
            {
                using var package = JsonDocument.Parse(File.ReadAllText(pkgPath));
                if (package.RootElement.TryGetProperty("scripts", out var scripts) &&
                    scripts.ValueKind == JsonValueKind.Object &&
                    scripts.TryGetProperty("test", out var testScript) &&
                    testScript.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(testScript.GetString()))
                {
                    return OperatingSystem.IsWindows()
                        ? ("npm test", "cmd.exe", "/c npm test")
                        : ("npm test", "/bin/sh", "-c \"npm test\"");
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "TestService.DetectNpmTestScript"); }
        }

        // 3. Python (pytest or unittest)
        if (File.Exists(Path.Combine(workspace, "pytest.ini")) ||
            File.Exists(Path.Combine(workspace, "pyproject.toml")) ||
            Directory.Exists(Path.Combine(workspace, "tests")))
        {
            return ("pytest", "pytest", "-v");
        }

        // 4. Rust (Cargo.toml)
        if (File.Exists(Path.Combine(workspace, "Cargo.toml")))
        {
            return ("cargo test", "cargo", "test");
        }

        // 5. Go (go.mod)
        if (File.Exists(Path.Combine(workspace, "go.mod")))
        {
            return ("go test", "go", "test -json ./...");
        }

        return null;
    }

    private static bool ContainsWorkspaceFile(string workspace, string extension)
    {
        var matcher = new WorkspaceIgnoreMatcher(workspace, FileSystemService.IgnoredDirs);
        var pending = new Stack<string>();
        pending.Push(workspace);
        int visitedDirectories = 0;
        while (pending.Count > 0 && visitedDirectories++ < 20_000)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (!matcher.IsIgnored(file) && Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!matcher.IsIgnored(child, isDirectory: true)) pending.Push(child);
                }
            }
            catch (UnauthorizedAccessException) { /* access denied - skip this directory */ }
            catch (IOException) { /* directory vanished or is locked - skip */ }
        }
        return false;
    }

    public static async Task<TestRunResult> RunTestsAsync(
        string workspace,
        string? filter = null,
        Action<string>? onOutputLine = null)
    {
        var result = new TestRunResult();
        var detected = DetectTestRunner(workspace);
        if (detected == null)
        {
            result.Runner = "None";
            result.RawOutput = "No test runner detected in this workspace.\nSupported runners: dotnet test, npm test, pytest, cargo test, go test.";
            return result;
        }

        result.Runner = detected.Value.Runner;
        var fileName = detected.Value.Command;
        var args = detected.Value.Args;

        if (!string.IsNullOrEmpty(filter))
        {
            if (result.Runner.StartsWith("dotnet"))
            {
                args += $" --filter \"{filter}\"";
            }
            else if (result.Runner.StartsWith("pytest"))
            {
                args += $" -k \"{filter}\"";
            }
            else if (result.Runner.StartsWith("go"))
            {
                args = $"test -json -run \"{filter}\" ./...";
            }
        }

        ConfigureStructuredOutput(result.Runner, ref args, out var resultDirectory, out var resultPath, out var resultFormat);

        result.Command = $"{fileName} {args}".Trim();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var outputSb = new StringBuilder();
        var outputLock = new object();
        bool outputTruncated = false;
        var sw = Stopwatch.StartNew();

        void CaptureOutput(string line)
        {
            bool emit;
            lock (outputLock)
            {
                if (outputSb.Length < 10 * 1024 * 1024)
                {
                    outputSb.AppendLine(line);
                    emit = true;
                }
                else if (!outputTruncated)
                {
                    outputTruncated = true;
                    line = "[VoidCode truncated test output after 10 MiB.]";
                    emit = true;
                }
                else
                {
                    emit = false;
                }
            }
            if (emit) onOutputLine?.Invoke(line);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            lock (_lock)
            {
                _activeTestProcess = process;
            }

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    CaptureOutput(e.Data);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    CaptureOutput(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            lock (outputLock)
            {
                result.RawOutput = outputSb.ToString();
                if (outputTruncated) result.RawOutput += "\n[VoidCode truncated test output after 10 MiB.]\n";
            }

            if (!TryParseStructuredResults(result, workspace, resultPath, resultFormat))
            {
                ParseConsoleTestResults(result, workspace);
            }
        }
        catch (Exception ex)
        {
            result.RawOutput = $"Failed to execute {result.Command}: {ex.Message}";
        }
        finally
        {
            lock (_lock)
            {
                _activeTestProcess = null;
            }
            if (!string.IsNullOrWhiteSpace(resultDirectory))
            {
                try { Directory.Delete(resultDirectory, recursive: true); } catch (Exception) { /* result directory already removed or locked */ }
            }
        }

        return result;
    }

    private static void ConfigureStructuredOutput(
        string runner,
        ref string args,
        out string? resultDirectory,
        out string? resultPath,
        out string? resultFormat)
    {
        resultDirectory = null;
        resultPath = null;
        resultFormat = null;
        try
        {
            if (runner.StartsWith("go", StringComparison.OrdinalIgnoreCase))
            {
                resultFormat = "go-json";
                return;
            }
            if (!runner.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) &&
                !runner.StartsWith("pytest", StringComparison.OrdinalIgnoreCase))
                return;

            resultDirectory = Path.Combine(
                Path.GetTempPath(),
                "VoidCode",
                "test-results",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resultDirectory);

            if (runner.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                resultPath = Path.Combine(resultDirectory, "voidcode.trx");
                resultFormat = "trx";
                args += $" --logger \"trx;LogFileName={Path.GetFileName(resultPath)}\" --results-directory \"{resultDirectory}\"";
            }
            else
            {
                resultPath = Path.Combine(resultDirectory, "voidcode-junit.xml");
                resultFormat = "junit";
                args += $" --junitxml=\"{resultPath}\"";
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Could not configure structured test output: {ex.Message}", nameof(TestService));
            resultDirectory = null;
            resultPath = null;
            resultFormat = null;
        }
    }

    private static bool TryParseStructuredResults(
        TestRunResult result,
        string workspace,
        string? resultPath,
        string? resultFormat)
    {
        if (string.IsNullOrWhiteSpace(resultFormat)) return false;
        try
        {
            List<TestCaseItem> tests;
            switch (resultFormat)
            {
                case "trx" when !string.IsNullOrWhiteSpace(resultPath) && File.Exists(resultPath):
                    tests = ParseTrx(ReadStructuredResultFile(resultPath), workspace);
                    break;
                case "junit" when !string.IsNullOrWhiteSpace(resultPath) && File.Exists(resultPath):
                    tests = ParseJUnitXml(ReadStructuredResultFile(resultPath), workspace);
                    break;
                case "go-json":
                    tests = ParseGoJson(result.RawOutput, workspace);
                    break;
                default:
                    return false;
            }

            ApplyParsedTests(result, tests, resultFormat);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogWarning($"Could not parse {resultFormat} test output: {ex.Message}", nameof(TestService));
            return false;
        }
    }

    private static void ApplyParsedTests(TestRunResult result, List<TestCaseItem> tests, string format)
    {
        result.Tests = tests;
        result.Passed = tests.Count(test => test.Status == "passed");
        result.Failed = tests.Count(test => test.Status == "failed");
        result.Skipped = tests.Count(test => test.Status == "skipped");
        result.Total = tests.Count;
        result.ResultFormat = format;
        for (int index = 0; index < tests.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(tests[index].Id)) tests[index].Id = $"test_{index + 1}";
        }
    }

    public static List<TestCaseItem> ParseTrx(string xml, string workspace)
    {
        var document = LoadXmlDocument(xml);
        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest")
            .Select(element => new
            {
                Id = element.Attribute("id")?.Value ?? "",
                Method = element.Descendants().FirstOrDefault(child => child.Name.LocalName == "TestMethod")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Method, StringComparer.OrdinalIgnoreCase);

        var tests = new List<TestCaseItem>();
        foreach (var element in document.Descendants().Where(item => item.Name.LocalName == "UnitTestResult"))
        {
            string outcome = element.Attribute("outcome")?.Value ?? "";
            string status = MapTrxOutcome(outcome);
            string fullName = element.Attribute("testName")?.Value ?? "(unnamed test)";
            string testId = element.Attribute("testId")?.Value ?? "";
            definitions.TryGetValue(testId, out var method);
            string methodName = method?.Attribute("name")?.Value ?? fullName.Split('.').LastOrDefault() ?? fullName;
            string suite = method?.Attribute("className")?.Value ?? GetSuiteName(fullName);
            string? message = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "Message")?.Value.Trim();
            string? stackTrace = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "StackTrace")?.Value;
            var (filePath, line) = ParseStackLocation(stackTrace, workspace);

            tests.Add(new TestCaseItem
            {
                Id = testId,
                Name = methodName,
                Suite = suite,
                Status = status,
                Duration = FormatDuration(element.Attribute("duration")?.Value),
                Error = status == "failed" ? Truncate(message, 2000) : null,
                FilePath = filePath,
                Line = line
            });
        }
        return tests;
    }

    public static List<TestCaseItem> ParseJUnitXml(string xml, string workspace)
    {
        var document = LoadXmlDocument(xml);
        var tests = new List<TestCaseItem>();
        foreach (var element in document.Descendants().Where(item => item.Name.LocalName == "testcase"))
        {
            var failure = element.Elements().FirstOrDefault(item => item.Name.LocalName is "failure" or "error");
            bool skipped = element.Elements().Any(item => item.Name.LocalName == "skipped");
            string status = failure != null ? "failed" : skipped ? "skipped" : "passed";
            string? filePath = element.Attribute("file")?.Value;
            if (!string.IsNullOrWhiteSpace(filePath) && !Path.IsPathRooted(filePath))
                filePath = Path.GetFullPath(Path.Combine(workspace, filePath));
            if (!string.IsNullOrWhiteSpace(filePath) && !IsPathWithinWorkspace(workspace, filePath))
                filePath = null;
            int? line = int.TryParse(element.Attribute("line")?.Value, out var parsedLine) ? parsedLine : null;
            string suite = element.Attribute("classname")?.Value ??
                           element.Ancestors().FirstOrDefault(item => item.Name.LocalName == "testsuite")?.Attribute("name")?.Value ??
                           "Tests";
            string? error = failure == null
                ? null
                : failure.Attribute("message")?.Value ?? failure.Value.Trim();

            tests.Add(new TestCaseItem
            {
                Id = $"{suite}.{element.Attribute("name")?.Value}",
                Name = element.Attribute("name")?.Value ?? "(unnamed test)",
                Suite = suite,
                Status = status,
                Duration = FormatSeconds(element.Attribute("time")?.Value),
                Error = Truncate(error, 2000),
                FilePath = filePath,
                Line = line
            });
        }
        return tests;
    }

    public static List<TestCaseItem> ParseGoJson(string jsonLines, string workspace)
    {
        var tests = new Dictionary<string, TestCaseItem>(StringComparer.Ordinal);
        var output = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        foreach (var line in jsonLines.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                string testName = root.TryGetProperty("Test", out var testNode) ? testNode.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(testName)) continue;
                string package = root.TryGetProperty("Package", out var packageNode) ? packageNode.GetString() ?? "Go Package" : "Go Package";
                string key = $"{package}::{testName}";
                string action = root.TryGetProperty("Action", out var actionNode) ? actionNode.GetString() ?? "" : "";

                if (action == "output" && root.TryGetProperty("Output", out var outputNode))
                {
                    if (!output.TryGetValue(key, out var builder))
                    {
                        builder = new StringBuilder();
                        output[key] = builder;
                    }
                    if (builder.Length < 8000) builder.Append(outputNode.GetString());
                    continue;
                }
                if (action is not ("pass" or "fail" or "skip")) continue;

                double elapsed = root.TryGetProperty("Elapsed", out var elapsedNode) && elapsedNode.TryGetDouble(out var parsedElapsed)
                    ? parsedElapsed
                    : 0;
                tests[key] = new TestCaseItem
                {
                    Id = key,
                    Name = testName,
                    Suite = package,
                    Status = action == "pass" ? "passed" : action == "fail" ? "failed" : "skipped",
                    Duration = elapsed > 0 ? FormatMilliseconds(elapsed * 1000) : "",
                    Error = action == "fail" && output.TryGetValue(key, out var failureOutput)
                        ? Truncate(failureOutput.ToString().Trim(), 2000)
                        : null
                };
            }
        }
        return tests.Values.ToList();
    }

    private static XDocument LoadXmlDocument(string xml)
    {
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 50_000_000
        });
        return XDocument.Load(xmlReader, LoadOptions.None);
    }

    private static string ReadStructuredResultFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 50 * 1024 * 1024)
            throw new InvalidDataException("The structured test result is larger than 50 MiB.");
        return File.ReadAllText(path);
    }

    private static string MapTrxOutcome(string outcome)
    {
        if (outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)) return "passed";
        if (outcome.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase) ||
            outcome.Equals("Inconclusive", StringComparison.OrdinalIgnoreCase) ||
            outcome.Equals("Warning", StringComparison.OrdinalIgnoreCase)) return "skipped";
        return "failed";
    }

    private static string GetSuiteName(string fullName)
    {
        int lastDot = fullName.LastIndexOf('.');
        return lastDot > 0 ? fullName[..lastDot] : "Tests";
    }

    private static (string? FilePath, int? Line) ParseStackLocation(string? stackTrace, string workspace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return (null, null);
        var match = Regex.Match(stackTrace, @"\sin\s+(.+):line\s+([0-9]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return (null, null);
        string path = match.Groups[1].Value.Trim();
        if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(workspace, path));
        if (!IsPathWithinWorkspace(workspace, path)) return (null, null);
        return (path, int.TryParse(match.Groups[2].Value, out var line) ? line : null);
    }

    // Containment (reparse points, UNC, ADS, trailing dots/spaces) lives in PathPolicy.
    private static bool IsPathWithinWorkspace(string workspace, string path) =>
        PathPolicy.IsInsideWorkspace(workspace, path);

    private static string FormatDuration(string? value)
    {
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)
            ? FormatMilliseconds(duration.TotalMilliseconds)
            : value ?? "";
    }

    private static string FormatSeconds(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? FormatMilliseconds(seconds * 1000)
            : "";
    }

    private static string FormatMilliseconds(double milliseconds)
    {
        return milliseconds < 1000
            ? $"{milliseconds:0.##} ms"
            : $"{milliseconds / 1000:0.###} s";
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    public static void CancelActiveRun()
    {
        lock (_lock)
        {
            try
            {
                if (_activeTestProcess != null && !_activeTestProcess.HasExited)
                {
                    _activeTestProcess.Kill(true);
                }
            }
            catch (Exception) { /* test process already exited */ }
            finally
            {
                _activeTestProcess = null;
            }
        }
    }

    public static void ParseConsoleTestResults(TestRunResult result, string workspace)
    {
        var lines = result.RawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // 1. .NET Runner (dotnet test)
        if (result.Runner.StartsWith("dotnet"))
        {
            // Passed  Namespace.ClassName.MethodName [12 ms]
            // Failed  Namespace.ClassName.MethodName [34 ms]
            var dotnetRegex = new Regex(@"^\s*(Passed|Failed|Skipped)\s+([A-Za-z0-9_.]+)(?:\s+\[([0-9]+(?:\.[0-9]+)?\s*m?s)\])?", RegexOptions.IgnoreCase);
            var fileLineRegex = new Regex(@"at\s+.*in\s+(.+):line\s+([0-9]+)", RegexOptions.IgnoreCase);

            TestCaseItem? currentFailed = null;

            foreach (var line in lines)
            {
                var match = dotnetRegex.Match(line);
                if (match.Success)
                {
                    var status = match.Groups[1].Value.ToLowerInvariant();
                    var fullName = match.Groups[2].Value;
                    var duration = match.Groups[3].Success ? match.Groups[3].Value : "";

                    var lastDot = fullName.LastIndexOf('.');
                    var suite = lastDot > 0 ? fullName.Substring(0, lastDot) : "Tests";
                    var name = lastDot > 0 ? fullName.Substring(lastDot + 1) : fullName;

                    var item = new TestCaseItem
                    {
                        Id = $"test_{result.Tests.Count + 1}",
                        Name = name,
                        Suite = suite,
                        Status = status,
                        Duration = duration
                    };

                    result.Tests.Add(item);

                    if (status == "passed") result.Passed++;
                    else if (status == "failed")
                    {
                        result.Failed++;
                        currentFailed = item;
                    }
                    else if (status == "skipped") result.Skipped++;

                    continue;
                }

                // If currently capturing failed test details
                if (currentFailed != null)
                {
                    var flMatch = fileLineRegex.Match(line);
                    if (flMatch.Success)
                    {
                        currentFailed.FilePath = flMatch.Groups[1].Value.Trim();
                        if (int.TryParse(flMatch.Groups[2].Value, out int lineNum))
                        {
                            currentFailed.Line = lineNum;
                        }
                    }
                    else if (line.Contains("Error Message:") || line.Contains("ErrorMessage:"))
                    {
                        currentFailed.Error = line.Trim();
                    }
                }
            }

            result.Total = result.Tests.Count;
            return;
        }

        // 2. Python (pytest)
        if (result.Runner.StartsWith("pytest"))
        {
            // Test_file.py::test_func PASSED [ 50%]
            // Test_file.py::test_func FAILED [100%]
            var pytestRegex = new Regex(@"^([A-Za-z0-9_/\\.-]+)::([A-Za-z0-9_]+)\s+(PASSED|FAILED|SKIPPED)", RegexOptions.IgnoreCase);

            foreach (var line in lines)
            {
                var match = pytestRegex.Match(line);
                if (match.Success)
                {
                    var file = match.Groups[1].Value;
                    var name = match.Groups[2].Value;
                    var status = match.Groups[3].Value.ToLowerInvariant();

                    var item = new TestCaseItem
                    {
                        Id = $"test_{result.Tests.Count + 1}",
                        Name = name,
                        Suite = Path.GetFileName(file),
                        FilePath = Path.IsPathRooted(file) ? file : Path.Combine(workspace, file),
                        Status = status
                    };

                    result.Tests.Add(item);

                    if (status == "passed") result.Passed++;
                    else if (status == "failed") result.Failed++;
                    else result.Skipped++;
                }
            }

            result.Total = result.Tests.Count;
            return;
        }

        // 3. Node / Vitest / Jest (npm test)
        if (result.Runner.StartsWith("npm"))
        {
            // √ test name (12ms)
            // × test name (34ms)
            var nodeRegex = new Regex(@"^\s*([√✓x×!-])\s+(.+?)(?:\s+\(([0-9]+(?:\.[0-9]+)?\s*m?s)\))?$", RegexOptions.IgnoreCase);

            foreach (var line in lines)
            {
                var match = nodeRegex.Match(line);
                if (match.Success)
                {
                    var symbol = match.Groups[1].Value;
                    var name = match.Groups[2].Value.Trim();
                    var duration = match.Groups[3].Success ? match.Groups[3].Value : "";

                    string status = (symbol == "√" || symbol == "✓") ? "passed" : "failed";

                    var item = new TestCaseItem
                    {
                        Id = $"test_{result.Tests.Count + 1}",
                        Name = name,
                        Suite = "Node Tests",
                        Status = status,
                        Duration = duration
                    };

                    result.Tests.Add(item);

                    if (status == "passed") result.Passed++;
                    else result.Failed++;
                }
            }

            result.Total = result.Tests.Count;
            return;
        }

        // 4. Rust (cargo test)
        if (result.Runner.StartsWith("cargo"))
        {
            // Test tests::test_add ... ok
            // Test tests::test_fail ... FAILED
            var cargoRegex = new Regex(@"^test\s+([A-Za-z0-9_:]+)\s+\.\.\.\s+(ok|FAILED|ignored)", RegexOptions.IgnoreCase);

            foreach (var line in lines)
            {
                var match = cargoRegex.Match(line);
                if (match.Success)
                {
                    var fullName = match.Groups[1].Value;
                    var rawStatus = match.Groups[2].Value.ToLowerInvariant();
                    var status = rawStatus == "ok" ? "passed" : (rawStatus == "failed" ? "failed" : "skipped");

                    var parts = fullName.Split(new[] { "::" }, StringSplitOptions.None);
                    var suite = parts.Length > 1 ? string.Join("::", parts, 0, parts.Length - 1) : "Rust Tests";
                    var name = parts[parts.Length - 1];

                    var item = new TestCaseItem
                    {
                        Id = $"test_{result.Tests.Count + 1}",
                        Name = name,
                        Suite = suite,
                        Status = status
                    };

                    result.Tests.Add(item);

                    if (status == "passed") result.Passed++;
                    else if (status == "failed") result.Failed++;
                    else result.Skipped++;
                }
            }

            result.Total = result.Tests.Count;
            return;
        }

        // 5. Go (go test)
        if (result.Runner.StartsWith("go"))
        {
            // --- PASS: TestAdd (0.00s)
            // --- FAIL: TestSub (0.01s)
            var goRegex = new Regex(@"^---\s+(PASS|FAIL|SKIP):\s+([A-Za-z0-9_]+)\s*(?:\((.+?)\))?", RegexOptions.IgnoreCase);

            foreach (var line in lines)
            {
                var match = goRegex.Match(line);
                if (match.Success)
                {
                    var rawStatus = match.Groups[1].Value.ToLowerInvariant();
                    var name = match.Groups[2].Value;
                    var duration = match.Groups[3].Success ? match.Groups[3].Value : "";

                    var status = rawStatus == "pass" ? "passed" : (rawStatus == "fail" ? "failed" : "skipped");

                    var item = new TestCaseItem
                    {
                        Id = $"test_{result.Tests.Count + 1}",
                        Name = name,
                        Suite = "Go Package",
                        Status = status,
                        Duration = duration
                    };

                    result.Tests.Add(item);

                    if (status == "passed") result.Passed++;
                    else if (status == "failed") result.Failed++;
                    else result.Skipped++;
                }
            }

            result.Total = result.Tests.Count;
            return;
        }

        // Generic fallback: count non-empty output
        result.Total = result.Tests.Count;
    }
}

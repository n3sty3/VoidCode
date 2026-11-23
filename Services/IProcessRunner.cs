namespace VoidCode.Services;

/// <summary>What one captured process run produced. Immutable so a fake can return the same value twice.</summary>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// (first seam): every external process the host starts - git today, the test runner and
/// The language server next - goes through this instead of touching <c>Process</c> directly, so a unit
/// Test can drive real parsing code with canned output and no repository, toolchain or subprocess.
/// Both overloads exist because the two calling styles differ: a single git-style command line, and an
/// Argument list with optional stdin for patch application (no shell quoting in between).
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with one command line, capturing stdout/stderr as UTF-8.</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="fileName"/> with the arguments passed one by one (no quoting surprises) and,
    /// When <paramref name="standardInput"/> is set, writes it to the child's stdin before waiting.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? standardInput = null,
        CancellationToken cancellationToken = default);
}
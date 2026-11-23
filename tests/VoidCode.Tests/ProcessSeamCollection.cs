using Xunit;

namespace VoidCode.Tests;

// GitService.ProcessRunner is process-wide static state: a test that swaps it must not run next to
// Another test that expects the real git process.
[CollectionDefinition("Process seam", DisableParallelization = true)]
public class ProcessSeamCollection
{
}
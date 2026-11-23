using Xunit;

namespace VoidCode.Tests;

// Tests that rely on process-wide environment variables must not run next to each other.
[CollectionDefinition("Environment variables", DisableParallelization = true)]
public class EnvironmentVariableCollection
{
}
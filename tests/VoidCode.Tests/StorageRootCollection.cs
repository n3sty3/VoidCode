using Xunit;

namespace VoidCode.Tests;

// StorageService.Root is process-wide state (an in-memory override plus VOIDCODE_STORAGE_ROOT), so any
// Test that redirects it must not run next to another test that reads or writes the profile.
[CollectionDefinition("Storage root", DisableParallelization = true)]
public class StorageRootCollection
{
}
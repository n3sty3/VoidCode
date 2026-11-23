using System.Windows;

// The agent split moved code behind `internal` seams (ToolCatalog, ToolExecutor,
// ContextBuilder, Prompts, AgentLoop). The test project is the only friend, so the seams stay closed
// To everything else in the process.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VoidCode.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // Or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // App, or any theme specific resource dictionaries)
)]

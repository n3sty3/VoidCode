using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The .NET and Python debuggers are not implemented, so they must report
// "unsupported" honestly instead of emitting a "Starting..." toast and silently doing nothing.
public class DebugCapabilityTests
{
    private static async Task<List<(string Action, string Json)>> CaptureStartAsync(string file)
    {
        var events = new List<(string Action, string Json)>();
        DebugService.Initialize((action, payload) =>
        {
            lock (events) events.Add((action, JsonSerializer.Serialize(payload)));
        });

        await DebugService.StartDebuggingAsync(file, "C:\\proj");
        await DebugService.StopDebuggingAsync();

        lock (events) return new List<(string, string)>(events);
    }

    [Fact]
    public async Task DotNet_And_Python_ReportUnsupported_WithoutStartingToast()
    {
        var cs = await CaptureStartAsync("C:\\proj\\Program.cs");
        var py = await CaptureStartAsync("C:\\proj\\main.py");
        var all = cs.Concat(py).ToList();

        // No toast may claim we are starting a .NET or Python session.
        var toastTexts = all.Where(e => e.Action == "toast").Select(e => e.Json).ToList();
        Assert.DoesNotContain(toastTexts, t => t.Contains("Starting", StringComparison.OrdinalIgnoreCase));

        // Both runtimes must report the honest capability event.
        var unsupported = all.Where(e => e.Action == "debug_unsupported").Select(e => e.Json).ToList();
        Assert.Contains(unsupported, s => s.Contains("dotnet"));
        Assert.Contains(unsupported, s => s.Contains("python"));

        // The session must end idle, never stuck in "running".
        Assert.Equal("idle", DebugService.DebugState);
    }
}

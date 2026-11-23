using System;
using System.IO;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// LoggingService.Swallowed is the sink that replaces empty catch blocks. It must
// Record where and what happened to the log file, and it must never throw.
public class LoggingTests
{
    [Fact]
    public void Swallowed_WritesWhereAndExceptionToTheLogFile()
    {
        var where = "UnitTest/" + Guid.NewGuid().ToString("N");
        LoggingService.Swallowed(new InvalidOperationException("boom-t3-1"), where);

        Assert.True(File.Exists(LoggingService.LogFilePath));
        var text = File.ReadAllText(LoggingService.LogFilePath);
        Assert.Contains("Swallowed in " + where, text);
        Assert.Contains("boom-t3-1", text);
    }

    [Fact]
    public void Swallowed_NeverThrows_ForNullOrWeirdInputs()
    {
        var ex = Record.Exception(() =>
        {
            LoggingService.Swallowed(null, "UnitTest/null");
            LoggingService.Swallowed(new Exception(string.Empty), "UnitTest/empty");
        });

        Assert.Null(ex);
    }
}

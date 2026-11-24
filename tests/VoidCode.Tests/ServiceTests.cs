using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// Two tests here reach the profile through StorageService (checkpoints and the recovery journal), so the
// Class joins the "Storage root" collection and every write goes to a temp root.
[Collection("Storage root")]
public class ServiceTests : IDisposable
{
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "voidcode-service-store-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _storage;

    public ServiceTests()
    {
        Directory.CreateDirectory(_storageRoot);
        _storage = StorageService.UseRoot(_storageRoot);
    }

    public void Dispose()
    {
        _storage.Dispose();
        try { Directory.Delete(_storageRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void FileSystemService_IgnoredDirs_ContainsCriticalDirectories()
    {
        Assert.Contains(".git", FileSystemService.IgnoredDirs);
        Assert.Contains("node_modules", FileSystemService.IgnoredDirs);
        Assert.Contains("bin", FileSystemService.IgnoredDirs);
        Assert.Contains("obj", FileSystemService.IgnoredDirs);
        Assert.Contains(".voidcode", FileSystemService.IgnoredDirs);
    }

    [Fact]
    public void FileSystemService_Checkpoints_CanCreateAndRestore()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"voidcode_test_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "Original content 123");
            var cp = FileSystemService.CreateCheckpoint(tempFile);
            Assert.True(cp.IsRestorable);

            File.WriteAllText(tempFile, "Mutated content 456");
            var restored = FileSystemService.RestoreCheckpoint(cp.Id, out var restoredPath);

            Assert.True(restored);
            Assert.Equal(tempFile, restoredPath);
            Assert.Equal("Original content 123", File.ReadAllText(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void GitStatusShape_SeparatesStagedAndWorkingColumns()
    {
        var sampleStatus = "M  src/index.js\n M src/styles.css\n?? new_file.txt\nA  added.ts\nD  deleted.cs";
        var lines = sampleStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var staged = lines.Count(line => line.Length >= 3 && line[0] != ' ' && line[0] != '?');
        var working = lines.Count(line => line.Length >= 3 && (line[1] != ' ' || (line[0] == '?' && line[1] == '?')));

        Assert.Equal(3, staged);
        Assert.Equal(2, working);
    }

    [Fact]
    public void CodeIndexTokenShape_RecognizesSymbols()
    {
        var input = "public class FileSystemService { void RunTestRunner() {} }";
        var tokens = Regex.Matches(input, @"[A-Za-z0-9_]+").Select(match => match.Value).ToList();

        Assert.Contains("FileSystemService", tokens);
        Assert.Contains("RunTestRunner", tokens);
    }

    [Fact]
    public void DebugService_Breakpoints_CanToggleAndRetrieve()
    {
        var dummyPath = @"C:\dummy\project\app.js";
        DebugService.ClearBreakpoints(dummyPath);

        DebugService.ToggleBreakpoint(dummyPath, 15);
        var breakpoints = DebugService.GetBreakpoints(dummyPath);
        Assert.Single(breakpoints);
        Assert.Equal(15, breakpoints[0].Line);

        DebugService.ToggleBreakpoint(dummyPath, 15);
        Assert.Empty(DebugService.GetBreakpoints(dummyPath));
    }

    [Fact]
    public void LoggingService_WritesLogEntry()
    {
        LoggingService.LogInfo("Test log entry for unit verification", "UnitTests");

        Assert.True(File.Exists(LoggingService.LogFilePath));
        Assert.Contains("Test log entry for unit verification", File.ReadAllText(LoggingService.LogFilePath));
    }

    [Fact]
    public void FileSystemService_WriteFile_IsAtomicAndPreservesUtf8Bom()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"voidcode_atomic_{Guid.NewGuid():N}");
        var tempFile = Path.Combine(tempDir, "sample.txt");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(tempFile, "before", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            FileSystemService.WriteFile(tempFile, "after");

            var bytes = File.ReadAllBytes(tempFile);
            Assert.True(bytes.Length >= 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
            Assert.Equal("after", File.ReadAllText(tempFile));
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FileSystemService_TextMetadata_DetectsEncodingAndLineEndings()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"voidcode_encoding_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "one\r\ntwo\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var content = FileSystemService.ReadFile(tempFile);
            var metadata = FileSystemService.GetTextFileMetadata(tempFile, content);

            Assert.Equal("UTF-16 LE", metadata.Encoding);
            Assert.Equal("CRLF", metadata.Eol);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void StorageService_RecoveryJournal_RoundTripsAndClears()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"voidcode_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var state = new WorkspaceRecoveryState
            {
                Workspace = workspace,
                ActivePath = Path.Combine(workspace, "Program.cs"),
                Buffers =
                {
                    new RecoveryBufferState
                    {
                        Path = Path.Combine(workspace, "Program.cs"),
                        Content = "unsaved content",
                        CursorLine = 4,
                        CursorColumn = 9
                    }
                }
            };

            StorageService.SaveRecoveryState(state);
            var loaded = StorageService.LoadRecoveryState(workspace);

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Buffers);
            Assert.Equal("unsaved content", loaded.Buffers[0].Content);
            Assert.Equal(4, loaded.Buffers[0].CursorLine);
            Assert.Equal(9, loaded.Buffers[0].CursorColumn);

            StorageService.ClearRecoveryState(workspace);
            Assert.Null(StorageService.LoadRecoveryState(workspace));
        }
        finally
        {
            StorageService.ClearRecoveryState(workspace);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void AppSettings_Serialization_DoesNotExposeApiKey()
    {
        Assert.False(new AppSettings().AutoApproveTools);

        var settings = new AppSettings
        {
            ApiKey = "super-secret-test-value",
            ApiKeyConfigured = true
        };

        var json = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("super-secret-test-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ApiKey\"", json, StringComparison.Ordinal);
        Assert.Contains("ApiKeyConfigured", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsDpapi_RoundTripsForCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plain = Encoding.UTF8.GetBytes("voidcode-dpapi-test");
        var entropy = Encoding.UTF8.GetBytes("VoidCode.Provider.ApiKey.v1");

        var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        var decrypted = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);

        Assert.NotEqual(Convert.ToBase64String(plain), Convert.ToBase64String(encrypted));
        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public async Task CodebaseIndexService_UpdatesAndRemovesChangedFiles()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"voidcode_index_{Guid.NewGuid():N}");
        var file = Path.Combine(workspace, "Program.cs");
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(file, "public class OriginalSymbol { }");
            await CodebaseIndexService.BuildOrUpdateIndexAsync(workspace);
            Assert.NotEmpty(await CodebaseIndexService.SearchAsync(workspace, "OriginalSymbol"));

            File.WriteAllText(file, "public class UpdatedSymbol { }");
            await CodebaseIndexService.UpdateFileAsync(workspace, file);
            Assert.NotEmpty(await CodebaseIndexService.SearchAsync(workspace, "UpdatedSymbol"));
            var oldSymbolResults = await CodebaseIndexService.SearchAsync(workspace, "OriginalSymbol");
            Assert.DoesNotContain(oldSymbolResults, result => result.Symbols.Contains("OriginalSymbol"));

            File.Delete(file);
            await CodebaseIndexService.RemoveFileAsync(workspace, file);
            Assert.Empty(await CodebaseIndexService.SearchAsync(workspace, "UpdatedSymbol"));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task WorkspaceIgnoreMatcher_AppliesSharedIgnoreFilesAndNegation()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"voidcode_ignore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "ignored"));
            Directory.CreateDirectory(Path.Combine(workspace, "node_modules"));
            File.WriteAllText(Path.Combine(workspace, ".gitignore"), "ignored/\n*.log\n!important.log\n");
            File.WriteAllText(Path.Combine(workspace, "ignored", "hidden.cs"), "class Hidden {}");
            File.WriteAllText(Path.Combine(workspace, "trace.log"), "ignore me");
            File.WriteAllText(Path.Combine(workspace, "important.log"), "keep me");
            File.WriteAllText(Path.Combine(workspace, "visible.cs"), "class Visible {}");

            var matcher = new WorkspaceIgnoreMatcher(workspace, FileSystemService.IgnoredDirs);

            Assert.True(matcher.IsIgnored(Path.Combine(workspace, "ignored"), isDirectory: true));
            Assert.True(matcher.IsIgnored(Path.Combine(workspace, "trace.log")));
            Assert.False(matcher.IsIgnored(Path.Combine(workspace, "important.log")));
            Assert.True(matcher.IsIgnored(Path.Combine(workspace, "node_modules"), isDirectory: true));

            var root = FileSystemService.GetDirectoryTree(workspace);
            Assert.DoesNotContain(root.Children!, node => node.Name == "ignored");
            Assert.DoesNotContain(root.Children!, node => node.Name == "trace.log");
            Assert.Contains(root.Children!, node => node.Name == "important.log");

            var files = FileSystemService.GetAllFilesFlat(workspace);
            Assert.DoesNotContain(files, item => item.ToString()!.Contains("hidden.cs", StringComparison.Ordinal));
            Assert.Equal(0, FileSystemService.SearchWorkspaceStructured(workspace, "Hidden").TotalMatches);
            Assert.Equal(1, FileSystemService.SearchWorkspaceStructured(workspace, "Visible").TotalMatches);

            await CodebaseIndexService.BuildOrUpdateIndexAsync(workspace);
            Assert.Empty(await CodebaseIndexService.SearchAsync(workspace, "Hidden"));
            Assert.NotEmpty(await CodebaseIndexService.SearchAsync(workspace, "Visible"));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void FileSystemService_LazyDirectoryChildren_HasNoDepthCutoff()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"voidcode_tree_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var current = workspace;
            for (int depth = 0; depth < 10; depth++)
            {
                current = Path.Combine(current, $"level-{depth}");
                Directory.CreateDirectory(current);
            }
            File.WriteAllText(Path.Combine(current, "deep.cs"), "class Deep {}");

            var cursor = workspace;
            for (int depth = 0; depth < 10; depth++)
            {
                var children = FileSystemService.GetDirectoryChildren(workspace, cursor);
                var next = Assert.Single(children, node => node.IsDirectory);
                Assert.True(next.HasChildren);
                cursor = next.Path;
            }

            var leafChildren = FileSystemService.GetDirectoryChildren(workspace, cursor);
            Assert.Contains(leafChildren, node => node.Name == "deep.cs");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void FileSystemService_SearchHonorsCancellation()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"voidcode_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "sample.cs"), "class Sample {}");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                FileSystemService.SearchWorkspaceStructured(workspace, "Sample", cancellationToken: cts.Token));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void FileSystemService_DirectoryPages_BoundRenderedChildren()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"voidcode_page_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            for (int i = 0; i < 120; i++)
            {
                File.WriteAllText(Path.Combine(workspace, $"file-{i:000}.txt"), i.ToString());
            }

            var first = FileSystemService.GetDirectoryChildrenPage(workspace, workspace, offset: 0, limit: 50);
            var second = FileSystemService.GetDirectoryChildrenPage(workspace, workspace, offset: 50, limit: 50);

            Assert.Equal(120, first.Total);
            Assert.Equal(50, first.Children.Count);
            Assert.True(first.HasMore);
            Assert.Equal(50, second.Offset);
            Assert.Equal(50, second.Children.Count);
            Assert.True(second.HasMore);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void LspService_ParsesLocationsWorkspaceEditsAndNestedSymbols()
    {
        var fileA = Path.Combine(Path.GetTempPath(), "lsp-a.cs");
        var fileB = Path.Combine(Path.GetTempPath(), "lsp-b.cs");
        var uriA = new Uri(fileA).AbsoluteUri;
        var uriB = new Uri(fileB).AbsoluteUri;

        var locations = new JsonArray
        {
            new JsonObject
            {
                ["uri"] = uriA,
                ["range"] = RangeNode(1, 2, 1, 7)
            }
        };
        var parsedLocations = LspService.ParseLocations(locations);
        Assert.Single(parsedLocations);
        Assert.Equal(fileA, parsedLocations[0].Path);
        Assert.Equal(2, parsedLocations[0].StartLine);
        Assert.Equal(3, parsedLocations[0].StartColumn);

        var workspaceEdit = new JsonObject
        {
            ["changes"] = new JsonObject
            {
                [uriA] = new JsonArray
                {
                    new JsonObject { ["range"] = RangeNode(0, 0, 0, 3), ["newText"] = "New" }
                }
            },
            ["documentChanges"] = new JsonArray
            {
                new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uriB },
                    ["edits"] = new JsonArray
                    {
                        new JsonObject { ["range"] = RangeNode(4, 1, 4, 4), ["newText"] = "Other" }
                    }
                }
            }
        };
        var edits = LspService.ParseWorkspaceEdit(workspaceEdit);
        Assert.Equal(2, edits.Count);
        Assert.Contains(edits, edit => edit.Path == fileA && edit.NewText == "New");
        Assert.Contains(edits, edit => edit.Path == fileB && edit.StartLine == 5);

        var symbols = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Container",
                ["kind"] = 5,
                ["range"] = RangeNode(0, 0, 8, 1),
                ["selectionRange"] = RangeNode(0, 6, 0, 15),
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "Run",
                        ["kind"] = 6,
                        ["range"] = RangeNode(2, 2, 3, 3),
                        ["selectionRange"] = RangeNode(2, 7, 2, 10)
                    }
                }
            }
        };
        var parsedSymbols = LspService.ParseDocumentSymbols(symbols, fileA);
        Assert.Equal(2, parsedSymbols.Count);
        Assert.Equal("Container", parsedSymbols[1].ContainerName);
        Assert.Equal("Run", parsedSymbols[1].Name);
    }

    [Fact]
    public void LspService_ParsesFormattingEdits()
    {
        var file = Path.Combine(Path.GetTempPath(), "format-target.cs");
        var uri = new Uri(file).AbsoluteUri;
        var response = new JsonArray
        {
            new JsonObject
            {
                ["range"] = RangeNode(0, 0, 0, 4),
                ["newText"] = "    "
            },
            new JsonObject
            {
                ["range"] = RangeNode(3, 8, 3, 8),
                ["newText"] = Environment.NewLine
            }
        };

        var edits = LspService.ParseTextEdits(uri, response);

        Assert.Equal(2, edits.Count);
        Assert.All(edits, edit => Assert.Equal(file, edit.Path));
        Assert.Equal(1, edits[0].StartLine);
        Assert.Equal(1, edits[0].StartColumn);
        Assert.Equal(5, edits[0].EndColumn);
        Assert.Equal("    ", edits[0].NewText);
        Assert.Equal(4, edits[1].StartLine);
        Assert.Equal(Environment.NewLine, edits[1].NewText);
    }

    [Fact]
    public void LspService_ParsesCodeActionsResolvedEditsAndCommandArguments()
    {
        var file = Path.Combine(Path.GetTempPath(), "code-action.cs");
        var uri = new Uri(file).AbsoluteUri;
        var actions = new JsonArray
        {
            new JsonObject
            {
                ["title"] = "Add missing import",
                ["kind"] = "quickfix",
                ["isPreferred"] = true,
                ["edit"] = new JsonObject
                {
                    ["changes"] = new JsonObject
                    {
                        [uri] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["range"] = RangeNode(0, 0, 0, 0),
                                ["newText"] = "using System;\n"
                            }
                        }
                    }
                },
                ["command"] = new JsonObject
                {
                    ["title"] = "Refresh imports",
                    ["command"] = "csharp.refreshImports",
                    ["arguments"] = new JsonArray(uri, 7, new JsonObject { ["force"] = true })
                },
                ["data"] = new JsonObject { ["ticket"] = 42 }
            },
            new JsonObject
            {
                ["title"] = "Unavailable refactor",
                ["kind"] = "refactor.extract",
                ["disabled"] = new JsonObject { ["reason"] = "Select an expression first." }
            }
        };

        var parsed = LspService.ParseCodeActions(actions);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Add missing import", parsed[0].Title);
        Assert.Equal("quickfix", parsed[0].Kind);
        Assert.True(parsed[0].IsPreferred);
        Assert.Single(parsed[0].Edits);
        Assert.Equal("using System;\n", parsed[0].Edits[0].NewText);
        Assert.Equal("csharp.refreshImports", parsed[0].Command);
        var commandArguments = Assert.IsType<JsonArray>(parsed[0].Arguments);
        Assert.Equal(3, commandArguments.Count);
        Assert.Equal(uri, commandArguments[0]!.GetValue<string>());
        Assert.Equal(7, commandArguments[1]!.GetValue<int>());
        Assert.True(Assert.IsType<JsonObject>(commandArguments[2])["force"]!.GetValue<bool>());
        var rawAction = Assert.IsType<JsonObject>(parsed[0].Raw);
        Assert.Equal(42, rawAction["data"]!["ticket"]!.GetValue<int>());
        Assert.Equal("Select an expression first.", parsed[1].DisabledReason);

        var resolved = LspService.ParseCodeAction(new JsonObject
        {
            ["title"] = "Organize imports",
            ["kind"] = "source.organizeImports",
            ["command"] = "workspace.organizeImports",
            ["arguments"] = new JsonArray(uri)
        });

        Assert.NotNull(resolved);
        Assert.Equal("workspace.organizeImports", resolved!.Command);
        Assert.Single(resolved.Arguments!);
        Assert.Equal(uri, resolved.Arguments![0]!.GetValue<string>());
    }

    [Fact]
    public void LspService_RejectsWorkspaceResourceOperationsInsteadOfPartiallyApplyingThem()
    {
        var file = Path.Combine(Path.GetTempPath(), "created-by-server.cs");
        var resourceEdit = new JsonObject
        {
            ["documentChanges"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "create",
                    ["uri"] = new Uri(file).AbsoluteUri
                }
            }
        };

        Assert.True(LspService.HasUnsupportedResourceOperations(resourceEdit));
        Assert.Empty(LspService.ParseWorkspaceEdit(resourceEdit));

        var action = LspService.ParseCodeAction(new JsonObject
        {
            ["title"] = "Create generated file",
            ["kind"] = "quickfix",
            ["edit"] = resourceEdit.DeepClone()
        });

        Assert.NotNull(action);
        Assert.Empty(action!.Edits);
        Assert.Contains("file create", action.DisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LspService_ParsesWorkspaceSymbolsWithAndWithoutRanges()
    {
        var fileA = Path.Combine(Path.GetTempPath(), "workspace-symbol-a.cs");
        var fileB = Path.Combine(Path.GetTempPath(), "workspace-symbol-b.cs");
        var response = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "AlphaService",
                ["kind"] = 5,
                ["containerName"] = "Services",
                ["location"] = new JsonObject
                {
                    ["uri"] = new Uri(fileA).AbsoluteUri,
                    ["range"] = RangeNode(11, 4, 11, 16)
                }
            },
            new JsonObject
            {
                ["name"] = "Beta",
                ["kind"] = 12,
                ["location"] = new JsonObject
                {
                    ["uri"] = new Uri(fileB).AbsoluteUri
                }
            }
        };

        var symbols = LspService.ParseWorkspaceSymbols(response);

        Assert.Equal(2, symbols.Count);
        Assert.Equal("AlphaService", symbols[0].Name);
        Assert.Equal("Services", symbols[0].ContainerName);
        Assert.Equal(fileA, symbols[0].Path);
        Assert.Equal(12, symbols[0].StartLine);
        Assert.Equal(5, symbols[0].StartColumn);
        Assert.Equal(fileB, symbols[1].Path);
        Assert.Equal(1, symbols[1].StartLine);
        Assert.Equal(1, symbols[1].StartColumn);
    }

    [Fact]
    public void LspService_ParsesInlayHintsAndRemapsSemanticTokenLegends()
    {
        var file = Path.Combine(Path.GetTempPath(), "semantic-target.cs");
        var uri = new Uri(file).AbsoluteUri;
        var hints = LspService.ParseInlayHints(new JsonArray
        {
            new JsonObject
            {
                ["position"] = new JsonObject { ["line"] = 2, ["character"] = 8 },
                ["label"] = new JsonArray
                {
                    new JsonObject { ["value"] = "name" },
                    new JsonObject { ["value"] = ": " }
                },
                ["kind"] = 2,
                ["paddingLeft"] = true,
                ["tooltip"] = new JsonObject { ["kind"] = "markdown", ["value"] = "Parameter name" },
                ["textEdits"] = new JsonArray
                {
                    new JsonObject { ["range"] = RangeNode(2, 8, 2, 8), ["newText"] = "name: " }
                }
            }
        }, uri);

        var hint = Assert.Single(hints);
        Assert.Equal(3, hint.Line);
        Assert.Equal(9, hint.Column);
        Assert.Equal("name: ", hint.Label);
        Assert.Equal(2, hint.Kind);
        Assert.True(hint.PaddingLeft);
        Assert.Equal("Parameter name", hint.Tooltip);
        Assert.Single(hint.TextEdits);
        Assert.Equal(file, hint.TextEdits[0].Path);

        var semantic = LspServerSession.RemapSemanticTokens(
            new JsonObject
            {
                ["resultId"] = "semantic-7",
                ["data"] = new JsonArray(
                    0, 0, 5, 0, 5,
                    1, 2, 4, 1, 2,
                    0, 6, 3, 2, 0)
            },
            new[] { "method", "class", "customToken" },
            new[] { "static", "readonly", "customModifier" });

        Assert.Equal("semantic-7", semantic.ResultId);
        Assert.Equal(15, semantic.Data.Count);
        Assert.Equal((uint)13, semantic.Data[3]); // method
        Assert.Equal((uint)8, semantic.Data[4]); // static; unsupported custom modifier is ignored
        Assert.Equal((uint)2, semantic.Data[8]); // class
        Assert.Equal((uint)4, semantic.Data[9]); // readonly
        Assert.Equal((uint)8, semantic.Data[13]); // unknown token type falls back to variable
    }

    [Fact]
    public void TestService_ParsesTrxResultsWithFailureLocations()
    {
        var workspace = Path.GetTempPath();
        var sourceFile = Path.Combine(workspace, "CalculatorTests.cs");
        var escapedSource = System.Security.SecurityElement.Escape(sourceFile);
        var trx = $"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions>
                <UnitTest id="passed-id"><TestMethod className="Demo.CalculatorTests" name="Adds" /></UnitTest>
                <UnitTest id="failed-id"><TestMethod className="Demo.CalculatorTests" name="Subtracts" /></UnitTest>
                <UnitTest id="skip-id"><TestMethod className="Demo.CalculatorTests" name="Future" /></UnitTest>
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="passed-id" testName="Demo.CalculatorTests.Adds" outcome="Passed" duration="00:00:00.012" />
                <UnitTestResult testId="failed-id" testName="Demo.CalculatorTests.Subtracts" outcome="Failed" duration="00:00:01.250">
                  <Output><ErrorInfo><Message>Expected 2 but found 3</Message><StackTrace>at Demo.CalculatorTests.Subtracts() in {escapedSource}:line 42</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
                <UnitTestResult testId="skip-id" testName="Demo.CalculatorTests.Future" outcome="NotExecuted" />
              </Results>
            </TestRun>
            """;

        var tests = TestService.ParseTrx(trx, workspace);

        Assert.Equal(3, tests.Count);
        Assert.Equal("passed", tests[0].Status);
        Assert.Equal("12 ms", tests[0].Duration);
        Assert.Equal("failed", tests[1].Status);
        Assert.Equal("Expected 2 but found 3", tests[1].Error);
        Assert.Equal(sourceFile, tests[1].FilePath);
        Assert.Equal(42, tests[1].Line);
        Assert.Equal("skipped", tests[2].Status);
    }

    [Fact]
    public void TestService_ParsesJUnitAndGoJsonResults()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "voidcode-structured-tests");
        var junit = """
            <testsuites>
              <testsuite name="api">
                <testcase classname="tests.test_api" name="test_ok" file="tests/test_api.py" line="8" time="0.025" />
                <testcase classname="tests.test_api" name="test_bad" file="tests/test_api.py" line="19" time="1.5">
                  <failure message="assert 500 == 200">traceback</failure>
                </testcase>
                <testcase classname="tests.test_api" name="test_later"><skipped /></testcase>
              </testsuite>
            </testsuites>
            """;

        var junitTests = TestService.ParseJUnitXml(junit, workspace);

        Assert.Equal(3, junitTests.Count);
        Assert.Equal("25 ms", junitTests[0].Duration);
        Assert.Equal(Path.GetFullPath(Path.Combine(workspace, "tests/test_api.py")), junitTests[0].FilePath);
        Assert.Equal(8, junitTests[0].Line);
        Assert.Equal("failed", junitTests[1].Status);
        Assert.Equal("assert 500 == 200", junitTests[1].Error);
        Assert.Equal("skipped", junitTests[2].Status);

        var goJson = string.Join('\n', new[]
        {
            "{\"Action\":\"run\",\"Package\":\"example/math\",\"Test\":\"TestAdd\"}",
            "{\"Action\":\"pass\",\"Package\":\"example/math\",\"Test\":\"TestAdd\",\"Elapsed\":0.004}",
            "{\"Action\":\"output\",\"Package\":\"example/math\",\"Test\":\"TestSub\",\"Output\":\"math_test.go:21: mismatch\\n\"}",
            "{\"Action\":\"fail\",\"Package\":\"example/math\",\"Test\":\"TestSub\",\"Elapsed\":1.2}",
            "{\"Action\":\"skip\",\"Package\":\"example/math\",\"Test\":\"TestFuture\",\"Elapsed\":0.0}"
        });
        var goTests = TestService.ParseGoJson(goJson, workspace);

        Assert.Equal(3, goTests.Count);
        Assert.Contains(goTests, test => test.Name == "TestAdd" && test.Status == "passed" && test.Duration == "4 ms");
        Assert.Contains(goTests, test => test.Name == "TestSub" && test.Status == "failed" && test.Error!.Contains("mismatch"));
        Assert.Contains(goTests, test => test.Name == "TestFuture" && test.Status == "skipped");
    }

    [Fact]
    public void TestService_ConsoleFallbackAndSlnxDetectionRemainAvailable()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"voidcode_slnx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "Sample.slnx"), "<Solution />");
            var runner = TestService.DetectTestRunner(workspace);
            Assert.NotNull(runner);
            Assert.Equal("dotnet test", runner!.Value.Runner);

            var result = new TestRunResult
            {
                Runner = "dotnet test",
                RawOutput = "Passed  Demo.Tests.Works [15 ms]\nFailed  Demo.Tests.Breaks [20 ms]"
            };
            TestService.ParseConsoleTestResults(result, workspace);

            Assert.Equal(2, result.Total);
            Assert.Equal(1, result.Passed);
            Assert.Equal(1, result.Failed);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void GitService_ParsesIndependentUnifiedDiffHunksAndLineNumbers()
    {
        var diff = """
            diff --git a/sample.txt b/sample.txt
            index 1111111..2222222 100644
            --- a/sample.txt
            +++ b/sample.txt
            @@ -1,4 +1,4 @@ heading
             alpha
            -old value
            +new value
             omega
            @@ -10,2 +10,3 @@ tail
             ten
            +eleven
             twelve
            """;

        var hunks = GitService.ParseUnifiedDiff(diff);

        Assert.Equal(2, hunks.Count);
        Assert.NotEqual(hunks[0].Id, hunks[1].Id);
        Assert.Equal("heading", hunks[0].Section);
        Assert.Equal(1, hunks[0].Additions);
        Assert.Equal(1, hunks[0].Deletions);
        Assert.Equal(2, hunks[0].Lines[1].OldLine);
        Assert.Null(hunks[0].Lines[1].NewLine);
        Assert.Equal(2, hunks[0].Lines[2].NewLine);
        Assert.Contains("--- a/sample.txt", hunks[0].Patch);
        Assert.Contains("@@ -1,4 +1,4 @@", hunks[0].Patch);
        Assert.DoesNotContain("@@ -10,2 +10,3 @@", hunks[0].Patch);
        Assert.Equal(1, hunks[1].Additions);
        Assert.Equal(0, hunks[1].Deletions);
        Assert.Contains("@@ -10,2 +10,3 @@", hunks[1].Patch);
    }

    private static JsonObject RangeNode(int startLine, int startColumn, int endLine, int endColumn)
    {
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startColumn },
            ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endColumn }
        };
    }

    [Fact]
    public void DescribeNonTextFile_Image_PointsToViewImage()
    {
        var (handled, message) = FileSystemService.DescribeNonTextFile("diagram.png");
        Assert.True(handled);
        Assert.Contains("view_image", message);
    }

    [Fact]
    public void DescribeNonTextFile_Binary_PointsToInspectBinary()
    {
        var (handled, message) = FileSystemService.DescribeNonTextFile("module.dll");
        Assert.True(handled);
        Assert.Contains("inspect_binary", message);
    }

    [Fact]
    public void DescribeNonTextFile_Text_IsNotHandled()
    {
        var (handled, _) = FileSystemService.DescribeNonTextFile("Program.cs");
        Assert.False(handled);
    }

    [Fact]
    public void GetImageDataUri_ReturnsPngDataUri()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"voidcode_img_{Guid.NewGuid():N}.png");
        try
        {
            // 1x1 transparent PNG.
            File.WriteAllBytes(tempFile, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));

            var dataUri = FileSystemService.GetImageDataUri(tempFile, out var sizeBytes);

            Assert.True(sizeBytes > 0);
            Assert.StartsWith("data:image/png;base64,", dataUri);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PlanMode_AllowsReadOnlyTools_ButBlocksMutations()
    {
        Assert.True(AgentEngine.IsAllowedInPlanMode("read_file"));
        Assert.True(AgentEngine.IsAllowedInPlanMode("view_image"));
        Assert.True(AgentEngine.IsAllowedInPlanMode("search_code"));

        Assert.False(AgentEngine.IsAllowedInPlanMode("write_file"));
        Assert.False(AgentEngine.IsAllowedInPlanMode("patch_file"));
        Assert.False(AgentEngine.IsAllowedInPlanMode("delete_file"));
        Assert.False(AgentEngine.IsAllowedInPlanMode("run_command"));
        Assert.False(AgentEngine.IsAllowedInPlanMode("web_fetch"));
    }
}

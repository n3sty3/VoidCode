using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("StorageRoot")]
public class TurnTransactionLedgerTests
{
    private static string Sha256File(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void MultiFileTurn_CreateEditDeleteMoveAndOver4MB_RevertRestoresByteIdenticalTree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "voidcode-turn-tx-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(workspace);

        using var _ = StorageService.UseRoot(Path.Combine(tempRoot, "profile"));
        try
        {
            var editedPath = Path.Combine(workspace, "edited.cs");
            var deletedPath = Path.Combine(workspace, "deleted.txt");
            var moveSrcPath = Path.Combine(workspace, "old-name.txt");
            var moveDstPath = Path.Combine(workspace, "new-name.txt");
            var createdPath = Path.Combine(workspace, "created.cs");
            var hugePath = Path.Combine(workspace, "huge-5mb.bin");

            File.WriteAllText(editedPath, "line1\nline2\nline3\n");
            File.WriteAllText(deletedPath, "important original data");
            File.WriteAllText(moveSrcPath, "movable payload");

            // > 4 MB binary file (5 MB) — proves copies bytes instead of dropping previous content
            var hugeBytes = new byte[5 * 1024 * 1024];
            new Random(42).NextBytes(hugeBytes);
            File.WriteAllBytes(hugePath, hugeBytes);

            var editedHash = Sha256File(editedPath);
            var deletedHash = Sha256File(deletedPath);
            var moveSrcHash = Sha256File(moveSrcPath);
            var hugeHash = Sha256File(hugePath);

            var tx = TurnTransactionLedger.BeginTurn(workspace);

            // 1. Edit a file TWICE in the same turn — pre-turn snapshot must retain the very first state
            tx.SnapshotBeforeMutation(editedPath, "edit");
            File.WriteAllText(editedPath, "line1\nchanged-once\nline3\n");
            tx.SnapshotBeforeMutation(editedPath, "edit");
            File.WriteAllText(editedPath, "line1\nchanged-twice\nline3\n");

            // 2. Create a new file
            tx.SnapshotBeforeMutation(createdPath, "create");
            File.WriteAllText(createdPath, "brand new");

            // 3. Delete an existing file
            tx.SnapshotBeforeMutation(deletedPath, "delete");
            File.Delete(deletedPath);

            // 4. Move a file
            tx.SnapshotBeforeMutation(moveSrcPath, "move_from");
            tx.SnapshotBeforeMutation(moveDstPath, "move_to");
            File.Move(moveSrcPath, moveDstPath);

            // 5. Overwrite a 5 MB file
            tx.SnapshotBeforeMutation(hugePath, "edit");
            File.WriteAllBytes(hugePath, new byte[] { 1, 2, 3, 4 });

            // 6. Record an irreversible command
            tx.RecordIrreversible("run_command", "dotnet build");

            // Revert the entire turn
            var revert = tx.RevertWholeTurn();

            Assert.Empty(revert.FailedFiles);
            Assert.Single(revert.IrreversibleActions);
            Assert.Equal("run_command", revert.IrreversibleActions[0].Tool);

            // Byte-identical verification of the whole workspace tree
            Assert.False(File.Exists(createdPath));
            Assert.False(File.Exists(moveDstPath));
            Assert.True(File.Exists(deletedPath));
            Assert.True(File.Exists(moveSrcPath));

            Assert.Equal(editedHash, Sha256File(editedPath));
            Assert.Equal(deletedHash, Sha256File(deletedPath));
            Assert.Equal(moveSrcHash, Sha256File(moveSrcPath));
            Assert.Equal(hugeHash, Sha256File(hugePath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void LegacyCheckpoint_Over4MB_CopiesBlobAndRestoresByteIdentically()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "voidcode-cp-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        using var _ = StorageService.UseRoot(Path.Combine(tempRoot, "profile"));
        try
        {
            var bigPath = Path.Combine(tempRoot, "asset.dat");
            var original = new byte[4_500_000];
            new Random(99).NextBytes(original);
            File.WriteAllBytes(bigPath, original);
            var origHash = Sha256File(bigPath);

            var cp = FileSystemService.CreateCheckpoint(bigPath, "Wrote");
            Assert.True(cp.IsRestorable);
            Assert.NotNull(cp.SnapshotBlobPath);

            File.WriteAllBytes(bigPath, new byte[] { 9, 8, 7 });
            Assert.True(FileSystemService.RestoreCheckpoint(cp.Id, out var restored));
            Assert.Equal(bigPath, restored);
            Assert.Equal(origHash, Sha256File(bigPath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void RejectHunk_RevertsOnlyTargetHunk_AndPreservesOtherHunk()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "voidcode-hunk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        using var _ = StorageService.UseRoot(Path.Combine(tempRoot, "profile"));
        try
        {
            var filePath = Path.Combine(tempRoot, "sample.cs");
            var before = "line1\nline2\nline3\nline4\nline5\nline6\nline7\n";
            var after = "line1\nMOD_A\nline3\nline4\nline5\nMOD_B\nline7\n";
            File.WriteAllText(filePath, before);

            var tx = TurnTransactionLedger.BeginTurn(tempRoot);
            tx.SnapshotBeforeMutation(filePath, "edit");
            File.WriteAllText(filePath, after);

            var diff = tx.GetFileDiff(filePath);
            Assert.NotNull(diff);
            Assert.Equal(2, diff!.Hunks.Count);

            // Reject hunk 0 (MOD_A -> line2), keep hunk 1 (MOD_B)
            var (ok, refreshed, err) = tx.RejectHunk(filePath, 0);
            Assert.True(ok, err);
            Assert.NotNull(refreshed);
            Assert.Single(refreshed!.Hunks);

            var onDisk = File.ReadAllText(filePath);
            Assert.Contains("line2", onDisk);
            Assert.DoesNotContain("MOD_A", onDisk);
            Assert.Contains("MOD_B", onDisk);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public async Task BridgeRoutes_TurnReviewAndContextInspector_WorkEndToEnd()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "voidcode-turn-bridge-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(workspace);
        using var _ = StorageService.UseRoot(Path.Combine(tempRoot, "profile"));
        try
        {
            File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "RULE: always verify diffs.");
            var target = Path.Combine(workspace, "app.cs");
            File.WriteAllText(target, "a\nb\nc\n");

            var tx = TurnTransactionLedger.BeginTurn(workspace);
            tx.SnapshotBeforeMutation(target, "edit");
            File.WriteAllText(target, "a\nB_NEW\nc\n");

            var events = new System.Collections.Generic.List<(string Action, JsonNode Payload)>();
            var router = new BridgeRouter();
            var settings = new AppSettings { Provider = "anthropic", Model = "claude-sonnet-4-20250514", MaxTokens = 8192 };
            var handlers = new SettingsAgentHandlers(
                () => workspace,
                () => settings,
                updated => settings = updated,
                _ => { },
                (action, payload) => events.Add((action, JsonSerializer.SerializeToNode(payload)!)),
                _ => { });
            handlers.Register(router);

            // 1. get_turn_file_diff
            await router.TryHandleAsync(
                SettingsAgentRoutes.GetTurnFileDiff,
                "r1",
                JsonNode.Parse(JsonSerializer.Serialize(new { turnId = tx.TurnId, path = target }))!.AsObject());

            var diffFrame = Assert.Single(events, e => e.Action == "turn_file_diff_response");
            Assert.Contains("B_NEW", diffFrame.Payload.ToJsonString());

            // 2. reject_turn_hunk
            await router.TryHandleAsync(
                SettingsAgentRoutes.RejectTurnHunk,
                "r2",
                JsonNode.Parse(JsonSerializer.Serialize(new { turnId = tx.TurnId, path = target, hunkIndex = 0 }))!.AsObject());

            Assert.Contains(events, e => e.Action == "turn_hunk_rejected");
            Assert.Equal("a\nb\nc\n", File.ReadAllText(target));

            // 3. inspect_agent_context
            await router.TryHandleAsync(
                SettingsAgentRoutes.InspectAgentContext,
                "r3",
                JsonNode.Parse(JsonSerializer.Serialize(new
                {
                    promptDraft = "Explain the diff",
                    history = new[] { new { role = "user", content = "hello" } },
                    attachments = new[] { new { id = "problems", type = "problems", label = "@problems", content = "[Error] app.cs:1" } }
                }))!.AsObject());

            var ctxFrame = Assert.Single(events, e => e.Action == "agent_context_response");
            var ctxJson = ctxFrame.Payload.ToJsonString();
            Assert.Contains("claude-sonnet-4-20250514", ctxJson);
            Assert.Contains("AGENTS.md", ctxJson);
            Assert.Contains("@problems", ctxJson);
            Assert.Contains("Conversation history", ctxJson);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }
}

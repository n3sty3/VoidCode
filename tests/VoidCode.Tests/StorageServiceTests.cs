using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The root seam added for . Before it, every store resolved %APPDATA%\VoidCode
// In a static readonly field, so a test either skipped the route or wrote the developer's profile.
[Collection("Storage root")]
public class StorageServiceTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "voidcode-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Clears VOIDCODE_STORAGE_ROOT for the length of a call, so a developer shell cannot skew it.</summary>
    private static void WithNoEnvironmentRoot(Action body)
    {
        var previous = Environment.GetEnvironmentVariable("VOIDCODE_STORAGE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("VOIDCODE_STORAGE_ROOT", null);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOIDCODE_STORAGE_ROOT", previous);
        }
    }

    [Fact]
    public void Root_DefaultsToTheAppDataProfile()
    {
        WithNoEnvironmentRoot(() =>
        {
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoidCode");

            Assert.Equal(expected, StorageService.Root);
        });
    }

    [Fact]
    public void Root_HonoursTheEnvironmentVariableWithoutTouchingTheDefault()
    {
        var root = NewTempRoot();
        var previous = Environment.GetEnvironmentVariable("VOIDCODE_STORAGE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("VOIDCODE_STORAGE_ROOT", root);

            Assert.Equal(Path.GetFullPath(root), StorageService.Root);
            // Nothing is created just because the root was read - only a write does that.
            Assert.False(File.Exists(Path.Combine(root, "operators.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOIDCODE_STORAGE_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UseRoot_MovesEveryStore_AndRestoresThePreviousRoot()
    {
        var root = NewTempRoot();
        var before = StorageService.Root;
        try
        {
            using (StorageService.UseRoot(root))
            {
                Assert.Equal(Path.GetFullPath(root), StorageService.Root);

                var (success, _, username, role) = StorageService.AuthenticateOrRegister(
                    "register", "seam-tester", "correct horse", "Systems operator");
                Assert.True(success);
                Assert.Equal("seam-tester", username);
                Assert.Equal("Systems operator", role);
                Assert.True(File.Exists(Path.Combine(root, "operators.json")));

                // The operator is readable back through the same root, and a wrong passphrase is still
                // Refused - i.e. the PBKDF2 profile really landed in the temp directory.
                Assert.True(StorageService.AuthenticateOrRegister("login", "seam-tester", "correct horse", "").success);
                Assert.False(StorageService.AuthenticateOrRegister("login", "seam-tester", "wrong", "").success);

                StorageService.SaveSession(new SavedChatSession
                {
                    Id = "session_1",
                    Title = "Seam test",
                    UpdatedAt = "2026-01-01T00:00:00Z"
                });
                Assert.True(File.Exists(Path.Combine(root, "sessions.json")));
                Assert.Equal("session_1", Assert.Single(StorageService.LoadSessions()).Id);

                StorageService.SaveCheckpoints(new[]
                {
                    new FileCheckpoint { Id = "cp_1", FilePath = Path.Combine(root, "notes.txt"), PreviousContent = "old" }
                });
                Assert.True(File.Exists(Path.Combine(root, "checkpoints.json")));
                Assert.Equal("cp_1", Assert.Single(StorageService.LoadCheckpoints()).Id);

                StorageService.SaveRecoveryState(new WorkspaceRecoveryState
                {
                    Workspace = root,
                    Buffers = { new RecoveryBufferState { Path = Path.Combine(root, "notes.txt"), Content = "draft" } }
                });
                Assert.Single(Directory.GetFiles(Path.Combine(root, "recovery"), "*.json"));
            }

            Assert.Equal(before, StorageService.Root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UseRoot_AlsoMovesSettingsAndTheEncryptedCredential()
    {
        var root = NewTempRoot();
        var before = StorageService.Root;
        try
        {
            using (StorageService.UseRoot(root))
            {
                SettingsService.Save(new AppSettings { LastWorkspace = root, Theme = "harbor" });
                Assert.True(File.Exists(Path.Combine(root, "settings.json")));
                Assert.Equal(root, SettingsService.Load().LastWorkspace);

                Assert.True(SecretService.SaveApiKey("seam-test-key"));
                Assert.True(File.Exists(Path.Combine(root, "credentials", "provider-key.dat")));
                Assert.Equal("seam-test-key", SecretService.LoadApiKey());

                SecretService.ClearApiKey();
                Assert.False(SecretService.HasApiKey);
                Assert.False(File.Exists(Path.Combine(root, "credentials", "provider-key.dat")));
            }

            Assert.Equal(before, StorageService.Root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UseRoot_NestsAndGivesTheOuterScopeBack()
    {
        var outer = NewTempRoot();
        var inner = NewTempRoot();
        var before = StorageService.Root;
        try
        {
            using (StorageService.UseRoot(outer))
            {
                Assert.Equal(Path.GetFullPath(outer), StorageService.Root);

                using (StorageService.UseRoot(inner))
                {
                    Assert.Equal(Path.GetFullPath(inner), StorageService.Root);
                }

                Assert.Equal(Path.GetFullPath(outer), StorageService.Root);
            }

            Assert.Equal(before, StorageService.Root);
        }
        finally
        {
            foreach (var dir in new[] { outer, inner })
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void UseRoot_RejectsABlankRoot()
    {
        Assert.Throws<ArgumentException>(() => { StorageService.UseRoot("  "); });
    }
}
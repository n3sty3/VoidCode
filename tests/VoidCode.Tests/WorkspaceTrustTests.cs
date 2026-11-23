using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The trust store decides whether a folder may run commands, start language
// Servers or skip Review, so its defaults matter: absent means "unknown", and unknown is
// Untrusted. Decisions persist per folder and only as a path hash.
[Collection("Environment variables")]
public class WorkspaceTrustTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _storePath;
    private readonly string? _previousOverride;

    public WorkspaceTrustTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vc-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _storePath = Path.Combine(_sandbox, "workspace-trust.json");
        _previousOverride = Environment.GetEnvironmentVariable("VOIDCODE_TRUST_STORE");
        Environment.SetEnvironmentVariable("VOIDCODE_TRUST_STORE", _storePath);
        WorkspaceTrustService.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VOIDCODE_TRUST_STORE", _previousOverride);
        WorkspaceTrustService.ResetForTests();
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); }
        catch (Exception ex) { LoggingService.Swallowed(ex, "WorkspaceTrustTests.cleanup"); }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void AnUnknownFolderIsUntrustedAndForcesReview()
    {
        var folder = Folder("never-opened");

        Assert.Equal(WorkspaceTrustState.Unknown, WorkspaceTrustService.GetState(folder));
        Assert.False(WorkspaceTrustService.IsTrusted(folder));
        Assert.False(WorkspaceTrustService.AllowsRunCommand(folder));
        Assert.False(WorkspaceTrustService.AllowsWorkspaceLanguageServers(folder));

        WorkspaceTrustService.Resolve(folder);
        Assert.Equal(WorkspaceTrustState.Unknown, WorkspaceTrustService.CurrentState);
        Assert.True(WorkspaceTrustService.ForcesReview);
    }

    [Fact]
    public void TrustingAFolderSurvivesARestartAndStoresOnlyTheHash()
    {
        var folder = Folder("my-secret-project");

        WorkspaceTrustService.SetTrusted(folder, true);
        Assert.Equal(WorkspaceTrustState.Trusted, WorkspaceTrustService.GetState(folder));

        // The decision is on disk, keyed by the hash, and the folder name is not written down
        Assert.True(File.Exists(_storePath));
        var store = File.ReadAllText(_storePath);
        Assert.Contains(WorkspaceTrustService.HashPath(folder), store);
        Assert.DoesNotContain("my-secret-project", store);

        // A fresh process (cache dropped) still trusts it
        WorkspaceTrustService.ResetForTests();
        Assert.Equal(WorkspaceTrustState.Trusted, WorkspaceTrustService.GetState(folder));
        Assert.True(WorkspaceTrustService.IsTrusted(folder));
    }

    [Fact]
    public void DecliningAFolderIsRememberedAsRestricted()
    {
        var folder = Folder("declined");

        WorkspaceTrustService.SetTrusted(folder, false);
        WorkspaceTrustService.ResetForTests();

        Assert.Equal(WorkspaceTrustState.Restricted, WorkspaceTrustService.GetState(folder));
        Assert.False(WorkspaceTrustService.IsTrusted(folder));
        Assert.False(WorkspaceTrustService.AllowsRunCommand(folder));
    }

    [Fact]
    public void RevokingForgetsTheDecision()
    {
        var folder = Folder("revoked");
        WorkspaceTrustService.SetTrusted(folder, true);

        WorkspaceTrustService.Revoke(folder);
        WorkspaceTrustService.ResetForTests();

        Assert.Equal(WorkspaceTrustState.Unknown, WorkspaceTrustService.GetState(folder));
    }

    [Fact]
    public void ThePathHashIsStableCaseInsensitiveAndEmptyForNothing()
    {
        var folder = Folder("hash-me");
        var hash = WorkspaceTrustService.HashPath(folder);

        Assert.Equal(hash, WorkspaceTrustService.HashPath(folder.ToUpperInvariant()));
        Assert.Equal(hash, WorkspaceTrustService.HashPath(folder + Path.DirectorySeparatorChar));
        Assert.NotEqual(hash, WorkspaceTrustService.HashPath(Folder("hash-me-too")));
        Assert.Equal(string.Empty, WorkspaceTrustService.HashPath(null));
        Assert.Equal(string.Empty, WorkspaceTrustService.HashPath("   "));
    }

    [Fact]
    public void TrustedFoldersLiftTheRestrictedModePolicy()
    {
        var folder = Folder("trusted-project");
        WorkspaceTrustService.SetTrusted(folder, true);
        WorkspaceTrustService.Resolve(folder);

        Assert.Equal(WorkspaceTrustState.Trusted, WorkspaceTrustService.CurrentState);
        Assert.False(WorkspaceTrustService.ForcesReview);
        Assert.True(WorkspaceTrustService.AllowsRunCommand(folder));
        Assert.True(WorkspaceTrustService.AllowsWorkspaceLanguageServers(folder));
    }

    [Fact]
    public void ApplyToPromptsOnlyForFoldersItHasNeverSeen()
    {
        var unknown = Folder("fresh");
        var sent = new List<(string Action, object Payload)>();
        void Send(string action, object payload) => sent.Add((action, payload));

        WorkspaceTrustService.ApplyTo(unknown, Send);
        Assert.Equal(new[] { "trust_state", "trust_prompt" }, sent.Select(item => item.Action));
        Assert.Equal("unknown", PayloadValue(sent[0].Payload, "state"));
        Assert.Equal(unknown, PayloadValue(sent[1].Payload, "path"));

        // Trusting it clears the prompt for the next open
        WorkspaceTrustService.SetTrusted(unknown, true);
        sent.Clear();
        WorkspaceTrustService.ApplyTo(unknown, Send);
        Assert.Equal(new[] { "trust_state" }, sent.Select(item => item.Action));
        Assert.Equal("trusted", PayloadValue(sent[0].Payload, "state"));

        // No folder means nothing to ask about
        sent.Clear();
        WorkspaceTrustService.ApplyTo(null, Send);
        Assert.Equal(new[] { "trust_state" }, sent.Select(item => item.Action));
    }

    [Fact]
    public void SetWorkspaceTrustMessagesArePersistedAndConsumed()
    {
        var folder = Folder("from-ui");
        WorkspaceTrustService.Resolve(folder);
        var sent = new List<string>();
        void Send(string action, object payload) => sent.Add(action);

        var node = new JsonObject { ["path"] = folder, ["trusted"] = true };
        Assert.True(WorkspaceTrustService.TryHandleMessage("set_workspace_trust", node, Send));
        Assert.Equal(WorkspaceTrustState.Trusted, WorkspaceTrustService.CurrentState);
        Assert.Contains("trust_state", sent);

        // Unrelated actions are not ours
        Assert.False(WorkspaceTrustService.TryHandleMessage("run_command", node, Send));

        WorkspaceTrustService.ResetForTests();
        Assert.Equal(WorkspaceTrustState.Trusted, WorkspaceTrustService.GetState(folder));
    }

    // The service sends anonymous objects; read the property the test cares about.
    private static object? PayloadValue(object payload, string name) =>
        payload.GetType().GetProperty(name)?.GetValue(payload);
}
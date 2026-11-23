using System.Text.Json.Nodes;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The bridge nonce authorizes page -> host messages. It must never travel in
// The URL, and a message is only accepted when it comes from the mapped document AND carries
// The current nonce - which is what stops a hostile frame from driving the host.
public class BridgeProtocolTests
{
    private const string Source = "http://app.local/index.html";

    [Fact]
    public void CreateNonce_IsLongUniqueHex()
    {
        var first = BridgeProtocol.CreateNonce();
        var second = BridgeProtocol.CreateNonce();

        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9A-F]{64}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildHandshakeJson_CarriesOnlyTheActionAndNonce()
    {
        var nonce = BridgeProtocol.CreateNonce();
        var node = JsonNode.Parse(BridgeProtocol.BuildHandshakeJson(nonce))!.AsObject();

        Assert.Equal(BridgeProtocol.HandshakeAction, node["action"]!.GetValue<string>());
        Assert.Equal(nonce, node["payload"]!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public void TryAccept_AcceptsTopFrameMessageWithTheCurrentNonce()
    {
        var nonce = BridgeProtocol.CreateNonce();
        var json = $"{{\"action\":\"git_stage_file\",\"id\":\"req_1\",\"bridgeNonce\":\"{nonce}\"}}";

        var accepted = BridgeProtocol.TryAccept(Source, json, nonce, out var message, out var reason);

        Assert.True(accepted);
        Assert.Null(reason);
        Assert.Equal("git_stage_file", message!["action"]!.GetValue<string>());
    }

    [Theory]
    // A frame on any other origin, host suffix trick, scheme, port or path is refused
    [InlineData("http://evil.example/index.html")]
    [InlineData("http://app.local.evil.example/index.html")]
    [InlineData("https://app.local/index.html")]
    [InlineData("http://app.local:8080/index.html")]
    [InlineData("http://app.local/other.html")]
    [InlineData("file:///C:/app/index.html")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTrustedSource_RejectsAnythingButTheTopLevelDocument(string? source)
    {
        Assert.False(BridgeProtocol.IsTrustedSource(source));
    }

    [Fact]
    public void IsTrustedSource_AcceptsTheMappedDocument()
    {
        Assert.True(BridgeProtocol.IsTrustedSource(Source));
        Assert.True(BridgeProtocol.IsTrustedSource("http://app.local/index.html#/workspace"));
    }

    [Theory]
    [InlineData("{\"action\":\"window_close\",\"bridgeNonce\":\"not-the-nonce\"}")]
    [InlineData("{\"action\":\"window_close\"}")]
    [InlineData("{\"action\":\"window_close\",\"bridgeNonce\":\"\"}")]
    public void TryAccept_RejectsMissingOrWrongNonce(string json)
    {
        var nonce = BridgeProtocol.CreateNonce();

        var accepted = BridgeProtocol.TryAccept(Source, json, nonce, out var message, out var reason);

        Assert.False(accepted);
        Assert.Null(message);
        Assert.Contains("nonce", reason);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    [InlineData(null)]
    public void TryAccept_RejectsMalformedMessages(string? json)
    {
        var nonce = BridgeProtocol.CreateNonce();

        var accepted = BridgeProtocol.TryAccept(Source, json, nonce, out _, out var reason);

        Assert.False(accepted);
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryAccept_RejectsAnUntrustedFrameEvenWithAGoodNonce()
    {
        var nonce = BridgeProtocol.CreateNonce();
        var json = $"{{\"action\":\"agent_send\",\"bridgeNonce\":\"{nonce}\"}}";

        var accepted = BridgeProtocol.TryAccept("http://evil.example/index.html", json, nonce, out _, out var reason);

        Assert.False(accepted);
        Assert.Contains("untrusted source", reason);
    }
}

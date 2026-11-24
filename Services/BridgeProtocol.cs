using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VoidCode.Services;

// The page/host bridge handshake.
//
// The nonce authorizes every message the page sends to the host, so it must not travel in the
// URL: any frame, referrer, history entry or devtools session could read it there. The host
// Instead hands it to the top-level document once, right after navigation, and validates it on
// Each inbound message together with the frame that sent it. Keeping that logic here (rather
// Than inline in MainWindow.xaml.cs) makes it unit-testable and keeps the window class small.
public static class BridgeProtocol
{
    public const string HandshakeAction = "bridge_handshake";

    /// <summary>A fresh 256-bit nonce, hex encoded.</summary>
    public static string CreateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The single message the host pushes to the page after NavigationCompleted.</summary>
    public static string BuildHandshakeJson(string? nonce) => new JsonObject
    {
        ["action"] = HandshakeAction,
        ["payload"] = new JsonObject { ["nonce"] = nonce ?? string.Empty }
    }.ToJsonString();

    /// <summary>
    /// True only for the top-level document served from the mapped virtual host, i.e.
    /// Http://app.local/index.html on the default port. A nested frame on another origin
    /// (even something like http://app.local.evil.example/) fails, which is what stops a
    /// Hostile frame from talking to the host.
    /// </summary>
    public static bool IsTrustedSource(string? rawUri, Uri? allowedDocumentUri = null)
    {
        if (string.IsNullOrWhiteSpace(rawUri)) return false;
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)) return false;
        if (allowedDocumentUri != null)
        {
            return string.Equals(uri.Scheme, allowedDocumentUri.Scheme, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(uri.Host, allowedDocumentUri.Host, StringComparison.OrdinalIgnoreCase)
                   && uri.Port == allowedDocumentUri.Port
                   && string.Equals(uri.AbsolutePath, allowedDocumentUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        }
        return uri.Scheme == Uri.UriSchemeHttp
               && string.Equals(uri.Host, "app.local", StringComparison.OrdinalIgnoreCase)
               && uri.Port == 80
               && string.Equals(uri.AbsolutePath, "/index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Accepts a message only when it came from the trusted document AND carries the current
    /// Nonce. On failure <paramref name="reason"/> explains why for the log; on success
    /// <paramref name="message"/> is the parsed envelope to dispatch.
    /// </summary>
    public static bool TryAccept(
        string? sourceUri,
        string? rawJson,
        string? expectedNonce,
        [NotNullWhen(true)] out JsonObject? message,
        [NotNullWhen(false)] out string? reason,
        Uri? allowedDocumentUri = null)
    {
        message = null;

        if (!IsTrustedSource(sourceUri, allowedDocumentUri))
        {
            reason = $"untrusted source '{sourceUri}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            reason = "empty message";
            return false;
        }

        JsonObject? node;
        try
        {
            node = JsonNode.Parse(rawJson)?.AsObject();
        }
        catch (Exception)
        {
            node = null;
        }

        if (node == null)
        {
            reason = "malformed JSON";
            return false;
        }

        if (!FixedTimeEquals(node["bridgeNonce"]?.GetValue<string>(), expectedNonce))
        {
            reason = "invalid bridge nonce";
            return false;
        }

        message = node;
        reason = null;
        return true;
    }

    // Constant-time comparison so a wrong nonce cannot be recovered byte by byte.
    private static bool FixedTimeEquals(string? provided, string? expected)
    {
        var left = Encoding.UTF8.GetBytes(provided ?? string.Empty);
        var right = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        return left.Length == right.Length && left.Length > 0 && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

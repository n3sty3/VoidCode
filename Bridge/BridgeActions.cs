using System;
using System.Diagnostics;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge;

/// <summary>
/// The first five actions that moved off the MainWindow switch.
///
/// Three are page diagnostics (<c>js_log</c>, <c>js_error</c>, <c>open_external_url</c>) and two are
/// Window-state notifications (<c>window_minimize</c>, <c>window_maximize</c>).
/// </summary>
public static class BridgeActions
{
    /// <summary>The payload of <c>js_log</c> / <c>js_error</c>.</summary>
    public sealed class ClientLog
    {
        /// <summary>Required: every caller sends the message it wants logged.</summary>
        public required string Msg { get; init; }

        /// <summary>Optional origin - "csp.*" violations are security events ().</summary>
        public string? Context { get; init; }

        /// <summary>Optional level for <c>js_log</c>; core/bridge.js sends the swallowed-error level.</summary>
        public string? Level { get; init; }
    }

    public sealed class ExternalUrl
    {
        public required string Url { get; init; }
    }

    /// <summary>A notification that carries nothing the host needs (window state changes).</summary>
    public sealed class NoPayload
    {
    }

    public static void Register(BridgeRouter router, Action? onMinimize = null, Action? onMaximize = null)
    {
        router.RegisterNotification<ClientLog>("js_log", (request, _) =>
        {
            if ((request.Context ?? "").StartsWith("csp", StringComparison.Ordinal))
            {
                LoggingService.LogWarning($"CSP: {request.Msg}", nameof(BridgeActions));
            }
            Debug.WriteLine($"[VoidCode JS] {request.Msg}");
            return Task.CompletedTask;
        });

        router.RegisterNotification<ClientLog>("js_error", (request, _) =>
        {
            LoggingService.LogError($"JavaScript error: {request.Msg}", source: nameof(BridgeActions));
            return Task.CompletedTask;
        });

        router.RegisterNotification<ExternalUrl>("open_external_url", (request, _) =>
        {
            // UriLauncher re-validates the scheme; it never launches a file:// or javascript: URL.
            UriLauncher.OpenExternal(request.Url);
            return Task.CompletedTask;
        });

        if (onMinimize != null)
        {
            router.RegisterNotification<NoPayload>("window_minimize", (_, _) =>
            {
                onMinimize();
                return Task.CompletedTask;
            });
        }

        if (onMaximize != null)
        {
            router.RegisterNotification<NoPayload>("window_maximize", (_, _) =>
            {
                onMaximize();
                return Task.CompletedTask;
            });
        }
    }
}
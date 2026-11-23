using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge;

/// <summary>
/// The typed bridge router.
///
/// The 100+ case switch in MainWindow.xaml.cs answers every action the page sends, reading each payload
/// By hand (<c>node["path"]?.GetValue&lt;string&gt;() ?? ""</c>) and validating nothing. This router
/// Replaces it one action at a time: a route is registered with its request type, System.Text.Json
/// Deserializes it (so a missing <c>required</c> member becomes an <c>invalid_payload</c> error instead
/// Of a silently empty string), and the answer is a uniform envelope:
/// <code>{ id, ok: true, data }</code> or <code>{ id, ok: false, error: { code, message } }</code>.
///
/// Three properties make the migration safe:
/// <list type="bullet">
/// <item>An action the router does not know returns <see cref="BridgeOutcome.Skipped"/>, so the legacy
/// Switch keeps answering until the last case has moved.</item>
/// <item>An unknown <em>request</em> is answered with a <c>unknown_action</c> envelope instead of
/// Silence - the caller builds it from <see cref="UnknownActionOutcome"/>.</item>
/// <item>Every request id is cancellable: <c>cancel_request</c> is owned by the router itself, so a long
/// Action (a search, an agent turn) stops instead of finishing into a closed window.</item>
/// </list>
/// </summary>
public sealed class BridgeRouter
{
    public const string CancelAction = "cancel_request";
    public const string UnknownActionError = "unknown_action";
    public const string InvalidPayloadError = "invalid_payload";
    public const string HandlerFailedError = "handler_failed";
    public const string CancelledError = "cancelled";

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        // One message shape serves several actions, so unknown members are ignored; missing required
        // Ones throw and are reported as invalid_payload.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly Dictionary<string, Func<BridgeContext, Task<BridgeOutcome>>> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public BridgeRouter()
    {
        // The router owns cancellation, so every migrated action gets it without extra wiring.
        _routes[CancelAction] = CancelRouteAsync;
    }

    /// <summary>The actions this router answers, including <c>cancel_request</c>.</summary>
    public IReadOnlyCollection<string> Actions
    {
        get { lock (_gate) { return _routes.Keys.ToArray(); } }
    }

    public bool Contains(string? action)
    {
        if (string.IsNullOrEmpty(action)) return false;
        lock (_gate) { return _routes.ContainsKey(action!); }
    }

    /// <summary>
    /// Registers a request-style route: the message is deserialized into <typeparamref name="TRequest"/>
    /// (a <c>required</c> member makes an incomplete payload fail), the handler's result becomes
    /// <c>data</c>, and an exception becomes a coded error instead of an unobserved task.
    /// </summary>
    public void Register<TRequest, TResult>(string action, Func<TRequest, BridgeContext, Task<TResult>> handler, bool envelope = true)
        where TRequest : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(handler);

        AddRoute(action, async context =>
        {
            if (!TryDeserialize<TRequest>(context, out var request, out var failure)) return failure!;
            try
            {
                var data = await handler(request!, context).ConfigureAwait(false);
                return BridgeOutcome.Success(data, envelope);
            }
            catch (OperationCanceledException) when (!envelope)
            {
                // A legacy-shaped route answers for itself, so a cancellation cannot become an envelope:
                // The page's request times out and says so. Log it rather than losing it.
                LoggingService.LogWarning($"{action} was cancelled before it answered.", nameof(BridgeRouter));
                return BridgeOutcome.Answered();
            }
            catch (OperationCanceledException)
            {
                return BridgeOutcome.Failure(CancelledError, $"{action} was cancelled");
            }
            catch (Exception error) when (!envelope)
            {
                LoggingService.LogError($"{action} failed: {error.Message}", source: nameof(BridgeRouter));
                return BridgeOutcome.Answered();
            }
            catch (Exception error)
            {
                return BridgeOutcome.Failure(HandlerFailedError, error.Message);
            }
        });
    }

    /// <summary>A route that only has a side effect - the page does not wait for data.</summary>
    public void RegisterNotification<TRequest>(string action, Func<TRequest, BridgeContext, Task> handler)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register<TRequest, object?>(action, async (request, context) =>
        {
            await handler(request, context).ConfigureAwait(false);
            return null;
        }, envelope: false);
    }

    /// <summary>
    /// A route that reads the raw <see cref="JsonObject"/> - for actions whose payload is free-form (an
    /// Agent tool result, a serialized edit) and has no useful request type.
    /// </summary>
    public void RegisterRaw(string action, Func<BridgeContext, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(handler);

        AddRoute(action, async context =>
        {
            try
            {
                await handler(context).ConfigureAwait(false);
                return BridgeOutcome.Answered();
            }
            catch (OperationCanceledException)
            {
                LoggingService.LogWarning($"{action} was cancelled before it answered.", nameof(BridgeRouter));
                return BridgeOutcome.Answered();
            }
            catch (Exception error)
            {
                LoggingService.LogError($"{action} failed: {error.Message}", source: nameof(BridgeRouter));
                return BridgeOutcome.Answered();
            }
        });
    }

    /// <summary>
    /// Dispatches one accepted message. Never throws: a handler failure is a coded error, an unknown
    /// Action is <see cref="BridgeOutcome.Skipped"/>, and the cancellation registration is always undone.
    /// </summary>
    public async Task<BridgeOutcome> TryHandleAsync(string action, string id, JsonObject message)
    {
        Func<BridgeContext, Task<BridgeOutcome>>? route;
        lock (_gate) { _routes.TryGetValue(action ?? string.Empty, out route); }
        if (route == null) return BridgeOutcome.Skipped;

        var cts = new CancellationTokenSource();
        var tracked = !string.IsNullOrEmpty(id);
        if (tracked)
        {
            lock (_gate) { _inFlight[id] = cts; }
        }

        try
        {
            return await route(new BridgeContext(action!, id ?? string.Empty, message, cts.Token)).ConfigureAwait(false);
        }
        finally
        {
            if (tracked)
            {
                lock (_gate) { _inFlight.Remove(id); }
            }
            cts.Dispose();
        }
    }

    /// <summary>Cancels the request with this id. False when it already finished (or never existed).</summary>
    public bool Cancel(string id)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id ?? string.Empty, out cts)) return false;
        }

        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>The envelope for an action nobody owns, so an unknown request is answered, not ignored.</summary>
    public static BridgeOutcome UnknownActionOutcome(string action)
        => BridgeOutcome.Failure(UnknownActionError, $"no host handler for '{action}'");

    /// <summary>The wire shape: <c>{ id, ok, data }</c> or <c>{ id, ok: false, error: { code, message } }</c>.</summary>
    public static object BuildEnvelope(string id, BridgeOutcome outcome) => outcome.Ok
        ? new { id, ok = true, data = outcome.Data }
        : new { id, ok = false, error = new { code = outcome.Error?.Code ?? HandlerFailedError, message = outcome.Error?.Message ?? "" } };

    private async Task<BridgeOutcome> CancelRouteAsync(BridgeContext context)
    {
        var target = context.Text("id");
        if (target.Length == 0) return BridgeOutcome.Failure(InvalidPayloadError, "cancel_request needs an id");

        // Cancel() only says whether there was anything to cancel; the awaited route reports
        // CancelledError once it observes the token.
        var cancelled = Cancel(target);
        return await Task.FromResult(BridgeOutcome.Success(new { cancelled })).ConfigureAwait(false);
    }

    private void AddRoute(string action, Func<BridgeContext, Task<BridgeOutcome>> route)
    {
        lock (_gate)
        {
            if (_routes.ContainsKey(action))
            {
                throw new InvalidOperationException($"A bridge route for '{action}' is already registered.");
            }
            _routes[action] = route;
        }
    }

    private static bool TryDeserialize<TRequest>(BridgeContext context, out TRequest? request, out BridgeOutcome? failure)
        where TRequest : class
    {
        request = null;
        failure = null;
        try
        {
            request = context.Message.Deserialize<TRequest>(PayloadOptions);
        }
        catch (JsonException error)
        {
            failure = BridgeOutcome.Failure(InvalidPayloadError, error.Message);
            return false;
        }

        if (request == null)
        {
            failure = BridgeOutcome.Failure(InvalidPayloadError, "empty payload");
            return false;
        }
        return true;
    }
}
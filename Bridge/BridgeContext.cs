using System.Text.Json.Nodes;
using System.Threading;

namespace VoidCode.Bridge;

/// <summary>
/// One accepted page → host message, as the router hands it to a handler - the action,
/// The request id (empty for fire-and-forget notifications), the parsed JSON, and the token the router
/// Cancels when the page sends <c>cancel_request</c> for that id.
/// </summary>
public sealed class BridgeContext
{
    internal BridgeContext(string action, string id, JsonObject message, CancellationToken cancellation)
    {
        Action = action;
        Id = id;
        Message = message;
        Cancellation = cancellation;
    }

    public string Action { get; }

    /// <summary>Empty for a notification - the page is not waiting for an answer.</summary>
    public string Id { get; }

    public JsonObject Message { get; }

    public CancellationToken Cancellation { get; }

    /// <summary>True when the page sent an id and therefore expects an envelope back.</summary>
    public bool IsRequest => Id.Length > 0;

    /// <summary>Convenience read for the actions that are not worth a request record.</summary>
    public string Text(string name, string fallback = "")
        => Message[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
}

/// <summary>What a route did: handled or not, and either a result or a coded error.</summary>
public sealed class BridgeOutcome
{
    private BridgeOutcome(bool handled, bool ok, bool expectsEnvelope, object? data, BridgeError? error)
    {
        Handled = handled;
        Ok = ok;
        ExpectsEnvelope = expectsEnvelope;
        Data = data;
        Error = error;
    }

    /// <summary>No route owns this action - the caller may still handle it (the legacy switch).</summary>
    public static BridgeOutcome Skipped { get; } = new(false, false, false, null, null);

    public bool Handled { get; }

    public bool Ok { get; }

    /// <summary>
    /// True when the caller should post <c>{id, ok, data|error}</c> for this result. False for routes
    /// That answer in a legacy shape themselves (the file-system handlers still send
    /// <c>read_file_response</c> and friends verbatim) and for notifications, which answer nothing.
    /// </summary>
    public bool ExpectsEnvelope { get; }

    public object? Data { get; }

    public BridgeError? Error { get; }

    internal static BridgeOutcome Success(object? data = null, bool envelope = true) => new(true, true, envelope, data, null);

    /// <summary>Handled, answered by the route itself - the caller posts nothing.</summary>
    public static BridgeOutcome Answered() => new(true, true, false, null, null);

    public static BridgeOutcome Failure(string code, string message)
        => new(true, false, true, null, new BridgeError(code, message));
}

/// <summary>A machine-readable failure: the page can branch on <see cref="Code"/>.</summary>
public sealed record BridgeError(string Code, string Message);
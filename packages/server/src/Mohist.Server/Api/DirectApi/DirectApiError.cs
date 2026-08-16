namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The fixed public error-code vocabulary of the direct external Agent
/// API. Codes are the stable
/// machine-readable half of the error envelope; messages stay safe
/// public explanations and never carry internal detail.
/// </summary>
public static class DirectApiErrorCodes
{
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotImplemented = "not_implemented";

    /// <summary>
    /// The canonical-resource 404 codes. They surface only after the
    /// caller's Project grant passed: a resource absent from or not
    /// belonging to the authorized Project is indistinguishable from a
    /// missing one.
    /// </summary>
    public const string JobNotFound = "job_not_found";
    public const string InputNotFound = "input_not_found";
    public const string TurnNotFound = "turn_not_found";

    /// <summary>
    /// The freshness gate of every projection-sourced answer: the
    /// required durable source watermark is ahead of the stored
    /// projection checkpoint, so the projection cannot yet be served
    /// as current state. A transport condition — never the public
    /// five-state <c>unknown</c>.
    /// </summary>
    public const string ProjectionLag = "projection_lag";
}

/// <summary>
/// The single safe error shape of the <c>/api/v1</c> surface:
/// <c>{ "error": { "code", "message" } }</c>. It is deliberately distinct
/// from the control-plane <c>success</c> envelope: the two surfaces are
/// different contracts and must never be conflated.
/// </summary>
public sealed record DirectApiError(string Code, string Message)
{
    public static DirectApiError Unauthenticated() =>
        new(DirectApiErrorCodes.Unauthenticated, "Authentication required.");

    public static DirectApiError Forbidden() =>
        new(DirectApiErrorCodes.Forbidden, "The caller is not authorized for this request.");
}

/// <summary>Envelope wrapper so the serialized shape is exactly one
/// <c>error</c> object with <c>code</c> and <c>message</c>.</summary>
public sealed record DirectApiErrorEnvelope(DirectApiError Error);

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

namespace Mohist.Server.Api;

public record ApiResponse<T>(
    bool Success,
    T? Data = default,
    string? Error = null,
    string? Code = null,
    object? Details = null,
    string? Effect = null,
    bool? RetrySafe = null,
    string? NextAction = null);

/// <summary>
/// The decision facts a control-plane failure carries beside its cause: what
/// the operation changed, whether repeating the identical request is safe,
/// and the executable command that recovers. The Server supplies them when
/// it knows them; null fields are omitted from the wire payload.
/// </summary>
public static class ApiEffect
{
    public const string None = "none";
    public const string Unknown = "unknown";
    public const string Applied = "applied";
}

public static class ApiResults
{
    public static IResult Ok<T>(T data) => Results.Ok(new ApiResponse<T>(true, data));

    public static IResult Ok() => Results.Ok(SuccessEnvelope());

    public static ApiResponse<object> SuccessEnvelope() => new(true);

    public static IResult Fail(
        string error,
        int statusCode = 400,
        string? code = null,
        object? details = null,
        string? effect = null,
        bool? retrySafe = null,
        string? nextAction = null) =>
        Results.Json(Failure(error, statusCode, code, details, effect, retrySafe, nextAction), statusCode: statusCode);

    public static ApiResponse<object> Failure(
        string error,
        int statusCode = 400,
        string? code = null,
        object? details = null,
        string? effect = null,
        bool? retrySafe = null,
        string? nextAction = null) =>
        new(false, Error: error, Code: code ?? "bad_request", Details: details,
            Effect: effect, RetrySafe: retrySafe, NextAction: nextAction);

    public static IResult NotFound(
        string error,
        string? effect = null,
        bool? retrySafe = null,
        string? nextAction = null) =>
        Fail(error, 404, "not_found", effect: effect, retrySafe: retrySafe, nextAction: nextAction);

    public static IResult PayloadTooLarge(string error, string? code = null, object? details = null) =>
        Fail(error, 413, code ?? "payload_too_large", details);

    public static IResult Conflict(
        string error,
        string? code = null,
        object? details = null,
        string? effect = null,
        bool? retrySafe = null,
        string? nextAction = null) =>
        Fail(error, 409, code ?? "conflict", details, effect, retrySafe, nextAction);

    public static IResult BadRequest(
        string error,
        string? code = null,
        object? details = null,
        string? effect = null,
        bool? retrySafe = null,
        string? nextAction = null) =>
        Fail(error, 400, code ?? "bad_request", details, effect, retrySafe, nextAction);
}

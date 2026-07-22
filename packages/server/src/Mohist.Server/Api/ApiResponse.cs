namespace Mohist.Server.Api;

public record ApiResponse<T>(bool Success, T? Data = default, string? Error = null, string? Code = null, object? Details = null);

public static class ApiResults
{
    public static IResult Ok<T>(T data) => Results.Ok(new ApiResponse<T>(true, data));

    public static IResult Ok() => Results.Ok(new ApiResponse<object>(true));

    public static IResult Fail(string error, int statusCode = 400, string? code = null, object? details = null) =>
        Results.Json(new ApiResponse<object>(false, Error: error, Code: code, Details: details), statusCode: statusCode);

    public static IResult NotFound(string error) => Fail(error, 404, "not_found");

    public static IResult PayloadTooLarge(string error, string? code = null, object? details = null) =>
        Fail(error, 413, code ?? "payload_too_large", details);

    public static IResult Conflict(string error, string? code = null, object? details = null) => Fail(error, 409, code ?? "conflict", details);

    public static IResult BadRequest(string error, string? code = null, object? details = null) => Fail(error, 400, code ?? "bad_request", details);
}

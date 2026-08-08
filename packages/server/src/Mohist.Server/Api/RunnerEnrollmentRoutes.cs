using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;

namespace Mohist.Server.Api;

/// <summary>
/// Runner install registration (docs/auth.md "Runner：安装即注册"): the
/// admin mints a one-time enrollment token; a fresh runner consumes it to
/// receive a machine credential bound to its RunnerId; the admin can
/// revoke a runner's credential so its requests are rejected until it
/// re-runs the install flow.
/// </summary>
public static class RunnerEnrollmentRoutes
{
    private const int MaxRunnerIdLength = 256;

    public static WebApplication MapRunnerEnrollmentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runners");

        group.MapPost("/enrollment-tokens", CreateEnrollmentTokenAsync)
            .RequireScopes(Scope.Operator);
        group.MapPost("/register", RegisterAsync);
        group.MapDelete("/{runnerId}/credentials", RevokeCredentialsAsync)
            .RequireScopes(Scope.Operator);

        return app;
    }

    private static async Task<IResult> CreateEnrollmentTokenAsync(
        HttpContext context,
        ICredentialStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal)
            return Unauthorized();

        var expiresAt = time.GetUtcNow().Add(EnrollmentTokenPolicy.Ttl);
        var result = await store.CreateEnrollmentTokenAsync(expiresAt, ct).ConfigureAwait(false);
        return Results.Json(
            new ApiResponse<EnrollmentCreatedResponse>(true, new EnrollmentCreatedResponse(
                result.Token,
                result.EnrollmentToken.ExpiresAt)),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RegisterAsync(
        RunnerEnrollmentRegisterRequest request,
        ICredentialStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request is null)
            return ApiResults.BadRequest("request body is required", "register_body_required");

        var token = request.Token?.Trim();
        var runnerId = request.RunnerId?.Trim();
        if (string.IsNullOrEmpty(token))
            return ApiResults.BadRequest("token is required", "enrollment_token_required");
        if (string.IsNullOrEmpty(runnerId))
            return ApiResults.BadRequest("runnerId is required", "runner_id_required");
        if (runnerId.Length > MaxRunnerIdLength)
            return ApiResults.BadRequest(
                $"runnerId must be at most {MaxRunnerIdLength} characters", "runner_id_too_long");

        var status = await store.ConsumeEnrollmentTokenAsync(
            CredentialToken.Hash(token), time.GetUtcNow(), ct).ConfigureAwait(false);
        if (status != EnrollmentTokenConsumeStatus.Consumed)
        {
            // One indistinguishable answer for missing, expired and
            // already-consumed tokens (same discipline as credential
            // resolution): the runner re-runs the install flow.
            return EnrollmentTokenInvalid();
        }

        var result = await store.CreateRunnerCredentialAsync(
            MohistPrincipal.AdminPrincipalId, runnerId, ct).ConfigureAwait(false);
        if (result is null)
        {
            return ApiResults.Conflict(
                "Another registration for this runner is in progress; re-run the install flow",
                "runner_registration_conflict");
        }

        return Results.Json(
            new ApiResponse<RunnerCredentialCreatedResponse>(true, new RunnerCredentialCreatedResponse(
                result.Token, runnerId)),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeCredentialsAsync(
        HttpContext context,
        string runnerId,
        ICredentialStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal)
            return Unauthorized();

        var revokedAt = time.GetUtcNow();
        var revoked = await store.RevokeRunnerCredentialAsync(runnerId, revokedAt, ct).ConfigureAwait(false);
        if (!revoked)
            return ApiResults.NotFound($"No active credential for runner '{runnerId}'");

        return ApiResults.Ok(new RunnerCredentialRevokedResponse(runnerId, revokedAt));
    }

    private static IResult Unauthorized() =>
        Results.Json(
            new ApiResponse<object>(false, Error: "Authentication required.", Code: "unauthorized"),
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult EnrollmentTokenInvalid() =>
        Results.Json(
            new ApiResponse<object>(false, Error: "Enrollment token is invalid, expired or already used.", Code: "enrollment_token_invalid"),
            statusCode: StatusCodes.Status401Unauthorized);
}

public sealed record RunnerEnrollmentRegisterRequest(string? Token, string? RunnerId, string? Hostname);

public sealed record EnrollmentCreatedResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record RunnerCredentialCreatedResponse(string Token, string RunnerId);

public sealed record RunnerCredentialRevokedResponse(string RunnerId, DateTimeOffset RevokedAt);

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;

namespace Mohist.Server.Api;

/// <summary>
/// RFC 8628 device authorization: the CLI mints a pending flow, the
/// logged-in Web session resolves the user
/// code and records the decision, and the CLI polls for the session —
/// access (session, 1h) + refresh (30d) — that rolling refresh rotates.
/// Polling and user-code guessing are rate-limited per source
/// (slow_down / 429). <c>/api/auth/device/code</c> and
/// <c>/api/auth/token</c> are exempt from auth resolution (they carry
/// their own credential); verify/decision ride the Web session.
/// </summary>
public static class AuthDeviceRoutes
{
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    public const string RefreshTokenGrantType = "refresh_token";

    public static WebApplication MapAuthDeviceRoutes(this WebApplication app)
    {
        app.MapPost("/api/auth/device/code", CreateDeviceCodeAsync);
        app.MapPost("/api/auth/device/verify", VerifyUserCodeAsync);
        app.MapPost("/api/auth/device/decision", DecideAsync);
        app.MapPost("/api/auth/token", TokenAsync);
        app.MapPost("/api/auth/logout", LogoutAsync);
        return app;
    }

    private static async Task<IResult> CreateDeviceCodeAsync(
        HttpContext context,
        DeviceCodeRequest? request,
        IDeviceAuthorizationStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        var clientName = NormalizeClientName(request?.Name);
        if (request is not null && !string.IsNullOrWhiteSpace(request.Name) && clientName is null)
            return ApiResults.BadRequest(
                $"name must be at most {DeviceFlowPolicy.MaxClientNameLength} characters",
                "device_client_name_too_long");

        var now = time.GetUtcNow();
        var deviceCode = CredentialToken.GenerateDeviceCode();
        var userCode = DeviceFlowPolicy.GenerateUserCode();
        await store.CreateAsync(new DeviceAuthorization(
            $"device_flow_{Guid.NewGuid():N}",
            CredentialToken.Hash(deviceCode),
            CredentialToken.Hash(userCode),
            clientName,
            DeviceFlowStatus.Pending,
            PrincipalId: null,
            DecidedAt: null,
            now + DeviceFlowPolicy.FlowTtl,
            now), ct);

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        var verificationUri = $"{baseUrl}/device";
        return Results.Json(
            new ApiResponse<DeviceCodeResponse>(true, new DeviceCodeResponse(
                deviceCode,
                userCode,
                verificationUri,
                $"{verificationUri}?user_code={userCode}",
                (int)DeviceFlowPolicy.PollInterval.TotalSeconds,
                (int)DeviceFlowPolicy.FlowTtl.TotalSeconds)),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> VerifyUserCodeAsync(
        HttpContext context,
        DeviceVerifyRequest? request,
        IDeviceAuthorizationStore store,
        DeviceGuessRateLimiter guessLimiter,
        TimeProvider time,
        CancellationToken ct)
    {
        if (!guessLimiter.IsAllowed(SourceKey(context)))
            return RateLimited();

        var normalized = DeviceFlowPolicy.NormalizeUserCode(request?.UserCode ?? string.Empty);
        if (normalized.Length != DeviceFlowPolicy.UserCodeLength)
            return CodeNotFound();

        var flow = await store.FindByUserCodeHashAsync(CredentialToken.Hash(normalized), ct).ConfigureAwait(false);
        if (flow is null)
            return CodeNotFound();
        if (flow.ExpiresAt <= time.GetUtcNow())
            return ApiResults.Fail("This code has expired.", 410, "device_flow_expired");

        return ApiResults.Ok(new DeviceVerifyResponse(flow.Id, flow.ClientName, flow.ExpiresAt));
    }

    private static async Task<IResult> DecideAsync(
        HttpContext context,
        DeviceDecisionRequest? request,
        IDeviceAuthorizationStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FlowId))
            return ApiResults.BadRequest("flowId is required", "device_flow_id_required");
        if (!TryParseDecision(request.Decision, out var decision))
            return ApiResults.BadRequest("decision must be 'approved' or 'denied'", "device_decision_invalid");
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal principal)
            return Unauthorized();

        var result = await store.DecideAsync(
            request.FlowId, decision, principal.Id, time.GetUtcNow(), ct).ConfigureAwait(false);
        return result.Status switch
        {
            DeviceDecisionStatus.Decided => ApiResults.Ok(new { status = decision.ToString().ToLowerInvariant() }),
            DeviceDecisionStatus.NotFound => CodeNotFound(),
            _ when result.CurrentStatus == decision => ApiResults.Ok(new { status = decision.ToString().ToLowerInvariant() }),
            _ => ApiResults.Conflict("This authorization was already decided.", "device_flow_already_decided"),
        };
    }

    private static async Task<IResult> TokenAsync(
        HttpContext context,
        TokenRequest? request,
        IDeviceAuthorizationStore store,
        DevicePollRateLimiter pollLimiter,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request is null)
            return InvalidGrant();

        if (string.Equals(request.GrantType, DeviceCodeGrantType, StringComparison.Ordinal))
            return await DeviceTokenAsync(context, request, store, pollLimiter, time, ct).ConfigureAwait(false);
        if (string.Equals(request.GrantType, RefreshTokenGrantType, StringComparison.Ordinal))
            return await RefreshTokenAsync(request, store, time, ct).ConfigureAwait(false);

        return ApiResults.BadRequest("Unsupported grant type.", "unsupported_grant_type");
    }

    private static async Task<IResult> DeviceTokenAsync(
        HttpContext context,
        TokenRequest request,
        IDeviceAuthorizationStore store,
        DevicePollRateLimiter pollLimiter,
        TimeProvider time,
        CancellationToken ct)
    {
        if (!pollLimiter.IsAllowed(SourceKey(context)))
            return ApiResults.Fail("Polling too frequently.", 429, "slow_down");

        var deviceCode = request.DeviceCode?.Trim();
        if (string.IsNullOrEmpty(deviceCode))
            return ApiResults.BadRequest("device_code is required", "device_code_required");

        var now = time.GetUtcNow();
        var flow = await store.FindByDeviceCodeHashAsync(CredentialToken.Hash(deviceCode), ct).ConfigureAwait(false);
        if (flow is null)
            return InvalidGrant();
        if (flow.ExpiresAt <= now)
            return ApiResults.Fail("The device authorization has expired.", 400, "expired_token");

        if (flow.Status == DeviceFlowStatus.Pending)
            return ApiResults.Fail("Authorization pending.", 400, "authorization_pending");
        if (flow.Status == DeviceFlowStatus.Denied)
            return ApiResults.Fail("The authorization was denied.", 400, "access_denied");
        if (flow.Status != DeviceFlowStatus.Approved)
            return InvalidGrant();

        var issued = await store.IssueDeviceTokensAsync(flow.Id, now, ct).ConfigureAwait(false);
        return issued.Status switch
        {
            DeviceTokenIssueStatus.Issued => TokenResponse(issued),
            DeviceTokenIssueStatus.Pending => ApiResults.Fail("Authorization pending.", 400, "authorization_pending"),
            DeviceTokenIssueStatus.Denied => ApiResults.Fail("The authorization was denied.", 400, "access_denied"),
            _ => InvalidGrant(),
        };
    }

    private static async Task<IResult> RefreshTokenAsync(
        TokenRequest request,
        IDeviceAuthorizationStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        var refreshToken = request.RefreshToken?.Trim();
        if (string.IsNullOrEmpty(refreshToken))
            return ApiResults.BadRequest("refresh_token is required", "refresh_token_required");
        if (!CredentialToken.TryParse(refreshToken, out var kind) || kind != CredentialKind.Refresh)
            return InvalidGrant();

        var rotated = await store.RotateRefreshAsync(CredentialToken.Hash(refreshToken), time.GetUtcNow(), ct)
            .ConfigureAwait(false);
        return rotated.Status == RefreshRotationStatus.Rotated
            ? TokenResponse(rotated)
            : InvalidGrant();
    }

    private static async Task<IResult> LogoutAsync(
        LogoutRequest? request,
        IDeviceAuthorizationStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        // Idempotent and deliberately non-revealing: logout answers 200
        // even for an unknown or already-revoked refresh token, and the
        // family revoke is a no-op when nothing is left active.
        var refreshToken = request?.RefreshToken?.Trim();
        if (!string.IsNullOrEmpty(refreshToken))
        {
            var familyId = await store.FindFamilyIdByRefreshTokenAsync(
                CredentialToken.Hash(refreshToken), ct).ConfigureAwait(false);
            if (familyId is not null)
                await store.RevokeFamilyAsync(familyId, time.GetUtcNow(), ct).ConfigureAwait(false);
        }

        return ApiResults.Ok();
    }

    private static IResult TokenResponse(DeviceTokenIssueResult issued) =>
        ApiResults.Ok(new TokenResponse(
            issued.AccessToken!,
            issued.RefreshToken!,
            issued.Access!.ExpiresAt!.Value,
            issued.Refresh!.ExpiresAt!.Value));

    private static IResult TokenResponse(RefreshRotationResult rotated) =>
        ApiResults.Ok(new TokenResponse(
            rotated.AccessToken!,
            rotated.RefreshToken!,
            rotated.Access!.ExpiresAt!.Value,
            rotated.Refresh!.ExpiresAt!.Value));

    private static string? NormalizeClientName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var trimmed = name.Trim();
        return trimmed.Length > DeviceFlowPolicy.MaxClientNameLength ? null : trimmed;
    }

    private static bool TryParseDecision(string? raw, out DeviceFlowStatus decision)
    {
        if (string.Equals(raw, "approved", StringComparison.OrdinalIgnoreCase))
        {
            decision = DeviceFlowStatus.Approved;
            return true;
        }
        if (string.Equals(raw, "denied", StringComparison.OrdinalIgnoreCase))
        {
            decision = DeviceFlowStatus.Denied;
            return true;
        }

        decision = default;
        return false;
    }

    private static string SourceKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static IResult Unauthorized() =>
        Results.Json(
            new ApiResponse<object>(false, Error: "Authentication required.", Code: "unauthorized"),
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult InvalidGrant() =>
        ApiResults.Fail("The presented credential is invalid, expired or revoked.", 400, "invalid_grant");

    private static IResult RateLimited() =>
        ApiResults.Fail("Too many attempts. Try again later.", 429, "rate_limited");

    private static IResult CodeNotFound() =>
        ApiResults.Fail("Code not found.", 404, "device_code_not_found");
}

public sealed record DeviceCodeRequest(string? Name);

public sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int Interval,
    int ExpiresIn);

public sealed record DeviceVerifyRequest(string? UserCode);

public sealed record DeviceVerifyResponse(string FlowId, string? ClientName, DateTimeOffset ExpiresAt);

public sealed record DeviceDecisionRequest(string? FlowId, string? Decision);

public sealed record TokenRequest(
    [property: JsonPropertyName("grant_type")] string? GrantType,
    [property: JsonPropertyName("device_code")] string? DeviceCode,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt);

public sealed record LogoutRequest(string? RefreshToken);

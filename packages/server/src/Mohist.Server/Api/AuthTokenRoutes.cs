using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Personal access token management: issue, list and revoke PATs for the
/// authenticated principal. The full token value appears in exactly one
/// response — the issuance response; the store only ever holds its hash.
/// </summary>
public static class AuthTokenRoutes
{
    private const int MaxNameLength = 256;

    public static WebApplication MapAuthTokenRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth/tokens").RequireScopes(Scope.Operator);

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapPost("/{name}/revoke", RevokeAsync);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        CreatePatRequest request,
        ICredentialStore store,
        IAuthAuditRecorder audit,
        ProjectRefResolver projects,
        TimeProvider time,
        CancellationToken ct)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal principal)
            return Unauthorized();

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ApiResults.BadRequest("--name is required", "pat_name_required");
        if (name.Length > MaxNameLength)
            return ApiResults.BadRequest($"PAT name must be at most {MaxNameLength} characters", "pat_name_too_long");
        if (name.Contains('/'))
            return ApiResults.BadRequest("PAT name must not contain '/'", "pat_name_invalid");

        if (!ResolveScope(request.Scope, out var scope))
        {
            return ApiResults.BadRequest(
                "--scope must be 'operator' or 'readonly'", "pat_scope_invalid");
        }

        DateTimeOffset expiresAt;
        try
        {
            expiresAt = PatPolicy.ResolveExpiresAt(request.TtlHours, time);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiResults.BadRequest(
                $"--ttl must be between 1 and {PatPolicy.MaxTtlHours} hours", "pat_ttl_invalid");
        }

        var hasExplicitGrant = request.ProjectIds is not null;
        if (hasExplicitGrant && request.AllProjects)
            return Forbidden("--project and --all-projects cannot be combined");

        DirectApiProjectGrant? directApiProjectGrant = null;
        if (request.AllProjects)
        {
            if (!scope.Equals(Scope.Operator))
                return Forbidden("--all-projects requires operator scope");
            directApiProjectGrant = DirectApiProjectGrant.OperatorAll;
        }
        else if (hasExplicitGrant)
        {
            if (request.ProjectIds!.Count == 0)
                return Forbidden("At least one --project is required for an explicit grant");

            var projectIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var projectRef in request.ProjectIds)
            {
                var normalizedProjectRef = projectRef?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedProjectRef))
                    return Forbidden("Every --project value must identify a private Project");

                var project = await projects.ResolveAsync(normalizedProjectRef).ConfigureAwait(false);
                if (project is null)
                    return Forbidden("Every --project value must identify a private Project");
                projectIds.Add(project.Id);
            }

            directApiProjectGrant = DirectApiProjectGrant.Explicit(projectIds);
        }

        var result = await store.CreatePatAsync(
            principal.Id,
            name,
            [scope],
            expiresAt,
            ct,
            directApiProjectGrant).ConfigureAwait(false);
        if (result.Status == PatCreateStatus.InvalidGrant)
            return Forbidden("The requested Project grant could not be bound");

        if (result.Status == PatCreateStatus.DuplicateName || result.Credential is null || result.Token is null)
        {
            return ApiResults.Conflict(
                $"A PAT named '{name}' already exists; revoke it before reusing the name",
                "pat_name_in_use");
        }

        var credential = result.Credential;
        await audit.RecordAsync(AuthAuditEvent.CredentialIssued(
            principal.Id, credential.Id, credential.Kind, credential.Name, credential.CreatedAt), ct)
            .ConfigureAwait(false);
        return Results.Json(
            new ApiResponse<PatCreatedResponse>(true, new PatCreatedResponse(
                credential.Id,
                credential.Name!,
                scope.Name,
                credential.Prefix!,
                result.Token,
                credential.ExpiresAt!.Value,
                credential.CreatedAt)),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ICredentialStore store,
        CancellationToken ct)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal principal)
            return Unauthorized();

        var credentials = await store.ListPatAsync(principal.Id, ct).ConfigureAwait(false);
        var items = credentials.Select(credential => new PatListItemResponse(
            credential.Id,
            credential.Name ?? "",
            credential.Prefix ?? "",
            credential.Scopes.Select(scope => scope.Name).ToArray(),
            credential.ExpiresAt,
            credential.RevokedAt,
            credential.CreatedAt));
        return ApiResults.Ok(new { tokens = items });
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext context,
        string name,
        ICredentialStore store,
        IAuthAuditRecorder audit,
        TimeProvider time,
        CancellationToken ct)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal principal)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(name))
            return ApiResults.BadRequest("PAT name is required", "pat_name_required");

        var revokedAt = time.GetUtcNow();
        var revoked = await store.RevokePatAsync(
            principal.Id, name, revokedAt, ct).ConfigureAwait(false);
        if (!revoked)
            return ApiResults.NotFound($"No PAT named '{name}'");

        // The credential id is the audit target; the revoke path only
        // knows the name, so resolve it from the list before emitting.
        var credentials = await store.ListPatAsync(principal.Id, ct).ConfigureAwait(false);
        var revokedCredential = credentials.FirstOrDefault(candidate => candidate.Name == name);
        await audit.RecordAsync(AuthAuditEvent.CredentialRevoked(
            principal.Id,
            revokedCredential?.Id ?? name,
            CredentialKind.Pat,
            name,
            revokedAt), ct)
            .ConfigureAwait(false);

        return ApiResults.Ok(new { name, revokedAt });
    }

    private static IResult Unauthorized() =>
        Results.Json(
            new ApiResponse<object>(false, Error: "Authentication required.", Code: "unauthorized"),
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(string message) =>
        ApiResults.Fail(message, StatusCodes.Status403Forbidden, "forbidden");

    private static bool ResolveScope(string? raw, out Scope scope)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            scope = Scope.Operator;
            return true;
        }

        return Scope.TryParse(raw.Trim(), out scope)
            && (scope.Equals(Scope.Operator) || scope.Equals(Scope.Readonly));
    }
}

public sealed record CreatePatRequest(
    string? Name,
    string? Scope,
    int? TtlHours,
    IReadOnlyList<string>? ProjectIds = null,
    bool AllProjects = false);

public sealed record PatCreatedResponse(
    string Id,
    string Name,
    string Scope,
    string Prefix,
    string Token,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record PatListItemResponse(
    string Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);

using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;

namespace Mohist.Server.Api;

/// <summary>
/// The product query surface for the auth audit trail — newest-first
/// events, optionally filtered by event kind,
/// cutoff time and limit. Records never carry token plaintext, so
/// nothing in this surface can leak a credential value.
/// </summary>
public static class AuditRoutes
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;

    public static WebApplication MapAuditRoutes(this WebApplication app)
    {
        app.MapGet("/api/audit/events", ListAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IAuthAuditEventStore store,
        string? kind,
        DateTimeOffset? since,
        int? limit,
        CancellationToken ct)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal)
            return Unauthorized();

        AuthAuditEventType? eventType = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<AuthAuditEventType>(kind, ignoreCase: true, out var parsed))
                return ApiResults.BadRequest($"Unknown audit event kind '{kind}'", "audit_kind_invalid");
            eventType = parsed;
        }

        var resolvedLimit = limit is null ? DefaultLimit : Math.Clamp(limit.Value, 1, MaxLimit);
        var events = await store.ListAsync(eventType, since, resolvedLimit, ct).ConfigureAwait(false);

        return ApiResults.Ok(new
        {
            events = events.Select(auditEvent => new AuditEventResponse(
                auditEvent.Id,
                auditEvent.SubjectId,
                auditEvent.EventType,
                auditEvent.TargetKind,
                auditEvent.TargetId,
                auditEvent.OccurredAt,
                auditEvent.Metadata)),
        });
    }

    private static IResult Unauthorized() =>
        Results.Json(
            new ApiResponse<object>(false, Error: "Authentication required.", Code: "unauthorized"),
            statusCode: StatusCodes.Status401Unauthorized);
}

public sealed record AuditEventResponse(
    string Id,
    string SubjectId,
    AuthAuditEventType EventType,
    string TargetKind,
    string TargetId,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Metadata);

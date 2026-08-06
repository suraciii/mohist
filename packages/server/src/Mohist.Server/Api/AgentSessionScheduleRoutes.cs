using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Scheduled-input API for AgentSessions: create a durable one-shot
/// schedule, list all schedules of a session, and cancel a pending
/// schedule. Delivery happens inside the Session grain when the due
/// instant passes; this surface never leaks Runner / runtime facts.
/// </summary>
public static class AgentSessionScheduleRoutes
{
    internal static readonly IReadOnlySet<string> AllowedTopLevelFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "text",
        "dueAt",
    };

    private static readonly string[] Rfc3339Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    public static WebApplication MapAgentSessionScheduleRoutes(this WebApplication app)
    {
        var group = app.MapGroup(AgentSessionFollowupRoutes.FollowupPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{sessionId}/schedules", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            HttpRequest request,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            JsonElement raw;
            try
            {
                raw = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, JSON.Options, ct);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("request body is not valid JSON", "schedule_body_invalid");
            }
            if (raw.ValueKind != JsonValueKind.Object)
            {
                return ApiResults.BadRequest("request body must be a JSON object", "schedule_body_invalid");
            }

            var undeclared = new List<string>();
            foreach (var property in raw.EnumerateObject())
            {
                if (!AllowedTopLevelFields.Contains(property.Name))
                    undeclared.Add(property.Name);
            }
            if (undeclared.Count > 0)
            {
                return ApiResults.BadRequest(
                    $"unsupported top-level field(s): {string.Join(", ", undeclared)}; " +
                    "the schedule body accepts only text and dueAt.",
                    "unsupported_field",
                    new { fields = undeclared.ToArray() });
            }

            string? text = null;
            if (raw.TryGetProperty("text", out var textElement) && textElement.ValueKind != JsonValueKind.Null)
            {
                if (textElement.ValueKind != JsonValueKind.String)
                {
                    return ApiResults.BadRequest("text must be a string", "schedule_body_invalid");
                }
                text = textElement.GetString();
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return ApiResults.BadRequest("schedule requires non-empty text", "schedule_text_required");
            }

            if (!raw.TryGetProperty("dueAt", out var dueAtElement) || dueAtElement.ValueKind != JsonValueKind.String)
            {
                return ApiResults.BadRequest(
                    "dueAt must be an RFC 3339 timestamp with a timezone offset (Z or ±hh:mm)",
                    "schedule_due_invalid");
            }
            var dueAtText = dueAtElement.GetString()?.Trim();
            if (string.IsNullOrEmpty(dueAtText)
                || !DateTimeOffset.TryParseExact(
                    dueAtText,
                    Rfc3339Formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dueAt))
            {
                return ApiResults.BadRequest(
                    "dueAt must be an RFC 3339 timestamp with a timezone offset (Z or ±hh:mm)",
                    "schedule_due_invalid");
            }

            var project = context.GetResolvedProject();
            var target = await sessions.ResolveCanonicalFollowupTargetAsync(project.Id, sessionId, ct);
            if (target is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            var idempotencyKey = AgentSessionRecoveryRoutes.RecoveryIdempotencyKey(context);
            try
            {
                var created = await grains.GetGrain<IAgentSessionGrain>(target.SessionId)
                    .CreateScheduleAsync(new CreateSessionScheduleCommand(text!, dueAt, idempotencyKey));
                return ApiResults.Ok(ToDto(created.Schedule, created.AlreadyExists));
            }
            catch (ScheduleDueInPastException)
            {
                return ApiResults.BadRequest(
                    "dueAt must be strictly after the server's current time",
                    "schedule_due_in_past");
            }
            catch (ScheduleIdempotencyConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, "idempotency_conflict", new { sessionId = ex.SessionId });
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith($"Agent session {target.SessionId} does not exist", StringComparison.Ordinal))
            {
                return ApiResults.NotFound(ex.Message);
            }
        });

        group.MapGet("/{sessionId}/schedules", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var target = await sessions.ResolveCanonicalFollowupTargetAsync(project.Id, sessionId, ct);
            if (target is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            var schedules = await grains.GetGrain<IAgentSessionGrain>(target.SessionId).ListSchedulesAsync();
            return ApiResults.Ok(schedules.Select(schedule => ToDto(schedule)).ToArray());
        });

        group.MapPost("/{sessionId}/schedules/{scheduleId}/cancel", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            string scheduleId,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var target = await sessions.ResolveCanonicalFollowupTargetAsync(project.Id, sessionId, ct);
            if (target is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            try
            {
                var cancelled = await grains.GetGrain<IAgentSessionGrain>(target.SessionId)
                    .CancelScheduleAsync(new CancelSessionScheduleCommand(scheduleId));
                return ApiResults.Ok(ToDto(cancelled.Schedule));
            }
            catch (ScheduleNotFoundException)
            {
                return ApiResults.NotFound($"Schedule {scheduleId} not found");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith($"Agent session {target.SessionId} does not exist", StringComparison.Ordinal))
            {
                return ApiResults.NotFound(ex.Message);
            }
        });

        return app;
    }

    private static AgentSessionScheduleDto ToDto(SessionScheduleRecord schedule, bool alreadyExists = false) => new(
        schedule.ScheduleId,
        schedule.DueAt,
        schedule.Text,
        schedule.Status.ToString().ToLowerInvariant(),
        schedule.CreatedAt,
        schedule.CancelledAt,
        schedule.InputId,
        schedule.IdempotencyKey,
        alreadyExists);
}

public sealed record AgentSessionScheduleDto(
    string ScheduleId,
    DateTime DueAt,
    string Text,
    string Status,
    DateTime CreatedAt,
    DateTime? CancelledAt,
    string? InputId,
    string IdempotencyKey,
    bool AlreadyExists = false);

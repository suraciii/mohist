using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Contracts;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Canonical follow-up endpoint for AgentSessions from either source.
/// Follow-up joins the active turn or starts a user-initiated turn when the
/// session is idle; neither case creates a TaskRun or AgentJob. The issue-scoped
/// <c>POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup</c>
/// route (<see cref="IssueRoutes.MapIssueSessions"/>) is a Workflow lookup
/// alias that resolves to the same stable AgentSession id and returns the same
/// <see cref="AgentSessionFollowupResult"/> shape before using its
/// Workflow-shaped runner target. The resolver in
/// <see cref="AgentSessionQuerier.ResolveGenericFollowupTargetAsync"/> reads
/// the runner id from the session's Runtime state.
/// </summary>
public static class AgentSessionFollowupRoutes
{
    public const string FollowupPathPrefix = "/api/projects/{projectRef}/agent-sessions";

    internal static readonly IReadOnlySet<string> AllowedTopLevelFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "text",
        "attachments",
    };

    public static WebApplication MapAgentSessionFollowupRoutes(this WebApplication app)
    {
        var group = app.MapGroup(FollowupPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/{sessionId}", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var summary = await sessions.GetGenericSessionSummaryAsync(project.Id, sessionId, ct);
            return summary is null
                ? ApiResults.NotFound($"Agent session {sessionId} not found")
                : ApiResults.Ok(summary);
        });

        group.MapGet("/{sessionId}/transcript", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            string? runtimeSessionId,
            AgentSessionQuerier sessions,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var transcript = await sessions.GetGenericSessionTranscriptAsync(project.Id, sessionId, runtimeSessionId, ct);
            return transcript is null
                ? ApiResults.NotFound($"Agent session {sessionId} not found")
                : ApiResults.Ok(transcript);
        });

        group.MapPost("/{sessionId}/followup", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            HttpRequest request,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            AgentSessionFollowupDispatcher dispatcher,
            AttachmentService attachments,
            CancellationToken ct) =>
        {
            JsonElement raw;
            try
            {
                raw = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, JSON.Options, ct);
            }
            catch (JsonException)
            {
                return Rejected(sessionId, "followup_body_invalid", "request body is not valid JSON");
            }
            if (raw.ValueKind != JsonValueKind.Object)
            {
                return Rejected(sessionId, "followup_body_invalid", "request body must be a JSON object");
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
                    "the follow-up body accepts only text and attachments.",
                    "unsupported_field",
                    new { fields = undeclared.ToArray() });
            }

            var text = raw.TryGetProperty("text", out var textElement)
                && textElement.ValueKind != JsonValueKind.Null
                ? textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()
                    : throw new JsonException("text must be a string")
                : null;
            IReadOnlyList<string>? attachmentIds = null;
            if (raw.TryGetProperty("attachments", out var attachmentsElement)
                && attachmentsElement.ValueKind != JsonValueKind.Null)
            {
                attachmentIds = TryReadAttachments(attachmentsElement);
            }

            var hasText = !string.IsNullOrWhiteSpace(text);
            var hasAttachments = attachmentIds is { Count: > 0 };
            if (!hasText && !hasAttachments)
            {
                return Rejected(sessionId, "followup_input_required",
                    "follow-up requires non-empty text or at least one accepted attachment");
            }

            var project = context.GetResolvedProject();
            var idempotencyKey = AgentSessionRecoveryRoutes.RecoveryIdempotencyKey(context);
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                idempotencyKey = Guid.NewGuid().ToString("N");

            // Pre-mint the input id so we can validate+bind attachments
            // before the Session grain mints the durable input. The grain
            // adopts this id verbatim when supplied.
            var preMintedInputId = AgentLaunchCoordinatorCodec.StableToken(
                $"{sessionId}\n{idempotencyKey}\nfollowup-input");

            AgentInputAttachmentAcceptanceBatch attachmentBatch;
            try
            {
                attachmentBatch = await attachments.ValidateAndBindAgentInputAsync(
                    project.Id,
                    agentSessionId: sessionId,
                    inputId: preMintedInputId,
                    attachmentIds,
                    ct);
            }
            catch (AttachmentLimitException ex)
            {
                return Rejected(sessionId, "followup_attachment_limit", ex.Message);
            }
            catch (AttachmentValidationException ex)
            {
                return Rejected(sessionId, "followup_attachment_invalid", ex.Message);
            }

            if (attachmentBatch.AcceptedCount == 0 && !hasText)
            {
                return Rejected(sessionId, "followup_input_unusable",
                    "follow-up has no usable content: all attachments were rejected",
                    attachmentBatch.Results);
            }

            return await ExecuteFollowupAsync(
                project.Id,
                sessionId,
                text ?? string.Empty,
                idempotencyKey,
                attachmentBatch.Results
                    .Where(r => r.IsAccepted && r.Descriptor is not null)
                    .Select(r => r.Descriptor!)
                    .ToArray(),
                attachmentBatch.Results,
                preMintedInputId,
                sessions,
                grains,
                dispatcher,
                ct);
        });

        return app;
    }

    private static IReadOnlyList<string> TryReadAttachments(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("attachments must be an array of attachment ids");
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Null) continue;
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("attachments entries must be strings");
            }
            var raw = entry.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (seen.Add(raw.Trim()))
            {
                ids.Add(raw.Trim());
            }
        }
        return ids;
    }

    internal static async Task<IResult> ExecuteFollowupAsync(
        string projectId,
        string sessionId,
        string text,
        string idempotencyKey,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments,
        IReadOnlyList<AgentInputAttachmentAcceptance>? attachmentResults,
        string? preMintedInputId,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher dispatcher,
        CancellationToken ct)
    {
        var target = await sessions.ResolveCanonicalFollowupTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return Rejected(sessionId, "not_found", $"Agent session {sessionId} not found");

        var grain = grains.GetGrain<IAgentSessionGrain>(target.SessionId);
        AgentSessionFollowupAcceptResult accept;
        try
        {
            accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: text,
                Source: "agent-session-followup",
                IdempotencyKey: idempotencyKey,
                Attachments: attachments,
                PreMintedInputId: preMintedInputId,
                AttachmentResults: attachmentResults));
        }
        catch (RuntimeSessionMissingException ex)
        {
            return Rejected(ex.SessionId, "runtime_session_missing", ex.Message);
        }
        catch (RecoveryOperationInProgressException ex)
        {
            return Rejected(ex.SessionId, "recovery_in_progress", ex.Message);
        }
        catch (AgentSessionFollowupCapacityExceededException ex)
        {
            return Rejected(ex.SessionId, "capacity_exceeded", ex.Message);
        }
        catch (FollowupOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "followup_in_progress", new { sessionId = ex.SessionId });
        }
        catch (StopOperationInProgressException ex)
        {
            return ApiResults.Conflict(ex.Message, "stop_in_progress", new { sessionId = ex.SessionId, turnId = ex.TurnId });
        }
        catch (SessionActivityUnknownException ex)
        {
            return ApiResults.Conflict(ex.Message, "session_activity_unknown", new { sessionId = ex.SessionId });
        }
        catch (FollowupConcurrencyLimitException ex)
        {
            return ApiResults.Conflict(
                ex.Message,
                "concurrency_limit",
                new { sessionId = ex.SessionId, agentId = ex.AgentId });
        }
        catch (InvalidOperationException ex)
        {
            return Rejected(target.SessionId, "followup_rejected", ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ApiResults.Ok(new AgentSessionFollowupResult(
                target.SessionId,
                Status: "unknown",
                Error: ex.Message,
                Code: "followup_acceptance_unknown"));
        }

        await dispatcher.DispatchNextAsync(projectId, target.SessionId, ct);

        return ApiResults.Ok(BuildAcceptedResult(target.SessionId, accept));
    }

    private static AgentSessionFollowupResult BuildAcceptedResult(string sessionId, AgentSessionFollowupAcceptResult accept)
    {
        IReadOnlyList<AgentSessionLaunchAttachment>? accepted = null;
        IReadOnlyList<AgentSessionLaunchAttachmentRejection>? rejected = null;
        if (accept.Attachments is { Count: > 0 } attachments)
        {
            accepted = attachments
                .Select(a => new AgentSessionLaunchAttachment(a.Id, a.OriginalFileName, a.ContentType, a.Size))
                .ToArray();
        }
        var verdicts = accept.AttachmentResults;
        if (verdicts is { Count: > 0 })
        {
            rejected = verdicts
                .Where(r => !r.IsAccepted)
                .Select(r => new AgentSessionLaunchAttachmentRejection(
                    r.Id,
                    r.RejectionReason?.ToString() ?? "unknown",
                    r.RejectionMessage ?? "Attachment was rejected."))
                .ToArray();
        }
        return new AgentSessionFollowupResult(
            sessionId,
            InputId: accept.InputId,
            TurnId: accept.TurnId,
            Status: "accepted",
            InputAcceptance: AgentSessionObservationMapper.InputAcceptance(accept.InputAcceptance),
            TurnStatus: AgentSessionObservationMapper.TurnStatus(accept.TurnStatus),
            Attachments: accepted ?? [],
            RejectedAttachments: rejected ?? []);
    }

    private static IResult Rejected(
        string sessionId,
        string code,
        string error,
        IReadOnlyList<AgentInputAttachmentAcceptance>? attachmentResults = null) =>
        ApiResults.Ok(new AgentSessionFollowupResult(
            sessionId,
            Status: "rejected",
            Error: error,
            Code: code,
            Attachments: attachmentResults?
                .Where(r => r.IsAccepted)
                .Select(r => new AgentSessionLaunchAttachment(r.Id, r.Descriptor!.OriginalFileName, r.Descriptor.ContentType, r.Descriptor.Size))
                .ToArray() ?? [],
            RejectedAttachments: attachmentResults?
                .Where(r => !r.IsAccepted)
                .Select(r => new AgentSessionLaunchAttachmentRejection(r.Id, r.RejectionReason?.ToString() ?? "unknown", r.RejectionMessage ?? "Attachment was rejected."))
                .ToArray() ?? []));
}

/// <summary>
/// Body for <c>POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup</c>.
/// The route binds the JSON object directly (no longer a simple record) so it
/// can apply the same raw-JSON presence allowlist used by the launch path.
/// <see cref="Text"/> is optional: an attachment-only input is a valid follow-up.
/// Whitespace-only text is accepted as no-text so the attachment-only rule
/// still triggers when attachments are also supplied.
/// </summary>
public sealed record GenericFollowupRequest(string? Text = null);

public sealed record AgentSessionFollowupResult(
    string? SessionId,
    string? InputId = null,
    string? TurnId = null,
    string Status = "accepted",
    string? Error = null,
    string? Code = null,
    string? InputAcceptance = null,
    string? TurnStatus = null,
    IReadOnlyList<AgentSessionLaunchAttachment>? Attachments = null,
    IReadOnlyList<AgentSessionLaunchAttachmentRejection>? RejectedAttachments = null);

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Canonical AgentSession cancel endpoint for either source, addressed by the
/// stable session id. Cancel is intentionally outside the Compact/Reset idle
/// boundary: it interrupts only the current turn and never deletes the
/// AgentSession, transcript, lineage, or other persisted session state. The
/// interrupt is best-effort over the execution backend, and the route returns
/// the state reported by that backend.
/// </summary>
/// <remarks>
/// <para>
/// The cancel path is a separate SignalR method from <c>ReceiveFollowup</c>
/// because cancel needs a reply path (runner → server → client) for the
/// honest state, whereas followup is strictly fire-and-forget. The wire
/// payload is the same <see cref="SessionTarget"/> shape introduced in
/// T-004; T-005 only adds a new method name + reply shape.
/// </para>
/// <para>
/// The HTTP response mirrors the runner's reported state
/// (<c>cancelled</c> / <c>not-cancellable</c> / <c>&lt;terminal-state&gt;</c>)
/// so the API can never fake success.
/// </para>
/// </remarks>
public static class AgentSessionCancelRoutes
{
    public const string CancelPathPrefix = "/api/projects/{projectRef}/agent-sessions";

    public static WebApplication MapAgentSessionCancelRoutes(this WebApplication app)
    {
        var group = app.MapGroup(CancelPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{sessionId}/cancel", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionQuerier sessions,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var target = await sessions.ResolveGenericCancelTargetAsync(project.Id, sessionId, ct);
            if (target is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            // Terminal short-circuit: the session is already in
            // completed/failed/stopped. Return the current terminal state
            // honestly without calling the runner — there is no turn to
            // cancel and the runner cannot change the recorded outcome.
            if (!string.IsNullOrEmpty(target.TerminalState))
                return ApiResults.Ok(new { state = target.TerminalState });

            // The session is not terminal. If no runner ever bound itself
            // (RunnerId empty), there is no live runtime session to cancel —
            // mirror the "not-cancellable" contract rather than faking
            // success. The state name reuses the same value the runner
            // reports so the HTTP shape is uniform.
            if (string.IsNullOrWhiteSpace(target.RunnerId))
                return ApiResults.Ok(new { state = "not-cancellable" });

            var connectionId = connections.GetConnectionId(target.RunnerId);
            if (string.IsNullOrWhiteSpace(connectionId))
                return ApiResults.Ok(new { state = "not-cancellable" });

            // Server → runner invocation. The runner handler is
            // `CancelAgentSession` (distinct from `ReceiveFollowup`): it
            // MUST return a { state: ... } reply. The reply types
            // accepted are "cancelled", "not-cancellable", or a
            // terminal-state name (completed/failed/stopped). The route
            // never invents a state — it mirrors the runner's reported
            // value verbatim so the API cannot fake success.
            var reply = await runnerHub.Clients.Client(connectionId).InvokeAsync<AgentSessionCancelReply>(
                "CancelAgentSession",
                new
                {
                    target = new
                    {
                        kind = "generic",
                        projectId = project.Id,
                        sessionId = target.SessionId,
                    },
                },
                ct);

            if (reply is null || string.IsNullOrWhiteSpace(reply.State))
                return ApiResults.Fail(
                    "Runner did not report a cancel state",
                    502,
                    "runner_no_response",
                    new { sessionId });

            return ApiResults.Ok(new { state = reply.State });
        });

        return app;
    }
}

/// <summary>
/// Reply shape for the server→runner <c>CancelAgentSession</c> SignalR
/// invocation (issue-129 T-005 / design D6). The runner reports the
/// session state it actually observed so the API can never pretend
/// success. Recognised values:
/// <list type="bullet">
///   <item><c>cancelled</c> — the runner sent a cancel request to the
///     current runtime session.</item>
///   <item><c>not-cancellable</c> — the runner has no live runtime session
///     for the target, or the backend does not support cancellation.</item>
///   <item><c>completed</c> / <c>failed</c> / <c>stopped</c> — the session
///     transitioned to a terminal state as a side effect of the cancel
///     request; the API surfaces the same value so the caller can react
///     without polling.</item>
/// </list>
/// </summary>
public sealed record AgentSessionCancelReply(string? State);

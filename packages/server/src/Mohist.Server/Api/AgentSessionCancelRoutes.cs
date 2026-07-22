using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
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
            IGrainFactory grains,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            return await ExecuteCancelAsync(project.Id, sessionId, sessions, grains, runnerHub, connections, ct);
        });

        return app;
    }

    internal static async Task<IResult> ExecuteCancelAsync(
        string projectId,
        string sessionId,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections,
        CancellationToken ct)
    {
        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return ApiResults.NotFound($"Agent session {sessionId} not found");

        if (!string.IsNullOrEmpty(target.TerminalState))
            return ApiResults.Ok(new { state = target.TerminalState });

        if (string.IsNullOrWhiteSpace(target.RunnerId))
            return ApiResults.Ok(new { state = "not-cancellable" });

        var connectionId = connections.GetConnectionId(target.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
            return ApiResults.Ok(new { state = "not-cancellable" });

        try
        {
            await grains.GetGrain<IAgentSessionGrain>(target.SessionId).EnsureRuntimeSessionPresentAsync();
        }
        catch (RuntimeSessionMissingException)
        {
            return ApiResults.Ok(new { state = "not-cancellable" });
        }

        if (string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId))
        {
            return ApiResults.Ok(new { state = "not-cancellable" });
        }

        object binding = new
        {
            runtime = target.Runtime,
            runtimeSessionId = target.RuntimeSessionId,
            runnerId = target.RunnerId,
            workDir = target.WorkDir,
        };
        object wireTarget = string.Equals(target.SourceKind, "workflow", StringComparison.Ordinal)
            ? new
            {
                kind = "workflow",
                projectId,
                workflowRunId = target.WorkflowRunId,
                sessionName = target.SessionName,
                binding,
            }
            : new
            {
                kind = "generic",
                projectId,
                sessionId = target.SessionId,
                binding,
            };

        AgentSessionCancelReply? reply;
        try
        {
            reply = await runnerHub.Clients.Client(connectionId).InvokeAsync<AgentSessionCancelReply>(
                "CancelAgentSession",
                new { target = wireTarget },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ApiResults.Ok(new { state = "not-cancellable" });
        }

        if (reply is null || string.IsNullOrWhiteSpace(reply.State))
            return ApiResults.Fail(
                "Runner did not report a cancel state",
                502,
                "runner_no_response",
                new { sessionId });

        return reply.InterruptUnconfirmed == true
            ? ApiResults.Ok(new { state = reply.State, interruptUnconfirmed = true })
            : ApiResults.Ok(new { state = reply.State });
    }
}

/// <summary>
/// Reply shape for the server→runner <c>CancelAgentSession</c> SignalR
/// invocation (issue-129 T-005 / design D6 + issue-451 T-004 / design D6).
/// The runner reports the session state it actually observed so the API
/// can never pretend success. Recognised values:
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
/// <para>
/// <c>interruptUnconfirmed</c> is the additive honesty flag the API
/// needs to surface a stop the runner could not confirm (issue-451
/// T-004 / design D6). OpenCode replies never set the flag because the
/// OpenCode abort is authoritative; Pi replies set it exactly when the
/// session's stop state could not be observed. Absent on a confirmed
/// cancel so existing callers see byte-identical responses.
/// </para>
/// </summary>
public sealed record AgentSessionCancelReply(string? State, bool? InterruptUnconfirmed = null);

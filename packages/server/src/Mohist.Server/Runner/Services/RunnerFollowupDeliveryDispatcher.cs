using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services;

public sealed class RunnerFollowupDeliveryDispatcher : IFollowupDeliveryDispatcher
{
    private readonly IRunnerControlTransport _control;
    private readonly ILogger<RunnerFollowupDeliveryDispatcher> _log;

    public RunnerFollowupDeliveryDispatcher(
        IRunnerControlTransport control,
        ILogger<RunnerFollowupDeliveryDispatcher> log)
    {
        _control = control;
        _log = log;
    }

    public async Task<FollowupDeliveryResult> DispatchAsync(FollowupDeliveryRequest request, CancellationToken ct = default)
    {
        var binding = new RunnerSessionBinding(
            request.Runtime,
            request.RuntimeSessionId,
            request.RunnerId,
            request.WorkDir);
        var target = string.Equals(request.SourceKind, "workflow", StringComparison.Ordinal)
            ? new RunnerSessionTarget(
                "workflow",
                request.ProjectId,
                binding,
                WorkflowRunId: request.WorkflowRunId,
                SessionName: request.SessionName,
                SessionId: request.SessionId)
            : new RunnerSessionTarget(
                "generic",
                request.ProjectId,
                binding,
                SessionId: request.SessionId,
                Definition: request.Definition);
        var payload = new FollowupParams(
            target,
            string.Join("\n", request.InputTexts),
            request.OperationId,
            request.InputId,
            request.TurnId!,
            request.SlackExecutionContext,
            request.Attachments is { Count: > 0 }
                ? request.Attachments
                    .Select(descriptor => new FollowupAttachmentDescriptor(
                        descriptor.Id,
                        descriptor.OriginalFileName,
                        descriptor.ContentType,
                        descriptor.Size))
                    .ToArray()
                : null);

        try
        {
            var response = await _control.SendRequestAsync<FollowupParams, RunnerFollowupDeliveryResult>(
                request.RunnerId,
                "session.followup",
                payload,
                ct: ct);
            return new FollowupDeliveryResult(response.Accepted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Runner {RunnerId} failed to receive follow-up for AgentSession {SessionId}",
                request.RunnerId,
                request.SessionId);
            return new FollowupDeliveryResult(false);
        }
    }
}

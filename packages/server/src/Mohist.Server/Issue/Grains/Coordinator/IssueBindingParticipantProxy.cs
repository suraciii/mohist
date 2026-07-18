using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Grains;

namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-417 D2: adapts <see cref="IIssueGrain"/> to the narrow
/// <see cref="IIssueBindingParticipant"/> surface the coordinator
/// consumes. Each participant method captures the issue's
/// coordination revision, applies the receipt-bearing transaction
/// against the underlying Issue aggregate, and surfaces typed
/// exceptions that the coordinator translates into
/// <see cref="IssueRepositoryBindingResult"/>. The proxy is itself
/// non-reentrant (Orleans default) so two coordinator activations
/// cannot interleave their participant calls against the same issue.
/// </summary>
public class IssueBindingParticipantProxy : Grain, IIssueBindingParticipant
{
    private readonly IGrainFactory _grains;

    public IssueBindingParticipantProxy(IGrainFactory grains)
    {
        _grains = grains;
    }

    private static string IssueGrainKey(string projectId, int issueNumber) =>
        GrainKey.Issue(new IssueKey(projectId, issueNumber));

    public async Task<IssueBindingParticipantOutcome> CreateAsync(
        RepositoryCommandPayload.Create payload,
        string commandId,
        long? expectedRevision)
    {
        var grainKey = IssueGrainKey(payload.ProjectId, payload.IssueNumber);
        try
        {
            await BindingParticipantProbe.BeforeParticipantAsync(
                BindingParticipantProbeKind.Create,
                grainKey,
                commandId);
            return await _grains.GetGrain<IIssueBindingTarget>(grainKey).CreateWithReceiptAsync(
                payload.ProjectId,
                payload.IssueNumber,
                payload.Title,
                payload.Body,
                payload.Labels,
                payload.Priority,
                payload.RepositoryName,
                payload.Risk,
                payload.IsDraft,
                payload.AttachmentIds,
                payload.WorkflowProfileId,
                payload.PrerequisiteNumbers,
                payload.ParentIssueNumber,
                commandId,
                expectedRevision);
        }
        catch (IssueRepositoryStaleRevisionException)
        {
            throw;
        }
        catch (IssueRepositoryUnknownException)
        {
            throw;
        }
    }

    public async Task<IssueBindingParticipantOutcome> ChangeRepositoryAsync(
        RepositoryCommandPayload.Change payload,
        string commandId,
        long? expectedRevision)
    {
        var grainKey = IssueGrainKey(payload.ProjectId, payload.IssueNumber);
        try
        {
            await BindingParticipantProbe.BeforeParticipantAsync(
                BindingParticipantProbeKind.Change,
                grainKey,
                commandId);
            return await _grains.GetGrain<IIssueBindingTarget>(grainKey).ChangeRepositoryWithReceiptAsync(
                new IssueChangeRepositoryCommand(
                    payload.RepositoryName,
                    payload.Title,
                    payload.Body,
                    payload.Labels,
                    payload.Priority,
                    payload.IsDraft,
                    payload.AttachmentIds,
                    payload.WorkflowProfileId,
                    payload.PresentFields,
                    payload.ParentIssueNumber),
                commandId,
                expectedRevision);
        }
        catch (IssueRepositoryStaleRevisionException)
        {
            throw;
        }
        catch (IssueRepositoryUnknownException)
        {
            throw;
        }
        catch (IssueRepositoryLockedException)
        {
            throw;
        }
    }

    public async Task<IssueBindingParticipantOutcome> ReopenAsync(
        RepositoryCommandPayload.Reopen payload,
        string commandId,
        long? expectedRevision)
    {
        var grainKey = IssueGrainKey(payload.ProjectId, payload.IssueNumber);
        try
        {
            await BindingParticipantProbe.BeforeParticipantAsync(
                BindingParticipantProbeKind.Reopen,
                grainKey,
                commandId);
            return await _grains.GetGrain<IIssueBindingTarget>(grainKey).ReopenWithReceiptAsync(commandId, expectedRevision);
        }
        catch (IssueRepositoryStaleRevisionException)
        {
            throw;
        }
        catch (IssueRepositoryMissingOnReopenException)
        {
            throw;
        }
    }
}

public enum BindingParticipantProbeKind
{
    Create = 0,
    Change = 1,
    Reopen = 2,
    Remove = 3,
}

/// <summary>
/// issue-417 T-005: test-only static probe that runs synchronously
/// before the coordinator invokes a participant. Tests set this hook
/// to await on a TaskCompletionSource so the test can force
/// ordering / lost-response points without wall-clock waits. Production
/// callers MUST NOT touch this — leaving the probe null is the
/// intended zero-overhead path.
/// </summary>
public static class BindingParticipantProbe
{
    private static Func<BindingParticipantProbeKind, string, string, Task>? _hook;

    public static IDisposable Install(Func<BindingParticipantProbeKind, string, string, Task> hook)
    {
        _hook = hook;
        return new ResetOnDispose();
    }

    public static Task BeforeParticipantAsync(BindingParticipantProbeKind kind, string aggregateId, string commandId)
    {
        var hook = _hook;
        return hook is null ? Task.CompletedTask : hook(kind, aggregateId, commandId);
    }

    private sealed class ResetOnDispose : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hook = null;
        }
    }
}

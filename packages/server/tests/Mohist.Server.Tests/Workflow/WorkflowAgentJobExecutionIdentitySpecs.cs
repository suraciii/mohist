using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Workflow;

public sealed partial class WorkflowAgentJobExecutionSpecs
{
    [Fact]
    public async Task WorkflowAgentHandoff_NamedSessionRejectsDifferentCanonicalAgentAndAbortsPreparedJob()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("bootstrap", [AgentTask("bootstrap", "Bootstrap", "delivery")], [])
        ]);
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-identity-mismatch-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);
        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));

        var run = await LoadRunAsync(_workflowId!);
        var bootstrapAttempt = Assert.Single(run.CurrentStage().Tasks);
        var bootstrapHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(
                run.Metadata.ProjectId!,
                run.Id,
                run.CurrentStage().Id,
                bootstrapAttempt.Id,
                bootstrapAttempt.WorkId!));
        await bootstrapHandoff.ActivateAsync();
        var bootstrapPlan = await bootstrapHandoff.GetPlanAsync();
        Assert.NotNull(bootstrapPlan?.Invocation);
        var bootstrapWork = (await PollWorkAsync(runnerId)).Work;
        await ReportAsync(runnerId, bootstrapWork, "completed");
        await AttachRuntimeSessionAsync(bootstrapWork.AgentSessionId!);

        await using var scope = Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var before = await sessions.LoadAsync(bootstrapWork.AgentSessionId!);
        Assert.NotNull(before);
        var beforeInputCount = before!.Status.Inputs!.Count;
        var beforeTurnCount = before.Status.Turns!.Count;
        var beforeLeaseCount = before.Status.PendingFollowups?.Count ?? 0;
        var beforeLastDataAt = before.Status.LastDataAt;
        var beforeEvidenceCount = before.Status.PendingTranscriptEvidence?.Count ?? 0;

        const string mismatchIdentity = "review-with-builder-session.1";
        var mismatchCommand = bootstrapPlan!.Command with
        {
            CommandId = mismatchIdentity,
            ActionAttemptId = mismatchIdentity,
            AgentRef = "mohist/reviewer",
            Prompt = "Review with the wrong named Session",
            ReuseSessionId = bootstrapPlan.Invocation!.SessionId,
            Completion = bootstrapPlan.Command.Completion! with { WorkId = mismatchIdentity },
        };
        var mismatchHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(mismatchCommand));
        var prepared = await mismatchHandoff.PrepareAsync(mismatchCommand);
        var preparedPlan = await mismatchHandoff.GetPlanAsync();
        Assert.NotNull(preparedPlan?.AgentId);
        Assert.NotEqual(bootstrapPlan.AgentId, preparedPlan!.AgentId);
        await mismatchHandoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            mismatchIdentity,
            WorkflowAgentHandoffCodec.Fingerprint(mismatchCommand)));

        var failed = await mismatchHandoff.ActivateAsync();
        var failedPlan = await mismatchHandoff.GetPlanAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Failed, failed.Disposition);
        Assert.Equal("agent_session_identity_mismatch", failedPlan!.Rejection?.Code);
        Assert.Equal(WorkflowAgentActivationStep.EnsureSession, failedPlan.ActivationStep);
        Assert.Equal(AgentJobStatus.Cancelled,
            await Grains.GetGrain<IAgentJobGrain>(prepared.Invocation!.JobKey).GetStatusAsync());
        var after = await sessions.LoadAsync(bootstrapWork.AgentSessionId!);
        Assert.Equal(beforeInputCount, after!.Status.Inputs!.Count);
        Assert.Equal(beforeTurnCount, after.Status.Turns!.Count);
        Assert.Equal(beforeLeaseCount, after.Status.PendingFollowups?.Count ?? 0);
        Assert.Equal(beforeLastDataAt, after.Status.LastDataAt);
        Assert.Equal(beforeEvidenceCount, after.Status.PendingTranscriptEvidence?.Count ?? 0);

        var replay = await mismatchHandoff.ActivateAsync();
        Assert.Equal(WorkflowAgentHandoffDisposition.Failed, replay.Disposition);
        Assert.Equal(prepared.Invocation, replay.Invocation);
    }
}

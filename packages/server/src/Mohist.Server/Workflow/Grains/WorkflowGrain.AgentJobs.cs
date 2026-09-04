using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    private async Task<bool> TryLaunchAgentTaskAsync(WorkflowTaskWork work)
    {
        if (!string.Equals(work.Uses, "mohist/agent", StringComparison.Ordinal))
            return false;
        if (_workflowItemTranslator is null)
            throw new InvalidOperationException("Workflow Agent launch requires the Workflow item translator.");

        var item = _workLifecycle.BuildClaimableWorkItem(_run!, work)
            ?? throw new InvalidOperationException("Workflow Agent task could not be projected.");

        WorkflowAgentHandoffCommand command;
        try
        {
            command = await _workflowItemTranslator.BuildAgentHandoffCommandAsync(item, GrainKey, _run!);
        }
        catch (WorkflowDispatchRejectedException ex)
        {
            // A handoff that cannot even be rendered must terminate the
            // attempt durably, mirroring the prepared-rejection path below.
            // Leaving it Pending would hold the stage claim forever because
            // every later claim re-renders the same failure.
            await FailAgentAttemptAsync(work.Id, item.Id!, ex.Error);
            return true;
        }

        var key = WorkflowAgentHandoffCodec.KeyFor(command);
        var grain = GrainFactory.GetGrain<IWorkflowAgentHandoffGrain>(key);
        var prepared = await grain.PrepareAsync(command);
        if (prepared.Disposition == WorkflowAgentHandoffDisposition.Rejected)
        {
            var error = new ExecutionError(
                prepared.Rejection?.Code ?? "agent_launch_rejected",
                prepared.Rejection?.Message ?? "Workflow Agent launch was rejected.");
            await FailAgentAttemptAsync(command.ActionAttemptId, command.Completion!.WorkId, error);
            return true;
        }

        var plan = await grain.GetPlanAsync()
            ?? throw new InvalidOperationException("Prepared Workflow Agent handoff plan was not persisted.");
        var invocation = prepared.Invocation
            ?? throw new InvalidOperationException("Prepared Workflow Agent handoff has no invocation.");
        var started = _run!.StartAgentTask(
            command.Completion!.WorkId,
            invocation.InvocationId,
            invocation.JobKey,
            invocation.SessionId,
            plan.RequestFingerprint,
            Now());
        await CommitAsync(started);

        await grain.AcceptAsync(new WorkflowAgentHandoffAcceptance(command.CommandId, plan.RequestFingerprint));
        await grain.TriggerActivationAsync();
        return true;
    }

    private async Task FailAgentAttemptAsync(string actionAttemptId, string workId, ExecutionError error)
    {
        var current = _run!.CurrentStage();
        var attempt = current.Tasks.Single(task => string.Equals(task.Id, actionAttemptId, StringComparison.Ordinal));
        attempt.Status = WorkflowActionAttemptStatus.Running;
        attempt.StartedAt = Now();
        attempt.WorkId = workId;
        attempt.Error = error;
        var events = _run!.FailTask(current.Id, attempt.Id, new TaskResult("failed", error.Message, error), Now());
        await CommitAsync(events);
    }

    private async Task ReconcileWorkflowAgentHandoffsAsync()
    {
        if (_run is null || _run.Status.IsTerminal())
            return;
        foreach (var stage in _run.Stages)
        {
            foreach (var attempt in stage.Tasks)
            {
                if (attempt.Status != WorkflowActionAttemptStatus.Running
                    || !string.Equals(attempt.Uses, "mohist/agent", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(attempt.WorkId)
                    || string.IsNullOrWhiteSpace(attempt.AgentLaunchFingerprint))
                    continue;
                var projectId = _run.Metadata.ProjectId;
                if (string.IsNullOrWhiteSpace(projectId))
                    continue;
                var key = WorkflowAgentHandoffCodec.KeyFor(
                    projectId,
                    GrainKey,
                    stage.Id,
                    attempt.Id,
                    attempt.WorkId);
                var handoff = GrainFactory.GetGrain<IWorkflowAgentHandoffGrain>(key);
                await handoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
                    attempt.WorkId,
                    attempt.AgentLaunchFingerprint));
                await handoff.ActivateAsync();
            }
        }
    }

    public async Task<WorkReportVerdict> ReceiveAgentJobTerminalAsync(WorkflowAgentJobTerminalDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        RejectIfRunReloadRequired();
        if (_run is null)
            return WorkReportVerdict.Refused;
        var stage = _run.Stages.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, delivery.Stage, StringComparison.Ordinal));
        var attempt = stage?.Tasks.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, delivery.ActionAttemptId, StringComparison.Ordinal));
        if (stage is null || attempt is null
            || !string.Equals(attempt.Uses, "mohist/agent", StringComparison.Ordinal)
            || !string.Equals(attempt.WorkId, delivery.WorkId, StringComparison.Ordinal)
            || !string.Equals(attempt.AgentJobId, delivery.JobKey, StringComparison.Ordinal)
            || !string.Equals(attempt.AgentInvocationId, delivery.InvocationId, StringComparison.Ordinal)
            || !string.Equals(attempt.AgentLaunchFingerprint, delivery.RequestFingerprint, StringComparison.Ordinal))
            return WorkReportVerdict.Refused;

        if (string.Equals(attempt.TerminalResultFingerprint, delivery.DeliveryId, StringComparison.Ordinal))
            return WorkReportVerdict.Accepted;
        if (attempt.Status != WorkflowActionAttemptStatus.Running)
            return WorkReportVerdict.Refused;

        attempt.AgentSessionId = delivery.AgentSessionId ?? attempt.AgentSessionId;
        var succeeded = string.Equals(delivery.Status, "completed", StringComparison.Ordinal);
        var output = ParseAgentOutput(delivery.Output);
        var error = succeeded
            ? await ApplySetVarsAsync(attempt, output)
            : new ExecutionError(
                delivery.FailureCategory ?? "agent_failed",
                delivery.FailureReason ?? delivery.Message ?? "Agent execution failed.");
        if (error is not null) succeeded = false;
        var report = new TaskReport(
            WorkId: delivery.WorkId,
            Status: succeeded ? TaskReportStatus.Succeeded : TaskReportStatus.Failed,
            Output: output,
            Artifacts: null,
            Detail: delivery.Message ?? delivery.FailureReason,
            AddTasks: delivery.AddTasks,
            Error: error,
            ArtifactUploadIds: delivery.ArtifactUploadIds,
            ActionAttemptId: delivery.ActionAttemptId,
            TerminalResultFingerprint: delivery.DeliveryId);
        var item = WorkItem.Task(
            delivery.Stage,
            delivery.WorkId,
            attempt.Title,
            attempt.Uses,
            attempt.WithInput,
            attempt.Artifacts,
            attempt.SetVars,
            attempt.Recovery,
            attempt.RecoveryRemaining,
            attempt.ExpectInput);
        var active = new WorkflowActiveWork(item, attempt.Id, null);
        var artifactUploadIds = report.ArtifactUploadIds?.ToArray();
        report = await ValidateTaskReportArtifactsAsync(active, report);
        var events = await _workLifecycle.ApplyTaskReportAsync(
            _run,
            report,
            delivery.Stage,
            delivery.ActionAttemptId);
        if (artifactUploadIds is { Length: > 0 } && report.Artifacts is { Count: > 0 })
        {
            await CommitWithArtifactsAsync(events, new WorkflowArtifactBindingIntent(
                delivery.WorkId,
                delivery.ActionAttemptId,
                artifactUploadIds,
                Now(),
                GetProjectId(),
                GetIssueNumber()));
        }
        else
        {
            await CommitAsync(events);
        }
        return WorkReportVerdict.Accepted;
    }

    private async Task<ExecutionError?> ApplySetVarsAsync(WorkflowActionAttempt attempt, JsonElement? output)
    {
        if (attempt.SetVars is not { Count: > 0 } mappings || _runVariablesStore is null)
            return null;
        if (output is not { ValueKind: JsonValueKind.Object })
            return new ExecutionError("set_vars_failed", "Agent output is not an object required by setVars.");

        var values = new JsonObject();
        foreach (var (target, source) in mappings)
        {
            var path = source.StartsWith("output.", StringComparison.Ordinal)
                ? source["output.".Length..]
                : source;
            var current = output.Value;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                    return new ExecutionError("set_vars_failed", $"setVars source '{source}' was not found in Agent output.");
            }
            VariableJsonMerge.SetPath(values, target, current.Clone());
        }
        try
        {
            await PatchVariablesAsync(
                new VariableBundle(Vars: JSON.DeserializeElement(values.ToJsonString())));
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return new ExecutionError("set_vars_rejected", ex.Message);
        }
    }

    private static JsonElement? ParseAgentOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JSON.SerializeToElement(output);
        }
    }
}

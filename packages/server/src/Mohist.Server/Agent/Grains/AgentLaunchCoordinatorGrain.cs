using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans.Runtime;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Durably-keyed manual-agent-launch coordinator. The coordinator is a
/// narrow application process manager: it persists the canonical
/// request, the generated Job/Session/Input/Turn ids, and a
/// one-at-a-time command fence, then drives the four launch steps
/// across the AgentJob and AgentSession participants. It does not
/// mirror Job status, Session activity, transcript, or Runner state.
/// </summary>
public sealed class AgentLaunchCoordinatorGrain : Grain, IAgentLaunchCoordinatorGrain
{
    internal const string RecoveryReminderName = "agent-launch-coordinator-recovery";

    private static readonly TimeSpan RecoveryReminderDue = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryReminderPeriod = TimeSpan.FromSeconds(1);

    private readonly IPersistentState<AgentLaunchCoordinatorState> _state;
    private readonly IGrainFactory _grains;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentLaunchCoordinatorGrain> _log;

    public AgentLaunchCoordinatorGrain(
        [PersistentState("agent-launch-coordinator")] IPersistentState<AgentLaunchCoordinatorState> state,
        IGrainFactory grains,
        TimeProvider timeProvider,
        ILogger<AgentLaunchCoordinatorGrain> log)
    {
        _state = state;
        _grains = grains;
        _timeProvider = timeProvider;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();

        if (_state.State.Plan is null)
        {
            await UnregisterReminderAsync();
            return;
        }

        if (_state.State.Plan.Completed)
        {
            await UnregisterReminderAsync();
            return;
        }

        await EnsureRecoveryReminderAsync();
        await AdvanceAsync();
    }

    public async Task<AgentLaunchCoordinatorResult> LaunchAsync(AgentLaunchCoordinatorCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);

        if (string.IsNullOrWhiteSpace(command.ProjectId))
            throw new ArgumentException("ProjectId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.AgentId))
            throw new ArgumentException("AgentId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.AgentName))
            throw new ArgumentException("AgentName is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(command));

        var fingerprint = AgentLaunchCoordinatorCodec.Fingerprint(command.Request);

        var existing = _state.State.Plan;
        if (existing is not null)
        {
            if (!string.Equals(existing.ProjectId, command.ProjectId, StringComparison.Ordinal)
                || !string.Equals(existing.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "AgentLaunchCoordinatorGrain primary key does not match the supplied (ProjectId, IdempotencyKey).");
            }
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new LaunchIdempotencyConflictException(command.IdempotencyKey, existing.RequestFingerprint);
            }

            if (!existing.Completed)
            {
                await EnsureRecoveryReminderAsync();
                await AdvanceAsync();
            }
        }
        else
        {
            var plan = new AgentLaunchCoordinatorPlan(
                ProjectId: command.ProjectId,
                IdempotencyKey: command.IdempotencyKey,
                RequestFingerprint: fingerprint,
                JobKey: $"agent-job-launch-{Guid.NewGuid():N}",
                SessionId: $"agent-session-{Guid.NewGuid():N}",
                InputId: Guid.NewGuid().ToString("N"),
                TurnId: Guid.NewGuid().ToString("N"),
                AgentId: command.AgentId,
                AgentName: command.AgentName,
                AgentInstructions: command.AgentInstructions,
                AgentConfigJson: command.AgentConfigJson,
                Model: command.Model,
                Variant: command.Variant,
                Runtime: command.Runtime,
                Prompt: command.Prompt,
                WorkspacePath: command.WorkspacePath,
                IssueNumber: command.IssueNumber,
                EpicNumber: command.EpicNumber,
                Repository: command.Repository,
                Title: command.Title,
                AgentRef: command.Request.AgentRef,
                Completed: false);
            _state.State.Plan = plan;
            await SaveStateAsync();
            await EnsureRecoveryReminderAsync();
            await AdvanceAsync();
        }

        var final = _state.State.Plan
            ?? throw new InvalidOperationException("Coordinator plan disappeared after advance.");
        if (!final.Completed)
            throw new LaunchSetupPendingException(final.IdempotencyKey);
        return new AgentLaunchCoordinatorResult(
            JobKey: final.JobKey,
            SessionId: final.SessionId,
            InputId: final.InputId,
            TurnId: final.TurnId,
            AgentId: final.AgentId,
            AgentName: final.AgentName,
            AlreadyPersisted: existing?.Completed == true);
    }

    public async Task<AgentLaunchCoordinatorResult?> ResumeAsync(AgentLaunchCoordinatorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = _state.State.Plan;
        if (existing is null)
            return null;

        var fingerprint = AgentLaunchCoordinatorCodec.Fingerprint(request);
        if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new LaunchIdempotencyConflictException(existing.IdempotencyKey, existing.RequestFingerprint);

        if (!existing.Completed)
        {
            await EnsureRecoveryReminderAsync();
            await AdvanceAsync();
        }

        var final = _state.State.Plan
            ?? throw new InvalidOperationException("Coordinator plan disappeared after resume.");
        if (!final.Completed)
            throw new LaunchSetupPendingException(final.IdempotencyKey);
        return new AgentLaunchCoordinatorResult(
            JobKey: final.JobKey,
            SessionId: final.SessionId,
            InputId: final.InputId,
            TurnId: final.TurnId,
            AgentId: final.AgentId,
            AgentName: final.AgentName,
            AlreadyPersisted: true);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RecoveryReminderName, StringComparison.Ordinal))
            return;
        if (_state.State.Plan is null || _state.State.Plan.Completed)
        {
            await UnregisterReminderAsync();
            return;
        }
        await AdvanceAsync();
    }

    private async Task AdvanceAsync()
    {
        var plan = _state.State.Plan;
        if (plan is null || plan.Completed)
            return;

        try
        {
            if (plan.Pending is null)
            {
                await BeginPrepareAsync(plan);
            }
            else
            {
                await ResumePendingAsync(plan);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentLaunchCoordinatorGrain {Key} advance failed; reminder will retry",
                PrimaryKeyString());
        }
    }

    private async Task BeginPrepareAsync(AgentLaunchCoordinatorPlan plan)
    {
        var commandId = Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: commandId,
                Kind: AgentLaunchCoordinatorCommand.PrepareJob,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(plan.JobKey);
        await jobGrain.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: plan.SessionId,
            InputId: plan.InputId,
            TurnId: plan.TurnId,
            Prompt: plan.Prompt,
            Model: plan.Model,
            WorkspacePath: plan.WorkspacePath,
            ProjectId: plan.ProjectId,
            Runtime: plan.Runtime,
            AgentId: plan.AgentId,
            AgentInstructions: plan.AgentInstructions,
            AgentConfig: DeserializeAgentConfig(plan.AgentConfigJson),
            Variant: plan.Variant,
            IssueNumber: plan.IssueNumber,
            EpicNumber: plan.EpicNumber,
            WorkflowRunId: null));

        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: Guid.NewGuid().ToString("N"),
                Kind: AgentLaunchCoordinatorCommand.EnsureInitialLaunch,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();
        await AdvanceAsync();
    }

    private async Task ResumePendingAsync(AgentLaunchCoordinatorPlan plan)
    {
        switch (plan.Pending!.Kind)
        {
            case AgentLaunchCoordinatorCommand.PrepareJob:
                await BeginPrepareAsync(plan);
                break;
            case AgentLaunchCoordinatorCommand.EnsureInitialLaunch:
                await BeginEnsureInitialLaunchAsync(plan);
                break;
            case AgentLaunchCoordinatorCommand.SubmitJob:
                await BeginSubmitAsync(plan);
                break;
            default:
                _state.State.Plan = plan with { Pending = null };
                await SaveStateAsync();
                break;
        }
    }

    private async Task BeginEnsureInitialLaunchAsync(AgentLaunchCoordinatorPlan plan)
    {
        var commandId = plan.Pending?.CommandId ?? Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: commandId,
                Kind: AgentLaunchCoordinatorCommand.EnsureInitialLaunch,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();

        var sessionGrain = _grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = plan.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = plan.AgentId,
            [GenericAgentSessionMetadata.AgentName] = plan.AgentName,
        };
        if (plan.IssueNumber is > 0)
            labels[GenericAgentSessionMetadata.IssueNumber] = plan.IssueNumber.Value.ToString();
        if (plan.EpicNumber is > 0)
            labels[GenericAgentSessionMetadata.EpicNumber] = plan.EpicNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(plan.WorkspacePath))
            labels[GenericAgentSessionMetadata.WorkspacePath] = plan.WorkspacePath!;
        if (!string.IsNullOrWhiteSpace(plan.Repository))
            labels[GenericAgentSessionMetadata.Repository] = plan.Repository!;

        var metadata = new AgentSessionMetadata(labels, null);
        await sessionGrain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: plan.InputId,
            TurnId: plan.TurnId,
            Prompt: plan.Prompt,
            Source: "agent-launch",
            JobId: plan.JobKey,
            Metadata: metadata,
            Runtime: plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
            WorkDir: plan.WorkspacePath));

        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: Guid.NewGuid().ToString("N"),
                Kind: AgentLaunchCoordinatorCommand.SubmitJob,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();
        await AdvanceAsync();
    }

    private async Task BeginSubmitAsync(AgentLaunchCoordinatorPlan plan)
    {
        var commandId = plan.Pending?.CommandId ?? Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: commandId,
                Kind: AgentLaunchCoordinatorCommand.SubmitJob,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(plan.JobKey);
        await jobGrain.SubmitPreparedLaunchAsync();

        _state.State.Plan = plan with
        {
            Pending = null,
            Completed = true,
        };
        await SaveStateAsync();
        await UnregisterReminderAsync();
    }

    private async Task SaveStateAsync()
    {
        await _state.WriteStateAsync();
    }

    private async Task EnsureRecoveryReminderAsync()
    {
        await this.RegisterOrUpdateReminder(
            RecoveryReminderName,
            RecoveryReminderDue,
            RecoveryReminderPeriod);
    }

    private async Task UnregisterReminderAsync()
    {
        try
        {
            var reminder = await this.GetReminder(RecoveryReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentLaunchCoordinatorGrain {Key} could not unregister orphan reminder",
                PrimaryKeyString());
        }
    }

    private string PrimaryKeyString() => this.GetPrimaryKeyString();

    private static JsonElement? DeserializeAgentConfig(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonDocument.Parse(json).RootElement.Clone();
}

[GenerateSerializer]
public sealed class AgentLaunchCoordinatorState
{
    [Id(0)] public AgentLaunchCoordinatorPlan? Plan { get; set; }
}

/// <summary>
/// Public envelope the coordinator route forwards. Carries the
/// canonical request snapshot plus the resolved Agent fields the
/// coordinator needs to populate the plan and the metadata the
/// AgentSession will receive.
/// </summary>
[GenerateSerializer]
public sealed record AgentLaunchCoordinatorCommandEnvelope(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string IdempotencyKey,
    [property: Id(2)] string AgentId,
    [property: Id(3)] string AgentName,
    [property: Id(4)] string? AgentInstructions,
    [property: Id(5)] string? AgentConfigJson,
    [property: Id(6)] string? Model,
    [property: Id(7)] string? Variant,
    [property: Id(8)] string? Runtime,
    [property: Id(9)] string Prompt,
    [property: Id(10)] string? WorkspacePath,
    [property: Id(11)] int? IssueNumber,
    [property: Id(12)] int? EpicNumber,
    [property: Id(13)] string? Repository,
    [property: Id(14)] string? Title,
    [property: Id(15)] AgentLaunchCoordinatorRequest Request = null!);

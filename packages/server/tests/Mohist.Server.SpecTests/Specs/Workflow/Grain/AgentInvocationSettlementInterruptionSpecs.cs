using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Interruption and resumption coverage for the Workflow-owned Agent
/// invocation finalizer (issue 559, T-004 / design D7): a failure injected
/// after each durable receipt persistence but before the later effects
/// simulates losing the grain mid-settlement; the finalizer reconcile
/// reminder (<see cref="WorkflowGrain.AgentInvocationSettlementReminderName"/>)
/// resumes from the recorded per-effect flags and completes the remaining
/// effects exactly once — artifacts bound once, variables patched once,
/// task outcome and advancement applied once.
/// </summary>
[Collection("MohistDb")]
public sealed partial class AgentInvocationSettlementInterruptionSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);

    private readonly MohistDbFixture _fixture;

    public AgentInvocationSettlementInterruptionSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    public enum InterruptAfter
    {
        Receipt,
        Artifacts,
        SetVars,
    }

    [Theory]
    [InlineData(InterruptAfter.Receipt)]
    [InlineData(InterruptAfter.Artifacts)]
    [InlineData(InterruptAfter.SetVars)]
    public async Task Interruption_ResumesFromRecordedFlagsAndCompletesRemainingEffectsExactlyOnce(InterruptAfter boundary)
    {
        var workflowRunId = $"wr-agent-settle-resume-{boundary}-{Guid.NewGuid():N}";
        var projectId = $"proj-agent-settle-resume-{boundary}";
        var workerId = $"worker-agent-settle-resume-{boundary}";
        var (grain, task, link) = await StartDelegatedWorkAsync(workflowRunId, projectId, workerId);
        var uploadId = $"upload-resume-{boundary}-{Guid.NewGuid():N}";
        await SeedPendingUploadAsync(workflowRunId, link.WorkId, task.Id, uploadId, "plans/report.md");

        // First delivery: the injected failure interrupts the settlement
        // after the receipt (and any earlier effects) persisted.
        var terminal = TerminalFor(workflowRunId, link, uploadIds: [uploadId]);
        grain.FailNext(boundary);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.SettleAgentInvocationAsync(terminal));

        // The receipt (and the applied flags before the interruption) is
        // durable; the finalizer reconcile reminder was registered.
        var interrupted = await LoadRunAsync(workflowRunId);
        var interruptedTask = Assert.Single(interrupted!.CurrentStage().Tasks);
        var receipt = interruptedTask.AgentInvocationSettlement;
        Assert.NotNull(receipt);
        Assert.Equal(terminal.DeliveryId, receipt.Terminal.DeliveryId);
        if (boundary == InterruptAfter.Receipt)
        {
            Assert.False(receipt.ArtifactsBound);
            Assert.Equal(TaskRunStatus.Running, interruptedTask.Status);
        }
        Assert.True(grain.ReminderEnsures > 0);

        // Time passes; the reconcile reminder tick resumes the settlement
        // from the recorded flags.
        TimeProvider.Advance(TimeSpan.FromSeconds(5));
        await grain.ReceiveReminder(WorkflowGrain.AgentInvocationSettlementReminderName, default);

        var run = (await LoadRunAsync(workflowRunId))!;
        var settled = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, settled.Status);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Equal(
            JSON.DeserializeElement("""{"promise":"done"}""").GetRawText(),
            settled.Output!.Value.GetRawText());
        var settledReceipt = settled.AgentInvocationSettlement;
        Assert.NotNull(settledReceipt);
        Assert.True(settledReceipt.IsSettled);
        Assert.True(settledReceipt.SettledAt >= settledReceipt.ReceivedAt);

        // Artifacts bound and recorded exactly once across the interruption.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>()
                .CreateDbContextAsync();
            var artifact = Assert.Single(await db.WorkflowArtifacts
                .Where(row => row.WorkflowRunId == workflowRunId)
                .ToListAsync());
            Assert.Equal(uploadId, artifact.SourceUploadId);
        }

        // setVars applied exactly once (re-application is value-stable, and
        // no further patch happens after settlement).
        var vars = await LoadRunVarsAsync(workflowRunId);
        Assert.Equal("done", vars.Vars!.Value.GetProperty("result").GetString());

        // The task outcome and advancement happened exactly once.
        var events = await ListRunEventsAsync(workflowRunId);
        Assert.Single(events, e => e.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
        Assert.Single(events, e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowArtifactRecorded);
        Assert.Contains(events, e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunCompleted);

        // The reminder was removed once nothing remains unsettled, and a
        // duplicate delivery is acknowledged as already applied.
        Assert.True(grain.ReminderRemoves > 0);
        Assert.Equal(
            AgentInvocationSettlementAck.AlreadyApplied,
            await grain.SettleAgentInvocationAsync(terminal));
    }

    [Fact]
    public async Task StopDuringInterruptedSettlement_AcknowledgesTheReceiptSettledWithoutFurtherEffects()
    {
        var workflowRunId = $"wr-agent-settle-resume-stop-{Guid.NewGuid():N}";
        var projectId = $"proj-agent-settle-resume-stop";
        var workerId = $"worker-agent-settle-resume-stop";
        var (grain, _, link) = await StartDelegatedWorkAsync(workflowRunId, projectId, workerId);

        grain.FailNext(InterruptAfter.Receipt);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.SettleAgentInvocationAsync(TerminalFor(workflowRunId, link)));

        // The run stops while the settlement is interrupted: the stop settles
        // the task under stop semantics, and the resumed settlement only
        // acknowledges the receipt as settled without applying effects.
        await grain.StopAsync("operator stop");
        TimeProvider.Advance(TimeSpan.FromSeconds(5));
        await grain.ReceiveReminder(WorkflowGrain.AgentInvocationSettlementReminderName, default);

        var run = (await LoadRunAsync(workflowRunId))!;
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.True(task.AgentInvocationSettlement!.IsSettled);
        Assert.DoesNotContain(await ListRunEventsAsync(workflowRunId),
            e => e.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
    }

    private async Task<(InterruptingWorkflowGrain Grain, TaskRun Task, AgentInvocationLink Link)> StartDelegatedWorkAsync(
        string workflowRunId,
        string projectId,
        string workerId)
    {
        await SeedWorkflowTemplateAsync(projectId, new WorkflowDefinition(
        [
            new StageDefinition("build",
            [
                new TaskDefinition(
                    "agent-task",
                    "Agent task",
                    "mohist/agent",
                    With: new Dictionary<string, JsonElement?> { ["prompt"] = JSON.SerializeToElement("run the task") },
                    Expect: new Dictionary<string, JsonElement?>
                    {
                        ["markers"] = JSON.DeserializeElement(
                            """[{"path":"_output","oneOf":["<promise>done</promise>"]}]"""),
                    },
                    Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("plans/report.md")]),
                    SetVars: new Dictionary<string, string> { ["result"] = "output.promise" }),
            ], []),
        ]));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var grain = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await grain.OnActivateAsync(CancellationToken.None);
        await grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));
        await grain.AssignWorkerAsync(workerId);
        var work = await grain.ClaimNextAsync(workerId);
        Assert.NotNull(work);

        var run = await store.LoadAsync(workflowRunId);
        var task = Assert.Single(run!.Stages.SelectMany(stage => stage.Tasks), t => t.Status == TaskRunStatus.Running);
        var link = new AgentInvocationLink(
            $"workflow-agent-invocation-{work!.Id}",
            task.Id,
            work.Id!,
            $"agent-job-{work.Id}",
            $"agent-session-{work.Id}",
            $"workflow-agent-input-{work.Id}",
            $"workflow-agent-turn-{work.Id}");
        Assert.Equal(ReportAck.Accepted, await grain.BindAgentInvocationAsync(link));
        return (grain, task, link);
    }

    private static AgentInvocationTerminal TerminalFor(
        string workflowRunId,
        AgentInvocationLink link,
        string[]? uploadIds = null) => new(
        DeliveryId: $"workflow-terminal:{link.JobId}",
        InvocationId: link.InvocationId,
        ProjectId: "proj-agent-settle-resume",
        WorkflowRunId: workflowRunId,
        TaskRunId: link.TaskRunId,
        WorkId: link.WorkId,
        JobId: link.JobId,
        SessionId: link.SessionId,
        InputId: link.InputId,
        TurnId: link.TurnId,
        Status: AgentInvocationTerminalStatus.Completed,
        Message: "AgentJob completed",
        FailureReason: null,
        FailureCategory: null,
        ExitCode: 0,
        ArtifactUploadIds: uploadIds,
        Expectation: new AgentInvocationExpectation(
            Satisfied: true,
            Matched: "<promise>done</promise>",
            Message: "Workflow completion requirements satisfied"),
        RecordedAt: TimeProvider.GetUtcNow());

    private InterruptingWorkflowGrain CreateGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId)
    {
        var resolver = services.GetRequiredService<WorkflowDefinitionResolver>();
        var identity = GrainTestContext.Create(
            workflowRunId,
            new WorkflowGrainTestProfileCoordinatorFactory(store, resolver));
        return new InterruptingWorkflowGrain(
            identity.Context,
            identity.Runtime,
            store,
            services.GetRequiredService<IDispatchSnapshotStore>(),
            resolver,
            services.GetRequiredService<WorkflowVariableResolver>(),
            services.GetRequiredService<IWorkflowArtifactBindService>(),
            services.GetRequiredService<WorkflowRunVariablesStore>(),
            services.GetRequiredService<WorkflowPromptResolver>(),
            Options.Create(new WorkflowOptions()),
            TimeProvider,
            NullLogger<WorkflowGrain>.Instance);
    }

    private async Task SeedWorkflowTemplateAsync(string projectId, WorkflowDefinition definition)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        const string profileId = "spec/workflow";
        var profile = await db.WorkflowProfileRecords.FindAsync(projectId, profileId);
        if (profile is null)
        {
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = profileId,
                Name = profileId,
                DefinitionSource = WorkflowYamlSerializer.ToYaml(definition),
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
            });
        }
        else
        {
            profile.DefinitionSource = WorkflowYamlSerializer.ToYaml(definition);
            profile.UpdatedAt = TimeProvider.GetUtcNow();
        }

        var projectProfile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (projectProfile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = profileId,
            });
        }
        else
        {
            projectProfile.DefaultWorkflowProfileId = profileId;
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedPendingUploadAsync(
        string workflowRunId,
        string workId,
        string taskRunId,
        string uploadId,
        string path)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            TaskRunId = taskRunId,
            Path = path,
            StoragePath = $"/mohist-tests/artifacts/{uploadId}",
            CreatedAt = TimeProvider.GetUtcNow(),
            ExpiresAt = TimeProvider.GetUtcNow() + TimeSpan.FromHours(1),
        });
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRun?> LoadRunAsync(string workflowRunId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(workflowRunId);
    }

    private async Task<VariableBundle> LoadRunVarsAsync(string workflowRunId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowRunVariablesStore>()
            .GetVariablesAsync(workflowRunId);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> ListRunEventsAsync(string workflowRunId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        return await events.ListAsync(workflowRunId);
    }

    private sealed class InterruptingWorkflowGrain : WorkflowGrain
    {
        private InterruptAfter? _failNext;

        public InterruptingWorkflowGrain(
            Orleans.Runtime.IGrainContext context,
            Orleans.Runtime.IGrainRuntime runtime,
            IWorkflowRunStore runStore,
            IDispatchSnapshotStore dispatchSnapshotStore,
            WorkflowDefinitionResolver definitionResolver,
            WorkflowVariableResolver variableResolver,
            IWorkflowArtifactBindService artifactBindService,
            WorkflowRunVariablesStore runVariablesStore,
            WorkflowPromptResolver promptResolver,
            IOptions<WorkflowOptions> options,
            TimeProvider timeProvider,
            ILogger<WorkflowGrain> log)
            : base(context, runtime, runStore, dispatchSnapshotStore, definitionResolver, variableResolver,
                artifactBindService, runVariablesStore, promptResolver, options, timeProvider, log)
        {
        }

        public int ReminderEnsures { get; private set; }
        public int ReminderRemoves { get; private set; }

        public void FailNext(InterruptAfter boundary) => _failNext = boundary;

        protected override Task EnsureAgentInvocationSettlementReminderAsync()
        {
            ReminderEnsures++;
            return Task.CompletedTask;
        }

        protected override Task RemoveAgentInvocationSettlementReminderAsync()
        {
            ReminderRemoves++;
            return Task.CompletedTask;
        }

        protected override Task OnAgentInvocationReceiptPersistedAsync(AgentInvocationSettlement receipt) =>
            FailIf(InterruptAfter.Receipt);

        protected override Task OnAgentInvocationArtifactsBoundAsync(AgentInvocationSettlement receipt) =>
            FailIf(InterruptAfter.Artifacts);

        protected override Task OnAgentInvocationSetVarsAppliedAsync(AgentInvocationSettlement receipt) =>
            FailIf(InterruptAfter.SetVars);

        private Task FailIf(InterruptAfter boundary)
        {
            if (_failNext == boundary)
            {
                _failNext = null;
                throw new InvalidOperationException($"simulated settlement interruption after {boundary}");
            }

            return Task.CompletedTask;
        }
    }
}

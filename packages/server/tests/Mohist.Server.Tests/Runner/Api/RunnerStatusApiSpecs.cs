using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
public class RunnerStatusApiSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    private readonly MohistIntegrationFixture _fixture;

    public RunnerStatusApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task AssignActiveWorkForTestAsync(
        string runnerId,
        string workflowId,
        string workId,
        string workType,
        string stage,
        string title,
        string projectId = "test-project")
    {
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        var definition = new WorkflowDefinition(
        [
            new StageDefinition(stage,
                [new TaskDefinition(workId.Contains('.', StringComparison.Ordinal) ? workId[..workId.LastIndexOf('.')] : workId, title, "spec/task")],
                [])
        ]);
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: FixedNow,
             ProjectId: projectId),
            VerificationCommand: "true"));
        await workflow.AssignWorkerAsync(runnerId);

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.NotNull(await runner.PollAsync(_fixture.Services));
    }

    private async Task SeedWorkflowTemplateAsync(string workflowId, WorkflowDefinition definition, string projectId = "test-project")
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        const string templateId = "spec/workflow";
        var templateJson = WorkflowGrainTestHelpers.SerializeProfile(definition);
        var template = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (template is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = templateJson,
            });
        }
        else
        {
            template.Template = templateJson;
            template.UpdatedAt = FixedNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync("test-project");
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "test-project",
                DefaultTemplateId = templateId,
            });
        }
        else
        {
            profile.DefaultTemplateId = templateId;
            profile.UpdatedAt = FixedNow;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetRunners_GlobalInventoryIncludesDurableDefinitions()
    {
        var projectId = await CreateProjectIdAsync($"proj-empty-{Guid.NewGuid():N}");

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var existingIds = await registry.ListRunnerIdsAsync();
        foreach (var id in existingIds)
            await registry.UnregisterAsync(id);

        var response = await _fixture.Client.GetAsync("/api/runners");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("ready", data.GetProperty("inventory").GetProperty("state").GetString());
        Assert.Equal(global::System.Text.Json.JsonValueKind.Array, data.GetProperty("runners").ValueKind);
    }

    [Fact]
    public async Task GetRunners_ProjectScopedRoute_IsRemoved()
    {
        var response = await _fixture.Client.GetAsync("/api/projects/does-not-exist/runners");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRunners_RunnerFields_UseRunnerTerminology()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-terms-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "terms-host",
            projectId,
            CoderModels: new[] { "openai/gpt-4" },
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);

        try
        {
            var response = await _fixture.Client.GetAsync("/api/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().Single(r => r.GetProperty("identity").GetProperty("id").GetString() == runnerId);

            Assert.Equal("ready", payload.GetProperty("data").GetProperty("inventory").GetProperty("state").GetString());
            Assert.Equal("terms-host", runner.GetProperty("identity").GetProperty("hostname").GetString());
            Assert.Equal("online", runner.GetProperty("presence").GetProperty("state").GetString());
            Assert.Equal("disconnected", runner.GetProperty("control").GetProperty("state").GetString());
            Assert.Contains("capabilities", runner.ToString());
            Assert.Contains("runtimes", runner.ToString());
            Assert.Contains("activeWorks", runner.ToString());
            Assert.DoesNotContain("coderModels", runner.ToString());
            Assert.DoesNotContain("idle", runner.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task GetRunner_BusyRunner_Returns200WithFullDetail()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-detail-{Guid.NewGuid():N}";
        var hash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "detail-host",
            projectId,
            CoderModels: new[] { "openai/gpt-4" },
            BuildGitHash: hash,
            Component: "mohist-runner",
            SourceRevision: hash,
            ReleaseId: "release-detail",
            Generation: 7,
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflowId = $"wf-detail-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "work-detail-1", "task", "build", "Detail Task", projectId);

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/runners/{runnerId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var detail = payload.GetProperty("data").GetProperty("runner");

            Assert.Equal(runnerId, detail.GetProperty("identity").GetProperty("id").GetString());
            Assert.Equal("external", detail.GetProperty("identity").GetProperty("kind").GetString());
            Assert.Equal("detail-host", detail.GetProperty("identity").GetProperty("hostname").GetString());
            Assert.Equal("mohist-runner", detail.GetProperty("identity").GetProperty("component").GetString());
            Assert.Equal(hash, detail.GetProperty("identity").GetProperty("sourceRevision").GetString());
            Assert.Equal("release-detail", detail.GetProperty("identity").GetProperty("releaseId").GetString());
            Assert.Equal(7, detail.GetProperty("identity").GetProperty("generation").GetInt64());
            Assert.Equal("online", detail.GetProperty("presence").GetProperty("state").GetString());
            Assert.Equal(1, detail.GetProperty("capacity").GetProperty("used").GetInt32());
            Assert.Equal(1, detail.GetProperty("capacity").GetProperty("total").GetInt32());
            Assert.DoesNotContain("buildGitHash", detail.ToString());
            Assert.DoesNotContain("coderModels", detail.ToString());

            var activeWorks = detail.GetProperty("activeWorks");
            Assert.Equal(global::System.Text.Json.JsonValueKind.Array, activeWorks.ValueKind);
            var first = activeWorks.EnumerateArray().Single();
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("workId").GetString()));
            Assert.Equal("workflow", first.GetProperty("ownerKind").GetString());
            Assert.Equal(workflowId, first.GetProperty("ownerId").GetString());
            Assert.Equal("task", first.GetProperty("workType").GetString());
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("stage").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("title").GetString()));

        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task GetRunner_StatusReadDoesNotSettleDormantPendingCloseout()
    {
        await ResetRunnerReadModelsAsync();
        var projectId = await CreateProjectIdAsync($"proj-readonly-{Guid.NewGuid():N}");
        var runnerId = $"runner-readonly-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "readonly-host",
            projectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create(),
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);
        await runner.UpdateAsync(2);
        await CreateActiveCredentialAsync(runnerId);

        var workflowId = $"wf-readonly-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "work-readonly-1", "task", "build", "Read-only status", projectId);
        var jobId = $"job-readonly-{Guid.NewGuid():N}";
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "Keep this AgentJob running",
            WorkspacePath: "/tmp/runner-status-readonly",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-test",
            PinnedRunnerId: runnerId));
        var dispatches = await runner.PollAllAsync(_fixture.Services);
        Assert.Single(dispatches, dispatch => dispatch.WorkflowRunId == workflowId);
        Assert.Single(dispatches, dispatch => dispatch.AgentJobId == jobId);

        try
        {
            Assert.Equal("Running", await _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId).GetRunStatusAsync());
            Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
            await TestLifecycle.DeactivateAndWait(runner, _fixture.Grains);
            var storage = _fixture.Services.GetRequiredService<IGrainStorage>();
            var stored = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), stored);
            stored.State.ClosingProcessGeneration = TestRunnerGenerationExtensions.ProcessGeneration;
            stored.State.PresenceLeaseExpiresAt = null;
            await storage.WriteStateAsync("runner", runner.GetGrainId(), stored);

            using var response = await _fixture.Client.GetAsync($"/api/runners/{runnerId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Running", await _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId).GetRunStatusAsync());
            Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task AgentStatus_ActiveAgentJobOccupiesLastRunnerSlot()
    {
        await ResetRunnerReadModelsAsync();
        var projectId = await CreateProjectIdAsync($"proj-agent-capacity-{Guid.NewGuid():N}");
        var runnerId = $"runner-agent-capacity-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-capacity-host",
            projectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create(),
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);
        await CreateActiveCredentialAsync(runnerId);
        var jobId = $"job-capacity-{Guid.NewGuid():N}";
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.SubmitAsync(new AgentJobInput(
            "Use the last Runner slot",
            WorkspacePath: "/tmp/runner-status-capacity",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-test",
            PinnedRunnerId: runnerId));

        try
        {
            var dispatch = Assert.Single(await runner.PollAllAsync(_fixture.Services));
            Assert.Equal(jobId, dispatch.AgentJobId);
            var detailResponse = await _fixture.Client.GetAsync($"/api/runners/{runnerId}");
            var detailPayload = await detailResponse.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var detail = detailPayload.GetProperty("data").GetProperty("runner");
            Assert.Equal(1, detail.GetProperty("capacity").GetProperty("used").GetInt32());
            Assert.Contains("capacity-full", detail.GetProperty("admission").GetProperty("reasonCodes").EnumerateArray().Select(item => item.GetString()));
            var activeWork = Assert.Single(detail.GetProperty("activeWorks").EnumerateArray());
            Assert.Equal("agent-job", activeWork.GetProperty("ownerKind").GetString());
            Assert.Equal(jobId, activeWork.GetProperty("ownerId").GetString());

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{projectId}/agent/status");
            Assert.Equal(1, status.Capacity.Active);
            Assert.Equal(1, status.Capacity.Max);
            Assert.False(status.RunnerAvailable);
            Assert.Equal("Runner capacity is full.", status.RunnerMessage);
            var statusRunner = Assert.Single(status.Runners, item => item.Id == runnerId);
            Assert.Equal(1, statusRunner.Active);
            Assert.Equal(1, statusRunner.Max);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task AgentAvailability_OnlineAdmissionBlockerAndNegativeRuntimeWitnessStayVisible()
    {
        await ResetRunnerReadModelsAsync();
        var projectId = await CreateProjectIdAsync($"proj-agent-blocked-{Guid.NewGuid():N}");
        var runnerId = $"runner-agent-blocked-{Guid.NewGuid():N}";
        var connectionTracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var connectionGeneration = connectionTracker.Register(runnerId, "blocked-connection");
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "blocked-host",
            projectId,
            ConnectionGeneration: connectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);
        await CreateActiveCredentialAsync(runnerId);
        await runner.ObserveDispatchObservationAsync(
            TestRunnerGenerationExtensions.ProcessGeneration,
            new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: false,
                AdmissionReasonCodes: [RunnerAdmissionReasonCodes.ProviderPolicyInvalid],
                RuntimeReadiness: [new RuntimeReadinessWitness("pi", Ready: false, Generation: 7)]));
        var agentResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = "blocked-runner-agent",
                description = "blocked runner coverage",
                instructions = "wait for admission",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = Array.Empty<string>(),
                maxConcurrentRuns = 1,
            });
        agentResponse.EnsureSuccessStatusCode();

        try
        {
            var detailResponse = await _fixture.Client.GetAsync($"/api/runners/{runnerId}");
            var detailPayload = await detailResponse.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runtime = Assert.Single(detailPayload.GetProperty("data").GetProperty("runner").GetProperty("runtimes").EnumerateArray());
            Assert.Equal("not-ready", runtime.GetProperty("readiness").GetProperty("state").GetString());
            Assert.Equal(7, runtime.GetProperty("readiness").GetProperty("generation").GetInt64());
            Assert.Equal("runtime-reported-not-ready", runtime.GetProperty("readiness").GetProperty("reasonCode").GetString());

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{projectId}/agent/status");
            Assert.False(status.RunnerAvailable);
            Assert.Contains(RunnerAdmissionReasonCodes.ProviderPolicyInvalid, status.RunnerMessage);

            var availabilityResponse = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents/availability");
            availabilityResponse.EnsureSuccessStatusCode();
            var availabilityPayload = await availabilityResponse.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var availability = Assert.Single(availabilityPayload.GetProperty("data").EnumerateArray());
            Assert.False(availability.GetProperty("canStartNow").GetBoolean());
            Assert.Equal(RunnerAdmissionReasonCodes.ProviderPolicyInvalid, availability.GetProperty("waitingReason").GetString());
        }
        finally
        {
            connectionTracker.Unregister(runnerId);
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task AgentAvailability_AdmissionReadyButRequiredRuntimeNotReady_IsNotAvailable()
    {
        await ResetRunnerReadModelsAsync();
        var projectId = await CreateProjectIdAsync($"proj-agent-runtime-{Guid.NewGuid():N}");
        var runnerId = $"runner-agent-runtime-{Guid.NewGuid():N}";
        var connectionTracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var connectionGeneration = connectionTracker.Register(runnerId, "runtime-connection");
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "runtime-host",
            projectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create(),
            ConnectionGeneration: connectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);
        await CreateActiveCredentialAsync(runnerId);
        await runner.ObserveDispatchObservationAsync(
            TestRunnerGenerationExtensions.ProcessGeneration,
            new RunnerDispatchObservation(
                connectionGeneration,
                AdmissionReady: true,
                AdmissionReasonCodes: [],
                RuntimeReadiness: [new RuntimeReadinessWitness("pi", Ready: false, Generation: 7)]));
        var agentResponse = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = "runtime-blocked-agent",
                description = "runtime readiness coverage",
                instructions = "wait for runtime",
                agentConfig = new { runtime = "pi", model = "openai/gpt-5.6" },
                skills = Array.Empty<string>(),
                maxConcurrentRuns = 1,
            });
        agentResponse.EnsureSuccessStatusCode();
        var agentPayload = await agentResponse.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        var agentId = agentPayload.GetProperty("data").GetProperty("id").GetString()!;

        try
        {
            // Admission is ready and a slot is free; only the required Runtime
            // is unavailable, so availability must still refuse to advertise work.
            var detailResponse = await _fixture.Client.GetAsync($"/api/runners/{runnerId}");
            var detailPayload = await detailResponse.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var detail = detailPayload.GetProperty("data").GetProperty("runner");
            Assert.Equal("ready", detail.GetProperty("admission").GetProperty("state").GetString());
            Assert.Equal(0, detail.GetProperty("capacity").GetProperty("used").GetInt32());
            Assert.Equal(1, detail.GetProperty("capacity").GetProperty("total").GetInt32());

            var statusResponse = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents/{agentId}/status");
            statusResponse.EnsureSuccessStatusCode();
            var statusPayload = await statusResponse.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var availability = statusPayload.GetProperty("data").GetProperty("availability");
            Assert.False(availability.GetProperty("canStartNow").GetBoolean());
            Assert.Equal("runtime-not-ready", availability.GetProperty("waitingReason").GetString());

            var listResponse = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents/availability");
            listResponse.EnsureSuccessStatusCode();
            var listPayload = await listResponse.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var entry = Assert.Single(listPayload.GetProperty("data").EnumerateArray());
            Assert.False(entry.GetProperty("canStartNow").GetBoolean());
            Assert.Equal("runtime-not-ready", entry.GetProperty("waitingReason").GetString());
        }
        finally
        {
            connectionTracker.Unregister(runnerId);
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task GetRunner_ColdObservationStore_KeepsDurablePresenceIdentityAndDrain()
    {
        await ResetRunnerReadModelsAsync();
        var projectId = await CreateProjectIdAsync($"proj-durable-{Guid.NewGuid():N}");
        var runnerId = $"runner-durable-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "durable-host",
            projectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create(),
            Component: "mohist-runner",
            SourceRevision: "durable-source",
            ReleaseId: "durable-release",
            Generation: 11,
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);
        await runner.UpdateAsync(2);
        await CreateActiveCredentialAsync(runnerId);

        var workflowId = $"wf-durable-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "work-durable-1", "task", "build", "Durable status", projectId);
        var fenceId = Guid.NewGuid().ToString("N");
        Assert.NotNull(await runner.BeginUpdateInterruptAsync(fenceId));

        try
        {
            await TestLifecycle.DeactivateAndWait(runner, _fixture.Grains);
            var storage = _fixture.Services.GetRequiredService<IGrainStorage>();
            var stored = new GrainState<RunnerState>();
            await storage.ReadStateAsync("runner", runner.GetGrainId(), stored);
            var durablePresence = stored.State.LastPresenceAt;
            Assert.NotNull(durablePresence);
            Assert.Equal(fenceId, stored.State.UpdateInterruptFence?.PendingId);
            stored.State.ClosingProcessGeneration = TestRunnerGenerationExtensions.ProcessGeneration;
            await storage.WriteStateAsync("runner", runner.GetGrainId(), stored);

            // A Server restart empties the process-local lifecycle projection;
            // status must rebuild durable facts without activating the grain.
            _fixture.Services.GetRequiredService<RunnerStatusObservationStore>().Clear();

            using var response = await _fixture.Client.GetAsync($"/api/runners/{runnerId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var detail = payload.GetProperty("data").GetProperty("runner");

            Assert.Equal("durable-host", detail.GetProperty("identity").GetProperty("hostname").GetString());
            Assert.Equal("mohist-runner", detail.GetProperty("identity").GetProperty("component").GetString());
            Assert.Equal("durable-source", detail.GetProperty("identity").GetProperty("sourceRevision").GetString());
            Assert.Equal("durable-release", detail.GetProperty("identity").GetProperty("releaseId").GetString());
            Assert.Equal(11, detail.GetProperty("identity").GetProperty("generation").GetInt64());
            Assert.Equal(
                durablePresence!.Value.ToUnixTimeMilliseconds(),
                detail.GetProperty("presence").GetProperty("lastObservedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
            var drain = detail.GetProperty("drain");
            Assert.True(drain.GetProperty("active").GetBoolean());
            Assert.Equal(fenceId, drain.GetProperty("updateInterruptId").GetString());

            // The status read stayed diagnostic: the dormant closeout is still owed.
            Assert.Equal("Running", await _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId).GetRunStatusAsync());
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task GetRunner_UnknownRunner_Returns404WithRunnerNotFoundReason()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var unknownRunnerId = $"runner-unknown-{Guid.NewGuid():N}";

        var response = await _fixture.Client.GetAsync($"/api/runners/{unknownRunnerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("runner_not_found", payload.GetProperty("code").GetString());
        Assert.Contains(unknownRunnerId, payload.GetProperty("error").GetString()!);
    }

    private async Task ResetRunnerReadModelsAsync()
    {
        _fixture.Services.GetRequiredService<RunnerStatusObservationStore>().Clear();
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var runnerId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(runnerId);
    }

    private async Task CreateActiveCredentialAsync(string runnerId)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CredentialStore>().CreateRunnerCredentialAsync("status-test", runnerId);
    }

    private async Task<string> CreateProjectIdAsync(string name)
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            name,
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return projectId;
    }

    private sealed record AgentStatusDto(bool RunnerAvailable, string? RunnerMessage, AgentCapacityDto Capacity, RunnerDto[] Runners);
    private sealed record AgentCapacityDto(int Active, int Max);
    private sealed record RunnerDto(string Id, string Kind, int Active, int Max);
}

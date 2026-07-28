using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("PlatformIntegration")]
public class AgentActivityObservabilitySpecs : AgentSessionTestSupport
{
    public AgentActivityObservabilitySpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task AgentActivity_ExposesObservabilityFields()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("activity-observability");
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await _client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new
                {
                    type = "usage.updated",
                    payload = new
                    {
                        inputTokens = 100,
                        outputTokens = 50,
                        totalTokens = 150,
                        cachedReadTokens = 10,
                        thoughtTokens = 5,
                        costAmount = 0.01,
                        costCurrency = "USD",
                        contextWindowSize = 200000,
                        contextWindowUsed = 150
                    }
                },
                new
                {
                    type = "model.resolved",
                    payload = new { resolvedModel = "anthropic/claude-sonnet-4", source = "newSession" }
                },
                new
                {
                    type = "tool_call.started",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read file" }
                },
                new
                {
                    type = "tool_call.updated",
                    payload = new { toolCallId = "tool-1", kind = "read", status = "failed", title = "Read file" }
                },
                new
                {
                    type = "session.activity",
                    payload = new { activity = "idle", status = "failed", failureReason = "probe timed out", failureCategory = "probe_timeout", exitCode = 1, operationId = "op-observe" }
                }
            }
        });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 4, persistence);

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.EventSummary);
        Assert.NotNull(card.Usage);
        Assert.Equal("anthropic/claude-sonnet-4", card.EventSummary!.ResolvedModel);
        Assert.Equal(100, card.Usage!.InputTokens);
        Assert.Equal(50, card.Usage.OutputTokens);
        Assert.Equal(150, card.Usage.TotalTokens);
        Assert.Equal(10, card.Usage.CachedReadTokens);
        Assert.Equal(5, card.Usage.ThoughtTokens);
        Assert.Equal(0.01, card.Usage.CostAmount);
        Assert.Equal("USD", card.Usage.CostCurrency);
        Assert.Equal(150, card.Usage.ContextWindowUsed);
        Assert.Equal(200000, card.Usage.ContextWindowSize);
        Assert.Equal("probe_timeout", card.EventSummary.FailureCategory);
        Assert.Equal(1, card.EventSummary.ToolCallCount);
        Assert.Equal(1, card.EventSummary.ToolErrorCount);
    }

    [Fact]
    public async Task AgentActivity_WhenRunnerActiveWorksExceedVisibleSessions_SlotsReflectRunner()
    {
        // Divergence proof for issue-300/T-002: the runner grain carries more
        // active workflow works than there are visible AgentSessions, so
        // /agent/activity.summary.slots.active must follow the runner active-works
        // count rather than be clamped to the visible AgentSession count.
        var projectName = $"activity-divergence-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);

        var runnerId = $"activity-divergence-{Guid.NewGuid():N}";
        try
        {
            await _client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
            await _client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 4 });

            var workflowA = $"wf-activity-div-a-{Guid.NewGuid():N}";
            var workflowB = $"wf-activity-div-b-{Guid.NewGuid():N}";
            var workflowProjectId = $"wf-activity-div-project-{Guid.NewGuid():N}";
            await SeedActivityDivergenceTemplateAsync(workflowProjectId);

            var workflowAGrain = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowA);
            var workflowBGrain = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowB);
            var startInput = new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                 ProjectId: workflowProjectId));
            await workflowAGrain.StartAsync(startInput);
            await workflowBGrain.StartAsync(startInput);
            await workflowAGrain.AssignWorkerAsync(runnerId);
            await workflowBGrain.AssignWorkerAsync(runnerId);

            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var first = await runner.PollAsync(_fixture.Services);
            Assert.NotNull(first);
            var second = await runner.PollAsync(_fixture.Services);
            Assert.NotNull(second);

            var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");

            // summary.slots.active reflects the runner active-works count (2
            // distinct workflow owners), NOT the visible AgentSession count (no
            // AgentSessions were persisted in this scenario, so 0). max reflects
            // the persisted runner slots (4).
            Assert.Equal(2, activity.Summary.Slots.Active);
            Assert.Equal(4, activity.Summary.Slots.Max);
            // summary.active continues to reflect the visible AgentSession count;
            // it does NOT participate in capacity derivation.
            Assert.Equal(0, activity.Summary.Active);
        }
        finally
        {
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task SeedActivityDivergenceTemplateAsync(string projectId)
    {
        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var templateId = "spec/workflow";
        var templateJson = WorkflowGrainTestHelpers.SerializeProfile(new WorkflowDefinition(
            [
                new StageDefinition("build",
                    [new TaskDefinition("task-1", "Task 1", "spec/task")],
                    [])
            ]));

        var existing = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (existing is null)
        {
            db.ProjectWorkflowTemplates.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = templateJson,
            });
        }
        else
        {
            existing.Template = templateJson;
            existing.UpdatedAt = TestTime.UtcNow;
        }
        if (await db.ProjectWorkflowProfiles.FindAsync(projectId) is null)
        {
            db.ProjectWorkflowProfiles.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = templateId,
            });
        }
        if (await db.WorkflowProfileRecords.FindAsync(projectId, templateId) is null)
        {
            db.WorkflowProfileRecords.Add(new Mohist.Server.Infrastructure.Data.Workflow.WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = templateId,
                Name = templateId,
                DefinitionSource = WorkflowYamlSerializer.ToYaml(new WorkflowDefinition(
                    [new StageDefinition("build", [new TaskDefinition("task-1", "Task 1", "spec/task")], [])])),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AgentActivity_WithResolvableWorkflowStage_ReturnsTaskProgress()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("activity-task-progress");

        var runState = JsonSerializer.Serialize(new
        {
            Id = work.WorkflowRunId,
            Metadata = new { CreatedAt = _fixture.TimeProvider.GetUtcNow(), Name = "test" },
            Status = "Running",
            CurrentStageId = work.Stage ?? "Build",
            Stages = new[]
            {
                new
                {
                    Id = work.Stage ?? "Build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Running",
                    Tasks = new[]
                    {
                        new { Id = "task-1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1", Status = "Completed", Uses = "mohist/opencode" },
                        new { Id = "task-2", DefinitionId = "task-2", Attempt = 1, Title = "Task 2", Status = "Running", Uses = "mohist/opencode" },
                        new { Id = "task-3", DefinitionId = "task-3", Attempt = 1, Title = "Task 3", Status = "Pending", Uses = "mohist/opencode" }
                    },
                    Checks = Array.Empty<object>()
                }
            }
        });
        var issueState = JsonSerializer.Serialize(new
        {
            ProjectId = project.Id,
            Number = issue.Number,
            WorkflowRunId = work.WorkflowRunId,
            Title = issue.Title
        });

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
                work.WorkflowRunId, runState);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO Issues (ProjectId, Number, State) VALUES ({0}, {1}, {2})",
                project.Id, issue.Number, issueState);
        }

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.TaskProgress);
        Assert.Equal(1, card.TaskProgress!.Completed);
        Assert.Equal(3, card.TaskProgress.Total);
    }

    [Fact]
    public async Task AgentActivity_WhenSessionStageIsStale_UsesWorkflowCurrentStageTaskProgress()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("activity-task-progress-stale-stage");

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE AgentSessions SET State = json_set(State, '$.metadata.labels."mohist.io/stage"', {0}) WHERE Id = {1}""",
                "Plan", session.Id);
        }

        var runState = JsonSerializer.Serialize(new
        {
            Id = work.WorkflowRunId,
            Metadata = new { CreatedAt = _fixture.TimeProvider.GetUtcNow(), Name = "test" },
            Status = "Running",
            CurrentStageId = "Build",
            Stages = new[]
            {
                new
                {
                    Id = "Plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "plan-1", DefinitionId = "plan-1", Attempt = 1, Title = "Plan 1", Status = "Completed", Uses = "mohist/opencode" }
                    },
                    Checks = Array.Empty<object>()
                },
                new
                {
                    Id = "Build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Running",
                    Tasks = new[]
                    {
                        new { Id = "task-1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1", Status = "Completed", Uses = "mohist/opencode" },
                        new { Id = "task-2", DefinitionId = "task-2", Attempt = 1, Title = "Task 2", Status = "Completed", Uses = "mohist/opencode" },
                        new { Id = "task-3", DefinitionId = "task-3", Attempt = 1, Title = "Task 3", Status = "Running", Uses = "mohist/opencode" },
                        new { Id = "task-4", DefinitionId = "task-4", Attempt = 1, Title = "Task 4", Status = "Pending", Uses = "mohist/opencode" }
                    },
                    Checks = Array.Empty<object>()
                }
            }
        });
        var issueState = JsonSerializer.Serialize(new
        {
            ProjectId = project.Id,
            Number = issue.Number,
            WorkflowRunId = work.WorkflowRunId,
            Title = issue.Title
        });

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
                work.WorkflowRunId, runState);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO Issues (ProjectId, Number, State) VALUES ({0}, {1}, {2})",
                project.Id, issue.Number, issueState);
        }

        var activity = await _client.GetDataAsync<ActivityDto>($"/api/projects/{project.Id}/agent/activity");
        var card = Assert.Single(activity.Sessions, s => s.SessionId == session.Id);

        Assert.NotNull(card.TaskProgress);
        Assert.Equal(2, card.TaskProgress!.Completed);
        Assert.Equal(4, card.TaskProgress.Total);
    }

}

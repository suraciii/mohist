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
using Mohist.Server.TestSupport;
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

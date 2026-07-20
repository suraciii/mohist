using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class ActivityWaitingApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public ActivityWaitingApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetActivity_WhenIssuePausedOnApprovalGate_AppearsInWaitingArray()
    {
        var project = await CreateProjectAsync();
        var waitingIssue = await InsertIssueWithApprovalGateAsync(
            project.Id,
            number: 1,
            title: "Awaiting product review",
            approvalRequestedAt: TestTime.UtcNow.AddMinutes(-3));

        var response = await _client.GetDataAsync<ActivityResponseDto>(
            $"/api/projects/{project.Id}/agent/activity");

        var entry = Assert.Single(response.Waiting, w => w.IssueNumber == waitingIssue.Number);
        Assert.Equal("Awaiting product review", entry.IssueTitle);
        Assert.Equal("plan", entry.Stage);
        Assert.Equal("Needs Approval", entry.Label);
        Assert.Equal(1, response.Summary.Waiting);
    }

    [Fact]
    public async Task GetActivity_WhenNoIssuePausedOnApprovalGate_HasEmptyWaitingArray()
    {
        var project = await CreateProjectAsync();
        await InsertBacklogIssueAsync(project.Id, number: 1, title: "Backlog only");

        var response = await _client.GetDataAsync<ActivityResponseDto>(
            $"/api/projects/{project.Id}/agent/activity");

        Assert.Empty(response.Waiting);
        Assert.Equal(0, response.Summary.Waiting);
    }

    [Fact]
    public async Task GetActivity_OnlyIncludesInProgressIssues_NotBacklogOrDone()
    {
        var project = await CreateProjectAsync();
        await InsertBacklogIssueAsync(project.Id, number: 1, title: "Backlog");
        await InsertDoneIssueAsync(project.Id, number: 2, title: "Done");
        await InsertApprovalGateIssueAsync(
            project.Id,
            number: 3,
            title: "Gated",
            approvalRequestedAt: TestTime.UtcNow);

        var response = await _client.GetDataAsync<ActivityResponseDto>(
            $"/api/projects/{project.Id}/agent/activity");

        var entry = Assert.Single(response.Waiting);
        Assert.Equal(3, entry.IssueNumber);
        Assert.Equal("Gated", entry.IssueTitle);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"waiting-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", name);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return project;
    }

    private async Task<ProjectIssueDto> InsertBacklogIssueAsync(string projectId, int number, string title)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.Backlog,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
        return new ProjectIssueDto(issue.Number);
    }

    private async Task<ProjectIssueDto> InsertDoneIssueAsync(string projectId, int number, string title)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.Done,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
        return new ProjectIssueDto(issue.Number);
    }

    private async Task<ProjectIssueDto> InsertIssueWithApprovalGateAsync(
        string projectId,
        int number,
        string title,
        DateTimeOffset approvalRequestedAt)
    {
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.InProgress,
        };
        issue.StartWorkflow(workflowRunId);

        var runState = JsonSerializer.Serialize(new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = TestTime.UtcNow, Name = "test" },
            Status = "AwaitingApproval",
            CurrentStageId = "plan",
            Stages = new[]
            {
                new
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "AwaitingApproval",
                    Tasks = new[]
                    {
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/opencode" },
                    },
                    Checks = new[]
                    {
                        new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = (string?)null,
                        RequestedAt = approvalRequestedAt.ToString("O"),
                        RespondedAt = (string?)null,
                    },
                }
            }
        });

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            db.Issues.Add(new IssueRow
            {
                State = IssueStore.Serialize(issue),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
                workflowRunId, runState);
        }

        return new ProjectIssueDto(issue.Number);
    }

    private async Task<ProjectIssueDto> InsertApprovalGateIssueAsync(
        string projectId,
        int number,
        string title,
        DateTimeOffset approvalRequestedAt) =>
        await InsertIssueWithApprovalGateAsync(projectId, number, title, approvalRequestedAt);

    private sealed record ProjectDto(string Id, string Name);
    private sealed record ProjectIssueDto(int Number);
    private sealed record ActivityWaitingEntryDto(int IssueNumber, string IssueTitle, string? Stage, string Label, string? RequestedAt, string? Preview);
    private sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed);
    private sealed record ActivityResponseDto(ActivitySummaryDto Summary, object[] Sessions, ActivityWaitingEntryDto[] Waiting);
}

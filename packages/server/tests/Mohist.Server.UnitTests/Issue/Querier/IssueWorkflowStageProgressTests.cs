using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Querier;

[Collection("MohistDb")]
public class IssueWorkflowStageProgressTests
{
    private readonly MohistDbFixture _fixture;

    public IssueWorkflowStageProgressTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_WithActiveWorkflowStageAndUserTasks_IncludesWorkflowStageProgress()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-progress-1", Name = "Progress Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_prog_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Progress issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            WorkflowRunId = "wf-run-1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.InProgress,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = "wf-run-1",
            State = """
            {
              "id": "wf-run-1",
              "status": "Running",
              "currentStageId": "build",
              "metadata": {
                "name": "test-run",
                "createdAt": "2024-01-01T00:00:00Z"
              },
              "stages": [
                {
                  "id": "build",
                  "status": "Running",
                  "attempt": 1,
                  "requiresApproval": false,
                  "initialized": true,
                  "tasks": [
                    {
                      "id": "build-task-1.1",
                      "definitionId": "build-task-1",
                      "attempt": 1,
                      "title": "Build the thing",
                      "status": "Completed",
                      "classification": "UserFacing"
                    },
                    {
                      "id": "build-task-2.1",
                      "definitionId": "build-task-2",
                      "attempt": 1,
                      "title": "Test the thing",
                      "status": "Running",
                      "classification": "UserFacing"
                    },
                    {
                      "id": "build-task-3.1",
                      "definitionId": "build-task-3",
                      "attempt": 1,
                      "title": "Package",
                      "status": "Pending",
                      "classification": "UserFacing"
                    }
                  ],
                  "checks": []
                }
              ]
            }
            """,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.NotNull(item.WorkflowStageProgress);
        Assert.Equal("build", item.WorkflowStageProgress.Stage);
        Assert.Equal(3, item.WorkflowStageProgress.Total);
        Assert.Equal(1, item.WorkflowStageProgress.Completed);
        Assert.Equal(1, item.WorkflowStageProgress.Running);
        Assert.Equal(0, item.WorkflowStageProgress.Failed);
        Assert.Equal("Test the thing", item.WorkflowStageProgress.CurrentTaskTitle);
    }

    [Fact]
    public async Task ListAsync_WithOrchestrationTasks_ExcludesThemFromProgressCount()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-orch-1", Name = "Orch Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_orch_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Orchestration issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            WorkflowRunId = "wf-run-orch-1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.InProgress,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = "wf-run-orch-1",
            State = """
            {
              "id": "wf-run-orch-1",
              "status": "Running",
              "currentStageId": "build",
              "metadata": {
                "name": "test-run",
                "createdAt": "2024-01-01T00:00:00Z"
              },
              "stages": [
                {
                  "id": "build",
                  "status": "Running",
                  "attempt": 1,
                  "requiresApproval": false,
                  "initialized": true,
                  "tasks": [
                    {
                      "id": "build-user-1.1",
                      "definitionId": "build-user-1",
                      "attempt": 1,
                      "title": "User task",
                      "status": "Completed",
                      "classification": "UserFacing"
                    },
                    {
                      "id": "orch-internal-1.1",
                      "definitionId": "orch-internal-1",
                      "attempt": 1,
                      "title": "Internal orch task",
                      "status": "Running",
                      "classification": "Orchestration"
                    }
                  ],
                  "checks": []
                }
              ]
            }
            """,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.NotNull(item.WorkflowStageProgress);
        Assert.Equal(1, item.WorkflowStageProgress.Total);
        Assert.Equal(1, item.WorkflowStageProgress.Completed);
        Assert.Equal(0, item.WorkflowStageProgress.Running);
    }

    [Fact]
    public async Task ListAsync_WithFailedUserTask_NotCountedAsCompleted()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-fail-1", Name = "Fail Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_fail_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Failed task issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            WorkflowRunId = "wf-run-fail-1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.InProgress,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = "wf-run-fail-1",
            State = """
            {
              "id": "wf-run-fail-1",
              "status": "Running",
              "currentStageId": "build",
              "metadata": {
                "name": "test-run",
                "createdAt": "2024-01-01T00:00:00Z"
              },
              "stages": [
                {
                  "id": "build",
                  "status": "Running",
                  "attempt": 1,
                  "requiresApproval": false,
                  "initialized": true,
                  "tasks": [
                    {
                      "id": "build-task-1.1",
                      "definitionId": "build-task-1",
                      "attempt": 1,
                      "title": "Failed task",
                      "status": "Failed",
                      "classification": "UserFacing"
                    },
                    {
                      "id": "build-task-2.1",
                      "definitionId": "build-task-2",
                      "attempt": 1,
                      "title": "Next task",
                      "status": "Pending",
                      "classification": "UserFacing"
                    }
                  ],
                  "checks": []
                }
              ]
            }
            """,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.NotNull(item.WorkflowStageProgress);
        Assert.Equal(2, item.WorkflowStageProgress.Total);
        Assert.Equal(0, item.WorkflowStageProgress.Completed);
        Assert.Equal(1, item.WorkflowStageProgress.Failed);
    }

    [Fact]
    public async Task ListAsync_ForBacklogIssue_ReturnsNullWorkflowStageProgress()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-backlog-1", Name = "Backlog Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_backlog_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Backlog issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
        };

        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }

    [Fact]
    public async Task ListAsync_ForCompletedWorkflow_ReturnsNullWorkflowStageProgress()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-done-1", Name = "Done Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_done_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Done issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            WorkflowRunId = "wf-run-done-1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Done,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = "wf-run-done-1",
            State = """
            {
              "id": "wf-run-done-1",
              "status": "Completed",
              "currentStageId": "integrate",
              "stages": []
            }
            """,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }

    [Fact]
    public async Task ListAsync_WithNoUserFacingTasks_ReturnsNullWorkflowStageProgress()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-nouser-1", Name = "No User Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_nouser_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Orchestration only issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            WorkflowRunId = "wf-run-nouser-1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.InProgress,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = "wf-run-nouser-1",
            State = """
            {
              "id": "wf-run-nouser-1",
              "status": "Running",
              "currentStageId": "build",
              "metadata": {
                "name": "test-run",
                "createdAt": "2024-01-01T00:00:00Z"
              },
              "stages": [
                {
                  "id": "build",
                  "status": "Running",
                  "attempt": 1,
                  "requiresApproval": false,
                  "initialized": true,
                  "tasks": [
                    {
                      "id": "orch-only-1.1",
                      "definitionId": "orch-only-1",
                      "attempt": 1,
                      "title": "Internal only",
                      "status": "Running",
                      "classification": "Orchestration"
                    }
                  ],
                  "checks": []
                }
              ]
            }
            """,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }

    [Fact]
    public async Task ListAsync_WithApprovalOnlyWaitingStage_OmitsWorkflowStageProgress()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-approval-1", Name = "Approval Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_approval_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Approval waiting issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            WorkflowRunId = "wf-run-approval-1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.InProgress,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = "wf-run-approval-1",
            State = """
            {
              "id": "wf-run-approval-1",
              "status": "Running",
              "currentStageId": "check",
              "metadata": {
                "name": "test-run",
                "createdAt": "2024-01-01T00:00:00Z"
              },
              "stages": [
                {
                  "id": "check",
                  "status": "AwaitingApproval",
                  "attempt": 1,
                  "requiresApproval": true,
                  "initialized": true,
                  "tasks": [
                    {
                      "id": "check-task-1.1",
                      "definitionId": "check-task-1",
                      "attempt": 1,
                      "title": "Prepare review",
                      "status": "Completed",
                      "classification": "UserFacing"
                    }
                  ],
                  "checks": [
                    {
                      "name": "merge-ready",
                      "title": "Merge ready",
                      "status": "Completed"
                    }
                  ],
                  "approvalStatus": {
                    "result": null,
                    "requestedAt": "2024-01-01T01:00:00Z",
                    "respondedAt": null
                  }
                }
              ]
            }
            """,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }
}

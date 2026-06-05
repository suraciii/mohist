using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Persistence.Issue;
using Mohist.Server.Issue.Querying;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Querying;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Views;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueQuerierSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueQuerierSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_ReadsIssueStateWithoutCallingIssueGrain()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-1", Name = "Project One", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Query me",
            Labels = ["bug"],
            Priority = "p1",
        };
        issue.Status = Issue.Domain.IssueStatus.Todo;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project, stage: "todo", label: "bug");

        var item = Assert.Single(list);
        Assert.Equal("Query me", item.Title);
        Assert.Equal("todo", item.Status);
        Assert.Equal("Project One", item.ProjectName);
    }

    [Fact]
    public async Task GetAndListAsync_ReadIssueIdKeyedRows()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-id-keyed-1", Name = "Id Keyed Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_id_keyed_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Id keyed issue",
            Labels = ["feature"],
            Priority = "p2",
        };
        issue.Status = Issue.Domain.IssueStatus.Todo;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var identities = scope.ServiceProvider.GetRequiredService<IssueIdentityResolver>();

        var loaded = await service.GetAsync(project.Id, issue.Number, project);
        var listed = await service.ListAsync(project.Id, project);
        var identity = await identities.GetAsync(project.Id, issue.Number);

        Assert.NotNull(loaded);
        Assert.Equal(issue.Id, loaded.Id);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, loaded.WorkflowProfileId);
        Assert.NotNull(identity);
        Assert.Equal(issue.Id, identity.IssueId);
        var item = Assert.Single(listed);
        Assert.Equal(issue.Id, item.Id);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, item.WorkflowProfileId);
    }

    [Fact]
    public async Task ListAsync_WithCanonicalRows_ReturnsIssueOnce()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dual-key-{Guid.NewGuid():N}", Name = "Dual Key Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = $"issue_dual_key_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Canonical title",
            Labels = [],
            Priority = "p2",
        };
        issue.Status = Issue.Domain.IssueStatus.Todo;

        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Equal(issue.Id, item.Id);
        Assert.Equal("Canonical title", item.Title);
    }

    [Fact]
    public async Task ListAsync_WithActiveWorkflowStageAndUserTasks_IncludesWorkflowStageProgress()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-progress-1", Name = "Progress Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_prog_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Progress issue",
            Labels = [],
            Priority = "p2",
            WorkflowRunId = "wf-run-1",
        };
        issue.Status = Issue.Domain.IssueStatus.InProgress;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var workflowRow = new WorkflowRunRow
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
        };
        db.WorkflowRuns.Add(workflowRow);
        db.WorkflowLeases.Add(new WorkflowLeaseRow
        {
            WorkflowRunId = "wf-run-1",
            State = "null",
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
        var project = new ProjectInfo { Id = "proj-orch-1", Name = "Orch Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_orch_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Orchestration issue",
            Labels = [],
            Priority = "p2",
            WorkflowRunId = "wf-run-orch-1",
        };
        issue.Status = Issue.Domain.IssueStatus.InProgress;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var workflowRow = new WorkflowRunRow
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
        };
        db.WorkflowRuns.Add(workflowRow);
        db.WorkflowLeases.Add(new WorkflowLeaseRow
        {
            WorkflowRunId = "wf-run-orch-1",
            State = "null",
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
        var project = new ProjectInfo { Id = "proj-fail-1", Name = "Fail Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_fail_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Failed task issue",
            Labels = [],
            Priority = "p2",
            WorkflowRunId = "wf-run-fail-1",
        };
        issue.Status = Issue.Domain.IssueStatus.InProgress;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var workflowRow = new WorkflowRunRow
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
        };
        db.WorkflowRuns.Add(workflowRow);
        db.WorkflowLeases.Add(new WorkflowLeaseRow
        {
            WorkflowRunId = "wf-run-fail-1",
            State = "null",
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
        var project = new ProjectInfo { Id = "proj-backlog-1", Name = "Backlog Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_backlog_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Backlog issue",
            Labels = [],
            Priority = "p2",
        };
        issue.Status = Issue.Domain.IssueStatus.Todo;
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
        var project = new ProjectInfo { Id = "proj-done-1", Name = "Done Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_done_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Done issue",
            Labels = [],
            Priority = "p2",
            WorkflowRunId = "wf-run-done-1",
        };
        issue.Status = Issue.Domain.IssueStatus.Done;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var workflowRow = new WorkflowRunRow
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
        };
        db.WorkflowRuns.Add(workflowRow);
        db.WorkflowLeases.Add(new WorkflowLeaseRow
        {
            WorkflowRunId = "wf-run-done-1",
            State = "null",
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
        var project = new ProjectInfo { Id = "proj-nouser-1", Name = "No User Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_nouser_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Orchestration only issue",
            Labels = [],
            Priority = "p2",
            WorkflowRunId = "wf-run-nouser-1",
        };
        issue.Status = Issue.Domain.IssueStatus.InProgress;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var workflowRow = new WorkflowRunRow
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
        };
        db.WorkflowRuns.Add(workflowRow);
        db.WorkflowLeases.Add(new WorkflowLeaseRow
        {
            WorkflowRunId = "wf-run-nouser-1",
            State = "null",
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
        var project = new ProjectInfo { Id = "proj-approval-1", Name = "Approval Project", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue
        {
            Id = "issue_approval_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Approval waiting issue",
            Labels = [],
            Priority = "p2",
            WorkflowRunId = "wf-run-approval-1",
        };
        issue.Status = Issue.Domain.IssueStatus.InProgress;
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var workflowRow = new WorkflowRunRow
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
        };
        db.WorkflowRuns.Add(workflowRow);
        db.WorkflowLeases.Add(new WorkflowLeaseRow
        {
            WorkflowRunId = "wf-run-approval-1",
            State = "null",
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }
}

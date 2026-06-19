using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Tests.Support;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_ReadsIssueStateWithoutCallingIssueGrain()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-1", Name = "Project One" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Query me",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" },
            Priority = "p1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project, stage: "backlog", label: "stream=frontend");

        var item = Assert.Single(list);
        Assert.Equal("Query me", item.Title);
        Assert.Equal("backlog", item.Status);
        Assert.Equal("Project One", item.ProjectName);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetAndListAsync_ReadIssueIdKeyedRows()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-id-keyed-1", Name = "Id Keyed Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_id_keyed_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Id keyed issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "auth" },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_WithCanonicalRows_ReturnsIssueOnce()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dual-key-{Guid.NewGuid():N}", Name = "Dual Key Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_dual_key_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Canonical title",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
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
        Assert.Equal(issue.Id, item.Id);
        Assert.Equal("Canonical title", item.Title);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.NotNull(item.WorkflowStageProgress);
        Assert.Equal(1, item.WorkflowStageProgress.Total);
        Assert.Equal(1, item.WorkflowStageProgress.Completed);
        Assert.Equal(0, item.WorkflowStageProgress.Running);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.NotNull(item.WorkflowStageProgress);
        Assert.Equal(2, item.WorkflowStageProgress.Total);
        Assert.Equal(0, item.WorkflowStageProgress.Completed);
        Assert.Equal(1, item.WorkflowStageProgress.Failed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.WorkflowStageProgress);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_FiltersByKeyValueLabel()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-kv-{Guid.NewGuid():N}", Name = "KV Project" };

        var issueA = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_a_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Stream frontend",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        var issueB = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_b_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 2,
            Title = "Stream backend",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "backend",
            },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow { IssueId = issueA.Id, State = IssueStore.Serialize(issueA) });
        db.Issues.Add(new IssueRow { IssueId = issueB.Id, State = IssueStore.Serialize(issueB) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var frontendHits = await service.ListAsync(project.Id, project, label: "stream=frontend");
        var frontendItem = Assert.Single(frontendHits);
        Assert.Equal(issueA.Number, frontendItem.Number);

        var backendHits = await service.ListAsync(project.Id, project, label: "stream=backend");
        var backendItem = Assert.Single(backendHits);
        Assert.Equal(issueB.Number, backendItem.Number);

        var missingHits = await service.ListAsync(project.Id, project, label: "stream=missing");
        Assert.Empty(missingHits);

        var keyMissHits = await service.ListAsync(project.Id, project, label: "missing=anything");
        Assert.Empty(keyMissHits);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_WithMultipleKeyValueLabels_RequiresAllFilters()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-kv-multi-{Guid.NewGuid():N}", Name = "KV Multi Project" };

        var match = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_multi_match_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Frontend auth",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        var missingModule = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_multi_miss_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 2,
            Title = "Frontend only",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow { IssueId = match.Id, State = IssueStore.Serialize(match) });
        db.Issues.Add(new IssueRow { IssueId = missingModule.Id, State = IssueStore.Serialize(missingModule) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var listed = await service.ListWithLabelFiltersAsync(
            project.Id,
            project,
            stage: null,
            labels: ["stream=frontend", "module=auth"],
            priority: null,
            archived: null,
            all: null);

        var item = Assert.Single(listed);
        Assert.Equal(match.Number, item.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ParseLabelFilter_SplitsOnFirstEquals()
    {
        var (key, value) = IssueQuerier.ParseLabelFilter("stream=frontend");
        Assert.Equal("stream", key);
        Assert.Equal("frontend", value);

        var withEqualsInValue = IssueQuerier.ParseLabelFilter("k=v=w");
        Assert.Equal("k", withEqualsInValue.Key);
        Assert.Equal("v=w", withEqualsInValue.Value);

        var noEquals = IssueQuerier.ParseLabelFilter("justatoken");
        Assert.Null(noEquals.Key);
        Assert.Equal("justatoken", noEquals.Value);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void LabelFilterTokens_SplitsCommaJoinedLegacyQuery()
    {
        Assert.Equal(
            new[] { "stream=frontend", "module=auth" },
            IssueQuerier.LabelFilterTokens("stream=frontend,module=auth"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_DayBucketing_ReturnsThirtyTrailingDays()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-day-{Guid.NewGuid():N}", Name = "Day Project" };
        var issue = SeedIssue(db, project, "issue_day_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueQuerier.CompletionBucket.Day, now);

        Assert.Equal("day", result.Bucket);
        Assert.Equal(30, result.Buckets.Count);
        Assert.Equal("2026-05-21", result.Buckets[0].Boundary);
        Assert.Equal("2026-06-19", result.Buckets[^1].Boundary);
        Assert.All(result.Buckets, b =>
        {
            Assert.Equal(0, b.Completed);
            Assert.Equal(0, b.Failed);
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_DayBucketing_BucketsCompletionAndFailureByIssueEventTime()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-day-fill-{Guid.NewGuid():N}", Name = "Day Fill Project" };
        var i1 = SeedIssue(db, project, "issue_df_1");
        var i2 = SeedIssue(db, project, "issue_df_2");
        var i3 = SeedIssue(db, project, "issue_df_3");
        await db.SaveChangesAsync();

        SeedEvent(db, i1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i2.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 18, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i3.Id, IssueQuerier.ClosedType, new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueQuerier.CompletionBucket.Day, now);

        var d17 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(2, d17.Completed);
        Assert.Equal(0, d17.Failed);
        var d19 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-19");
        Assert.Equal(0, d19.Completed);
        Assert.Equal(1, d19.Failed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_IssueEditedAfterCompletion_StaysInCompletionBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-edit-{Guid.NewGuid():N}", Name = "Edit Project" };
        var i1 = SeedIssue(db, project, "issue_edit_1", updatedAt: new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        // The completion event is in week 1 (early June).
        SeedEvent(db, i1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero));
        // The issue's `updatedAt` is in week 2 (a later edit/archive
        // touched it). The metric MUST keep the issue in the week 1
        // bucket, because bucketing reads `IssueEvents.Time` (terminal
        // transition time) — not issue `updatedAt`.
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueQuerier.CompletionBucket.Week, now);

        var total = result.Buckets.Sum(b => b.Completed + b.Failed);
        Assert.Equal(1, total);
        // 2026-06-08 is a Monday; verify the boundary of the only
        // non-zero bucket is exactly that Monday.
        var firstHit = result.Buckets.First(b => b.Completed + b.Failed > 0);
        Assert.Equal("2026-06-08", firstHit.Boundary);
        Assert.Equal(1, firstHit.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_FlappingIssue_AppearsInEachAffectedBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-flap-{Guid.NewGuid():N}", Name = "Flap Project" };
        var i1 = SeedIssue(db, project, "issue_flap_1");
        await db.SaveChangesAsync();

        // The issue closed in week 1, was reopened and closed again
        // in week 2. The endpoint must count distinct completions
        // across buckets, so it shows up in both week 1 and week 2.
        SeedEvent(db, i1.Id, IssueQuerier.ClosedType, new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, IssueQuerier.ClosedType, new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueQuerier.CompletionBucket.Week, now);

        var week1 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-08");
        Assert.Equal(1, week1.Failed);
        var week2 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-15");
        Assert.Equal(1, week2.Failed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_DistinctPerBucket_CollapsesRepeatedEventsForSameIssueAndType()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-distinct-{Guid.NewGuid():N}", Name = "Distinct Project" };
        var i1 = SeedIssue(db, project, "issue_distinct_1");
        await db.SaveChangesAsync();

        // Two same-type terminal events for the same issue in the
        // same day: must count as 1, not 2.
        SeedEvent(db, i1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        SeedEvent(db, i1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 16, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueQuerier.CompletionBucket.Day, now);

        var day = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, day.Completed);
        Assert.Equal(0, day.Failed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_ProjectScoping_OnlyCountsTargetProjectsIssues()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-scope-a-{Guid.NewGuid():N}", Name = "Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-scope-b-{Guid.NewGuid():N}", Name = "Scope B" };
        var a1 = SeedIssue(db, projectA, "issue_scope_a_1");
        var b1 = SeedIssue(db, projectB, "issue_scope_b_1");
        await db.SaveChangesAsync();

        SeedEvent(db, a1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, b1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetCompletionBucketsAsync(projectA.Id, IssueQuerier.CompletionBucket.Day, now);
        var resultB = await service.GetCompletionBucketsAsync(projectB.Id, IssueQuerier.CompletionBucket.Day, now);

        var dayA = Assert.Single(resultA.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, dayA.Completed);
        var dayB = Assert.Single(resultB.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, dayB.Completed);

        // Project A's series must not include B's event.
        Assert.DoesNotContain(resultA.Buckets, b => b.Completed > 1);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_NonTerminalEvents_AreNotCounted()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-noise-{Guid.NewGuid():N}", Name = "Noise Project" };
        var i1 = SeedIssue(db, project, "issue_noise_1");
        await db.SaveChangesAsync();

        // Only the two terminal types should count; other types
        // (work-started, archived, reopened, …) must not contribute
        // to completed/failed counts.
        SeedEvent(db, i1.Id, "com.mohist.issue.work-started", new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, "com.mohist.issue.archived", new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueQuerier.CompletionBucket.Day, now);

        var total = result.Buckets.Sum(b => b.Completed + b.Failed);
        Assert.Equal(0, total);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetCompletionBucketsAsync_WeekBucketing_ReturnsTwelveTrailingWeeks()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-week-{Guid.NewGuid():N}", Name = "Week Project" };
        SeedIssue(db, project, "issue_week_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        // 2026-06-19 is a Friday. Current ISO week starts on 2026-06-15
        // (Monday). 12 trailing weeks => boundaries 2026-03-30 …
        // 2026-06-15.
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueQuerier.CompletionBucket.Week, now);

        Assert.Equal("week", result.Bucket);
        Assert.Equal(12, result.Buckets.Count);
        Assert.Equal("2026-03-30", result.Buckets[0].Boundary);
        Assert.Equal("2026-06-15", result.Buckets[^1].Boundary);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void StartOfIsoWeek_ReturnsMondayForAnyInput()
    {
        // 2026-06-19 is a Friday; the Monday of the same week is
        // 2026-06-15.
        var friday = new DateTime(2026, 6, 19);
        Assert.Equal(new DateTime(2026, 6, 15), IssueQuerier.ISOWeekHelper.StartOfIsoWeek(friday));

        // 2026-06-15 is itself a Monday.
        var monday = new DateTime(2026, 6, 15);
        Assert.Equal(new DateTime(2026, 6, 15), IssueQuerier.ISOWeekHelper.StartOfIsoWeek(monday));

        // 2026-06-21 is a Sunday — the Monday of that week is
        // 2026-06-15, not 2026-06-22.
        var sunday = new DateTime(2026, 6, 21);
        Assert.Equal(new DateTime(2026, 6, 15), IssueQuerier.ISOWeekHelper.StartOfIsoWeek(sunday));
    }

    private static int _seedIssueCounter = 0;
    private static Mohist.Server.Issue.Domain.Issue SeedIssue(
        MohistDbContext db,
        ProjectInfo project,
        string idSuffix,
        DateTimeOffset? updatedAt = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = idSuffix,
            ProjectId = project.Id,
            Number = ++_seedIssueCounter,
            Title = "Test issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            CreatedAt = updatedAt?.UtcDateTime ?? DateTime.UtcNow,
            UpdatedAt = updatedAt?.UtcDateTime ?? DateTime.UtcNow,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        return issue;
    }

    private static void SeedEvent(
        MohistDbContext db,
        string issueId,
        string type,
        DateTimeOffset time)
    {
        var source = IssueQuerier.IssueSourcePrefix + issueId;
        var dbMax = db.IssueEvents
            .AsNoTracking()
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .Max();
        var trackedMax = db.ChangeTracker.Entries<IssueEventRow>()
            .Where(e => e.Entity.Source == source)
            .Select(e => (long?)e.Entity.Id)
            .Max();
        var nextId = (dbMax ?? 0) > (trackedMax ?? 0) ? (dbMax ?? 0) : (trackedMax ?? 0);
        nextId += 1;
        db.IssueEvents.Add(new IssueEventRow
        {
            Id = nextId,
            Source = source,
            EventId = Guid.NewGuid().ToString(),
            Type = type,
            Time = time,
            SpecVersion = "1.0",
            Subject = "1",
            DataContentType = "application/json",
            Data = System.Text.Json.JsonDocument.Parse("null").RootElement,
            ExtensionsJson = "{}",
        });
    }
}

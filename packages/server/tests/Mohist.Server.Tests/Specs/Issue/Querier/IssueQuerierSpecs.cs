using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Infrastructure;
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
        Assert.Equal(IssueWorkflowProfiles.LocalId, loaded.WorkflowProfileId);
        Assert.NotNull(identity);
        Assert.Equal(issue.Id, identity.IssueId);
        var item = Assert.Single(listed);
        Assert.Equal(issue.Id, item.Id);
        Assert.Equal(IssueWorkflowProfiles.LocalId, item.WorkflowProfileId);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_TrailingSevenDayWindow_IncludesOnlyRecentResponses()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-window-{Guid.NewGuid():N}", Name = "Approval Window Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var i1 = SeedIssue(db, project, "issue_aw_win_1", workflowRunId: "wr_aw_win_1");
        var i2 = SeedIssue(db, project, "issue_aw_win_2", workflowRunId: "wr_aw_win_2");
        var i3 = SeedIssue(db, project, "issue_aw_win_3", workflowRunId: "wr_aw_win_3");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_aw_win_1", ApprovalRunState("wr_aw_win_1", now.AddDays(-1), TimeSpan.FromHours(1)));
        await SeedWorkflowRunAsync(db, "wr_aw_win_2", ApprovalRunState("wr_aw_win_2", now.AddDays(-6), TimeSpan.FromHours(2)));
        await SeedWorkflowRunAsync(db, "wr_aw_win_3", ApprovalRunState("wr_aw_win_3", now.AddDays(-10), TimeSpan.FromHours(4)));

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(2, result.SampleCount);
        Assert.Equal(now.AddDays(-7), result.Window.From);
        Assert.Equal(now, result.Window.To);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_Statistics_ReturnsAverageMedianMaxFromSameSamples()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-stats-{Guid.NewGuid():N}", Name = "Approval Stats Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var waits = new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(16) };

        for (var i = 0; i < waits.Length; i++)
        {
            var runId = $"wr_aw_stats_{i}";
            SeedIssue(db, project, $"issue_aw_stats_{i}", workflowRunId: runId);
            await SeedWorkflowRunAsync(db, runId, ApprovalRunState(runId, now.AddDays(-1), waits[i]));
        }

        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(5, result.SampleCount);
        Assert.Equal(TimeSpan.FromHours(5).TotalSeconds, result.AverageSeconds);
        Assert.Equal(TimeSpan.FromHours(2).TotalSeconds, result.MedianSeconds);
        Assert.Equal(TimeSpan.FromHours(16).TotalSeconds, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_SingleSample_YieldsIdenticalAverageMedianMax()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-single-{Guid.NewGuid():N}", Name = "Approval Single Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_single_1", workflowRunId: "wr_aw_single_1");
        await SeedWorkflowRunAsync(db, "wr_aw_single_1", ApprovalRunState("wr_aw_single_1", now.AddDays(-1), TimeSpan.FromHours(3.2)));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        var expected = TimeSpan.FromHours(3.2).TotalSeconds;
        Assert.Equal(1, result.SampleCount);
        Assert.Equal(expected, result.AverageSeconds);
        Assert.Equal(expected, result.MedianSeconds);
        Assert.Equal(expected, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_PendingApproval_IsExcludedFromAggregate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-pending-{Guid.NewGuid():N}", Name = "Approval Pending Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_pending_1", workflowRunId: "wr_aw_pending_1");
        SeedIssue(db, project, "issue_aw_pending_2", workflowRunId: "wr_aw_pending_2");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_aw_pending_1", ApprovalRunState("wr_aw_pending_1", now.AddDays(-1), TimeSpan.FromHours(1), "approved"));
        await SeedWorkflowRunAsync(db, "wr_aw_pending_2", AwaitingApprovalRunState("wr_aw_pending_2", now.AddDays(-1)));

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(TimeSpan.FromHours(1).TotalSeconds, result.AverageSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_RejectedApproval_ContributesLikeApproved()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-rejected-{Guid.NewGuid():N}", Name = "Approval Rejected Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var wait = TimeSpan.FromHours(4);

        SeedIssue(db, project, "issue_aw_rejected_1", workflowRunId: "wr_aw_rejected_1");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_aw_rejected_1", ApprovalRunState("wr_aw_rejected_1", now.AddDays(-1), wait, "rejected"));

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(wait.TotalSeconds, result.AverageSeconds);
        Assert.Equal(wait.TotalSeconds, result.MedianSeconds);
        Assert.Equal(wait.TotalSeconds, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_MultipleCompletedApprovalStagesInOneRun_CountsEachGate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-multi-{Guid.NewGuid():N}", Name = "Approval Multi Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var planWait = TimeSpan.FromHours(1);
        var checkWait = TimeSpan.FromHours(4);

        SeedIssue(db, project, "issue_aw_multi_1", workflowRunId: "wr_aw_multi_1");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(
            db,
            "wr_aw_multi_1",
            MultiApprovalRunState(
                "wr_aw_multi_1",
                planRequestedAt: now.AddDays(-2),
                planWait,
                checkRequestedAt: now.AddDays(-1),
                checkWait));

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        var expectedAverage = (planWait.TotalSeconds + checkWait.TotalSeconds) / 2;
        Assert.Equal(2, result.SampleCount);
        Assert.Equal(expectedAverage, result.AverageSeconds);
        Assert.Equal(expectedAverage, result.MedianSeconds);
        Assert.Equal(checkWait.TotalSeconds, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_ZeroSamples_ReturnsEmptyResultWithNullStats()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-empty-{Guid.NewGuid():N}", Name = "Approval Empty Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_empty_1", workflowRunId: "wr_aw_empty_1");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_aw_empty_1", AwaitingApprovalRunState("wr_aw_empty_1", now.AddDays(-1)));

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(0, result.SampleCount);
        Assert.Null(result.AverageSeconds);
        Assert.Null(result.MedianSeconds);
        Assert.Null(result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_ZeroDurationWait_IsDistinguishableFromEmpty()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-zero-{Guid.NewGuid():N}", Name = "Approval Zero Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_zero_1", workflowRunId: "wr_aw_zero_1");
        await SeedWorkflowRunAsync(db, "wr_aw_zero_1", ApprovalRunState("wr_aw_zero_1", now.AddDays(-1), TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(0, result.AverageSeconds);
        Assert.Equal(0, result.MedianSeconds);
        Assert.Equal(0, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_ProjectScoping_OnlyCountsTargetProjectsIssues()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-approval-scope-a-{Guid.NewGuid():N}", Name = "Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-approval-scope-b-{Guid.NewGuid():N}", Name = "Scope B" };
        SeedIssue(db, projectA, "issue_aw_scope_a_1", workflowRunId: "wr_aw_scope_a_1");
        SeedIssue(db, projectB, "issue_aw_scope_b_1", workflowRunId: "wr_aw_scope_b_1");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_aw_scope_a_1", ApprovalRunState("wr_aw_scope_a_1", new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1)));
        await SeedWorkflowRunAsync(db, "wr_aw_scope_b_1", ApprovalRunState("wr_aw_scope_b_1", new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(5)));

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetApprovalWaitAsync(projectA.Id, now);
        var resultB = await service.GetApprovalWaitAsync(projectB.Id, now);

        Assert.Equal(1, resultA.SampleCount);
        Assert.Equal(TimeSpan.FromHours(1).TotalSeconds, resultA.AverageSeconds);
        Assert.Equal(1, resultB.SampleCount);
        Assert.Equal(TimeSpan.FromHours(5).TotalSeconds, resultB.AverageSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_AllChecksZeroRepair_IsFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-ftr-{Guid.NewGuid():N}", Name = "Quality FTR Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_ftr_1", workflowRunId: "wr_quality_ftr_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue.Id, IssueQuerier.WorkCompletedType, now.AddDays(-2));
        await SeedWorkflowRunAsync(db, "wr_quality_ftr_1", QualityRunState("wr_quality_ftr_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window7d.SampleCount);
        Assert.Equal(1.0, result.Window7d.FirstTimeRightRate);
        Assert.Contains(result.Window7d.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(result.Window7d.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_AnyRepairedCheck_IsNotFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-rework-{Guid.NewGuid():N}", Name = "Quality Rework Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_rework_1", workflowRunId: "wr_quality_rework_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue.Id, IssueQuerier.WorkCompletedType, now.AddDays(-2));
        await SeedWorkflowRunAsync(db, "wr_quality_rework_1", QualityRunState("wr_quality_rework_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 1)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window7d.SampleCount);
        Assert.Equal(0.0, result.Window7d.FirstTimeRightRate);
        Assert.Contains(result.Window7d.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(result.Window7d.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 1.0);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_NonDoneIssues_AreExcludedFromNumeratorAndDenominator()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-status-{Guid.NewGuid():N}", Name = "Quality Status Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var shipped = SeedIssue(db, project, "issue_quality_status_shipped", workflowRunId: "wr_quality_status_shipped", status: IssueStatus.Done);
        var inProgress = SeedIssue(db, project, "issue_quality_status_inprogress", workflowRunId: "wr_quality_status_inprogress", status: IssueStatus.InProgress);
        SeedIssue(db, project, "issue_quality_status_backlog", workflowRunId: null, status: IssueStatus.Backlog);
        await db.SaveChangesAsync();

        SeedEvent(db, shipped.Id, IssueQuerier.WorkCompletedType, now.AddDays(-2));
        await SeedWorkflowRunAsync(db, "wr_quality_status_shipped", QualityRunState("wr_quality_status_shipped", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_status_inprogress", QualityRunState("wr_quality_status_inprogress", [("plan", [("plan-ok", "Plan ok", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window7d.SampleCount);
        Assert.Equal(1.0, result.Window7d.FirstTimeRightRate);
        Assert.DoesNotContain(result.Window7d.Stages, s => s.Stage == "plan" && s.EnteredCount != 1);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_NeverEnteredStage_IsExcludedFromStageRate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-stage-{Guid.NewGuid():N}", Name = "Quality Stage Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_stage_1", workflowRunId: "wr_quality_stage_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue.Id, IssueQuerier.WorkCompletedType, now.AddDays(-2));
        await SeedWorkflowRunAsync(db, "wr_quality_stage_1", QualityRunState("wr_quality_stage_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
            ("integrate", null),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Contains(result.Window7d.Stages, s => s.Stage == "plan" && s.EnteredCount == 1);
        Assert.Contains(result.Window7d.Stages, s => s.Stage == "build" && s.EnteredCount == 1);
        Assert.DoesNotContain(result.Window7d.Stages, s => s.Stage == "integrate");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_WindowBucketing_BucketsByShipEventTime()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-window-{Guid.NewGuid():N}", Name = "Quality Window Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var recent = SeedIssue(db, project, "issue_quality_win_recent", workflowRunId: "wr_quality_win_recent", status: IssueStatus.Done);
        var mid = SeedIssue(db, project, "issue_quality_win_mid", workflowRunId: "wr_quality_win_mid", status: IssueStatus.Done);
        var old = SeedIssue(db, project, "issue_quality_win_old", workflowRunId: "wr_quality_win_old", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, recent.Id, IssueQuerier.WorkCompletedType, now.AddDays(-3));
        SeedEvent(db, mid.Id, IssueQuerier.WorkCompletedType, now.AddDays(-20));
        SeedEvent(db, old.Id, IssueQuerier.WorkCompletedType, now.AddDays(-40));

        await SeedWorkflowRunAsync(db, "wr_quality_win_recent", QualityRunState("wr_quality_win_recent", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_win_mid", QualityRunState("wr_quality_win_mid", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_win_old", QualityRunState("wr_quality_win_old", [("plan", [("plan-ok", "Plan ok", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window7d.SampleCount);
        Assert.Equal(2, result.Window30d.SampleCount);
        Assert.Equal(1.0, result.Window7d.FirstTimeRightRate);
        Assert.Equal(1.0, result.Window30d.FirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_EmptyWindow_ReturnsNullRatesWithZeroSampleCount()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-empty-{Guid.NewGuid():N}", Name = "Quality Empty Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_quality_empty_1", workflowRunId: "wr_quality_empty_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(0, result.Window7d.SampleCount);
        Assert.Null(result.Window7d.FirstTimeRightRate);
        Assert.Empty(result.Window7d.Stages);
        Assert.Equal(0, result.Window30d.SampleCount);
        Assert.Null(result.Window30d.FirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_PerStageDenominators_AreIndependent()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-denom-{Guid.NewGuid():N}", Name = "Quality Denom Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var reachedIntegrate = SeedIssue(db, project, "issue_quality_denom_integrate", workflowRunId: "wr_quality_denom_integrate", status: IssueStatus.Done);
        var onlyPlan = SeedIssue(db, project, "issue_quality_denom_plan", workflowRunId: "wr_quality_denom_plan", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, reachedIntegrate.Id, IssueQuerier.WorkCompletedType, now.AddDays(-2));
        SeedEvent(db, onlyPlan.Id, IssueQuerier.WorkCompletedType, now.AddDays(-2));

        await SeedWorkflowRunAsync(db, "wr_quality_denom_integrate", QualityRunState("wr_quality_denom_integrate", [
            ("plan", [("plan-ok", "Plan ok", 1)]),
            ("integrate", [("integrate-ok", "Integrate ok", 0)]),
        ]));
        await SeedWorkflowRunAsync(db, "wr_quality_denom_plan", QualityRunState("wr_quality_denom_plan", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("integrate", null),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var plan = Assert.Single(result.Window7d.Stages, s => s.Stage == "plan");
        Assert.Equal(2, plan.EnteredCount);
        Assert.Equal(0.5, plan.ReworkRate);

        var integrate = Assert.Single(result.Window7d.Stages, s => s.Stage == "integrate");
        Assert.Equal(1, integrate.EnteredCount);
        Assert.Equal(0.0, integrate.ReworkRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_ProjectScoping_OnlyCountsTargetProjectsIssues()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-quality-scope-a-{Guid.NewGuid():N}", Name = "Quality Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-quality-scope-b-{Guid.NewGuid():N}", Name = "Quality Scope B" };
        var a1 = SeedIssue(db, projectA, "issue_quality_scope_a_1", workflowRunId: "wr_quality_scope_a_1", status: IssueStatus.Done);
        var b1 = SeedIssue(db, projectB, "issue_quality_scope_b_1", workflowRunId: "wr_quality_scope_b_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, a1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
        SeedEvent(db, b1.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        await SeedWorkflowRunAsync(db, "wr_quality_scope_a_1", QualityRunState("wr_quality_scope_a_1", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_scope_b_1", QualityRunState("wr_quality_scope_b_1", [("plan", [("plan-ok", "Plan ok", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetQualityAsync(projectA.Id, now);
        var resultB = await service.GetQualityAsync(projectB.Id, now);

        Assert.Equal(1, resultA.Window7d.SampleCount);
        Assert.Equal(1.0, resultA.Window7d.FirstTimeRightRate);
        Assert.Equal(1, resultB.Window7d.SampleCount);
        Assert.Equal(0.0, resultB.Window7d.FirstTimeRightRate);
    }

    private static int _seedIssueCounter = 0;
    private static Mohist.Server.Issue.Domain.Issue SeedIssue(
        MohistDbContext db,
        ProjectInfo project,
        string idSuffix,
        DateTimeOffset? updatedAt = null,
        string? workflowRunId = null,
        Mohist.Server.Issue.Domain.IssueStatus? status = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = idSuffix,
            ProjectId = project.Id,
            Number = ++_seedIssueCounter,
            Title = "Test issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = status ?? Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            CreatedAt = updatedAt?.UtcDateTime ?? DateTime.UtcNow,
            UpdatedAt = updatedAt?.UtcDateTime ?? DateTime.UtcNow,
            WorkflowRunId = workflowRunId,
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

    private static async Task SeedWorkflowRunAsync(MohistDbContext db, string workflowRunId, object state)
    {
        var json = JsonSerializer.Serialize(state, JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);
    }

    private static object ApprovalRunState(string workflowRunId, DateTimeOffset requestedAt, TimeSpan wait, string result = "approved") =>
        RunState(workflowRunId, requestedAt, requestedAt + wait, result);

    private static object AwaitingApprovalRunState(string workflowRunId, DateTimeOffset requestedAt) =>
        RunState(workflowRunId, requestedAt, null, null);

    private static object MultiApprovalRunState(
        string workflowRunId,
        DateTimeOffset planRequestedAt,
        TimeSpan planWait,
        DateTimeOffset checkRequestedAt,
        TimeSpan checkWait)
    {
        const string planStage = "plan";
        const string checkStage = "check";
        return new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = planRequestedAt.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = checkStage,
            Stages = new[]
            {
                new
                {
                    Id = planStage,
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/acp-agent" },
                    },
                    Checks = new[]
                    {
                        new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = "approved",
                        RequestedAt = planRequestedAt.ToString("O"),
                        RespondedAt = (planRequestedAt + planWait).ToString("O"),
                    },
                },
                new
                {
                    Id = checkStage,
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "review", DefinitionId = "review", Attempt = 1, Title = "Check review", Status = "Completed", Uses = "mohist/acp-agent" },
                    },
                    Checks = new[]
                    {
                        new { Name = "check-ok", Title = "Check ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = "approved",
                        RequestedAt = checkRequestedAt.ToString("O"),
                        RespondedAt = (checkRequestedAt + checkWait).ToString("O"),
                    },
                }
            }
        };
    }

    private static object RunState(string workflowRunId, DateTimeOffset requestedAt, DateTimeOffset? respondedAt, string? result)
    {
        const string stage = "plan";
        return new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = requestedAt.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = stage,
            Stages = new[]
            {
                new
                {
                    Id = stage,
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/acp-agent" },
                    },
                    Checks = new[]
                    {
                        new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = result,
                        RequestedAt = requestedAt.ToString("O"),
                        RespondedAt = respondedAt?.ToString("O"),
                    },
                }
            }
        };
    }

    private static object QualityRunState(
        string workflowRunId,
        (string Stage, (string Name, string Title, int RepairCount)[]? Checks)[] stages)
    {
        var now = DateTimeOffset.UtcNow;
        var stageObjects = stages.Select(s =>
        {
            var initialized = s.Checks is not null;
            var checks = s.Checks is null
                ? Array.Empty<object>()
                : s.Checks.Select(c => (object)new
                {
                    Name = c.Name,
                    Title = c.Title,
                    Status = "Passed",
                    RepairCount = c.RepairCount,
                }).ToArray();

            return (object)new
            {
                Id = s.Stage,
                Attempt = 1,
                RequiresApproval = false,
                Initialized = initialized,
                Status = initialized ? "Completed" : "Pending",
                Tasks = initialized
                    ? new[] { new { Id = $"{s.Stage}-task", DefinitionId = $"{s.Stage}-task", Attempt = 1, Title = $"{s.Stage} task", Status = "Completed", Uses = "mohist/acp-agent" } }
                    : Array.Empty<object>(),
                Checks = checks,
            };
        }).ToArray();

        var currentStage = stages.LastOrDefault(s => s.Checks is not null).Stage
            ?? stages.First().Stage;

        return new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = now.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = currentStage,
            Stages = stageObjects,
        };
    }
}


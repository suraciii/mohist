using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
    public async Task ListAsync_ForDoneIssue_IncludesCompletedAt()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-list-{Guid.NewGuid():N}", Name = "CompletedAt List" };
        var completedAt = new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_list_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Done with completedAt",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Done,
            CompletedAt = completedAt,
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Equal(completedAt.ToString("o"), item.CompletedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DetailAsync_ForCancelledIssue_IncludesCompletedAt()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-detail-{Guid.NewGuid():N}", Name = "CompletedAt Detail" };
        var completedAt = new DateTime(2026, 6, 20, 14, 0, 0, DateTimeKind.Utc);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_detail_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Cancelled with completedAt",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Cancelled,
            CompletedAt = completedAt,
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Equal(completedAt.ToString("o"), detail.CompletedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DetailAsync_ForNonTerminalIssue_CompletedAtIsNull()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-null-{Guid.NewGuid():N}", Name = "CompletedAt Null" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_null_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Backlog no completedAt",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Null(detail.CompletedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DetailAsync_ArchivedIssue_ExposesSameCompletedAt()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-archived-{Guid.NewGuid():N}", Name = "CompletedAt Archived" };
        var completedAt = new DateTime(2026, 6, 25, 9, 15, 0, DateTimeKind.Utc);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_archived_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Archived done issue",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Done,
            CompletedAt = completedAt,
            ArchivedAt = new DateTime(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc),
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Equal(completedAt.ToString("o"), detail.CompletedAt);
        Assert.NotNull(detail.ArchivedAt);
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
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
using Mohist.Server.SpecTests.Specs.Sessions;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

internal static class IssueMetricsTestSupport
{
    public static Mohist.Server.Issue.Domain.Issue SeedDeliveredIssue(
        MohistDbContext db,
        ProjectInfo project,
        string idSuffix,
        DateTime createdAt,
        DateTime completedAt,
        string? workflowRunId = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = project.Id,
            Number = ++_seedIssueCounter,
            Title = "Delivered test issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Done,
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt,
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = issue.ProjectId,
            Number = issue.Number,
            State = IssueStore.Serialize(issue),
        });
        return issue;
    }

    public static void UpdateCompletedAtAndCreatedAt(
        MohistDbContext db,
        Mohist.Server.Issue.Domain.Issue issue,
        DateTime createdAt,
        DateTime completedAt)
    {
        var row = db.Issues.AsNoTracking()
            .FirstOrDefault(r => r.ProjectId == issue.ProjectId && r.Number == issue.Number)
            ?? throw new InvalidOperationException($"Issue #{issue.Number} not found");
        var state = IssueStore.Deserialize(row.State)
            ?? throw new InvalidOperationException($"Issue #{issue.Number} state could not be deserialized");
        var updated = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = state.ProjectId,
            Number = state.Number,
            Title = state.Title,
            Body = state.Body,
            Status = state.Status,
            Priority = state.Priority,
            Risk = state.Risk,
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            ArchivedAt = state.ArchivedAt,
            CompletedAt = completedAt,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        var tracked = db.Issues.First(r => r.ProjectId == issue.ProjectId && r.Number == issue.Number);
        tracked.State = IssueStore.Serialize(updated);
    }

    public static void UpdateIssueUpdatedAt(
        MohistDbContext db,
        Mohist.Server.Issue.Domain.Issue issue,
        DateTime updatedAt)
    {
        var row = db.Issues.AsNoTracking()
            .FirstOrDefault(r => r.ProjectId == issue.ProjectId && r.Number == issue.Number)
            ?? throw new InvalidOperationException($"Issue #{issue.Number} not found");
        var state = IssueStore.Deserialize(row.State)
            ?? throw new InvalidOperationException($"Issue #{issue.Number} state could not be deserialized");
        var updated = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = state.ProjectId,
            Number = state.Number,
            Title = state.Title,
            Body = state.Body,
            Status = state.Status,
            Priority = state.Priority,
            Risk = state.Risk,
            CreatedAt = state.CreatedAt,
            UpdatedAt = updatedAt,
            ArchivedAt = state.ArchivedAt,
            CompletedAt = state.CompletedAt,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        var tracked = db.Issues.First(r => r.ProjectId == issue.ProjectId && r.Number == issue.Number);
        tracked.State = IssueStore.Serialize(updated);
    }

    private static int _seedIssueCounter = 0;
    public static Mohist.Server.Issue.Domain.Issue SeedIssue(
        MohistDbContext db,
        ProjectInfo project,
        string idSuffix,
        DateTimeOffset? updatedAt = null,
        string? workflowRunId = null,
        Mohist.Server.Issue.Domain.IssueStatus? status = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = project.Id,
            Number = ++_seedIssueCounter,
            Title = "Test issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = status ?? Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            CreatedAt = updatedAt?.UtcDateTime ?? TestTime.UtcDateTime,
            UpdatedAt = updatedAt?.UtcDateTime ?? TestTime.UtcDateTime,
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = issue.ProjectId,
            Number = issue.Number,
            State = IssueStore.Serialize(issue),
        });
        return issue;
    }

    public static void SeedEvent(
        MohistDbContext db,
        Mohist.Server.Issue.Domain.Issue issue,
        string type,
        DateTimeOffset time,
        string? workflowRunId = null)
    {
        var source = IssueEventPersistence.IssueSource(issue.ProjectId, issue.Number);
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
            Subject = issue.Number.ToString(),
            DataContentType = "application/json",
            Data = workflowRunId is null
                ? JsonDocument.Parse("null").RootElement
                : JsonSerializer.SerializeToElement(new { workflowRunId }, JSON.Options),
            ExtensionsJson = "{}",
        });
    }

    public static async Task SeedWorkflowRunAsync(MohistDbContext db, string workflowRunId, object state)
    {
        var json = JsonSerializer.Serialize(state, JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);
    }

    public static void SeedWorkflowRunEvent(
        MohistDbContext db,
        string workflowRunId,
        long sequence,
        string type,
        DateTimeOffset time,
        object data)
    {
        db.WorkflowRunEvents.Add(new WorkflowRunEventRow
        {
            Id = sequence,
            Source = WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
            EventId = Guid.NewGuid().ToString(),
            Type = type,
            Time = time,
            SpecVersion = "1.0",
            Subject = null,
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(data, JSON.Options),
            ExtensionsJson = "{}",
        });
    }

    public static object ApprovalRunState(string workflowRunId, DateTimeOffset requestedAt, TimeSpan wait, string result = "approved") =>
        RunState(workflowRunId, requestedAt, requestedAt + wait, result);

    public static object AwaitingApprovalRunState(string workflowRunId, DateTimeOffset requestedAt) =>
        RunState(workflowRunId, requestedAt, null, null);

    public static object MultiApprovalRunState(
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
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/opencode" },
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
                        new { Id = "review", DefinitionId = "review", Attempt = 1, Title = "Check review", Status = "Completed", Uses = "mohist/opencode" },
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

    public static object RunState(string workflowRunId, DateTimeOffset requestedAt, DateTimeOffset? respondedAt, string? result)
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
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/opencode" },
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

    public static object QualityRunState(
        string workflowRunId,
        (string Stage, (string Name, string Title, int ReworkCount)[]? Checks)[] stages)
    {
        var now = TestTime.UtcNow;
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
                }).ToArray();
            var tasks = new List<object>();
            if (initialized)
            {
                tasks.Add(new { Id = $"{s.Stage}-task", DefinitionId = $"{s.Stage}-task", Attempt = 1, Title = $"{s.Stage} task", Status = "Completed", Uses = "mohist/opencode" });
                foreach (var check in s.Checks!.Where(c => c.ReworkCount > 0))
                    tasks.Add(new { Id = $"recover:{check.Name}.1", DefinitionId = $"recover:{check.Name}", Attempt = 1, Title = $"{check.Title} recovery", Status = "Completed", Uses = "mohist/opencode" });
            }

            return (object)new
            {
                Id = s.Stage,
                Attempt = 1,
                RequiresApproval = false,
                Initialized = initialized,
                Status = initialized ? "Completed" : "Pending",
                Tasks = tasks.ToArray(),
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

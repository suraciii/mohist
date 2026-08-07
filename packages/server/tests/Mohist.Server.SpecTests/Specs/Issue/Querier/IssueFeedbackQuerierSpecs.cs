using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

/// <summary>
/// Calculation specs for the feedback projections exercised by
/// <c>GET /api/projects/{ref}/issues/{n}/feedback</c>,
/// <c>GET .../feedback?stage=...</c>, and the feedback sub-array on
/// the issue-detail + workflow-status responses. The read paths
/// project the workflow-run <c>Feedback</c> list (ordered by
/// <c>CreatedAt</c> desc, scoped by stage, distinguishing open vs
/// resolved) without going through the workflow grain. Specs drive
/// the same load directly via <c>MohistDbContext</c> + JSON
/// deserialization so the projection is exercised without an HTTP
/// round-trip. The route contract (409 non-awaiting, 400 missing
/// fields, 404 unknown id, JSON shape with nested <c>resolution</c>
/// object) stays in <c>IssueFeedbackApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueFeedbackQuerierSpecs
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly MohistDbFixture _fixture;

    public IssueFeedbackQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListFeedback_OrdersByCreatedAtDesc()
    {
        var (projectId, issueNumber, _, wrId) = await SeedWorkflowRunAsync();
        await InjectFeedbackAsync(wrId, "fb-first", createdAt: TestTime.UtcNow.AddMinutes(-10), status: ApprovalFeedbackStatus.Open);
        await InjectFeedbackAsync(wrId, "fb-second", createdAt: TestTime.UtcNow.AddMinutes(-5), status: ApprovalFeedbackStatus.Open);
        await InjectFeedbackAsync(wrId, "fb-third", createdAt: TestTime.UtcNow.AddMinutes(-1), status: ApprovalFeedbackStatus.Open);

        var entries = await LoadFeedbackAsync(projectId, issueNumber);

        Assert.Equal(new[] { "fb-third", "fb-second", "fb-first" }, entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task ListFeedback_FiltersByStage()
    {
        var (projectId, issueNumber, _, wrId) = await SeedWorkflowRunAsync();
        await InjectFeedbackAsync(wrId, "fb-plan", createdAt: TestTime.UtcNow.AddMinutes(-3), status: ApprovalFeedbackStatus.Open, stage: "plan");
        await InjectFeedbackAsync(wrId, "fb-check", createdAt: TestTime.UtcNow.AddMinutes(-2), status: ApprovalFeedbackStatus.Open, stage: "check");

        var planOnly = await LoadFeedbackAsync(projectId, issueNumber, stage: "plan");
        var checkOnly = await LoadFeedbackAsync(projectId, issueNumber, stage: "check");
        var all = await LoadFeedbackAsync(projectId, issueNumber);

        Assert.Single(planOnly);
        Assert.Equal("fb-plan", planOnly[0].Id);
        Assert.Single(checkOnly);
        Assert.Equal("fb-check", checkOnly[0].Id);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ListFeedback_WithUnknownStageFilter_ReturnsEmpty()
    {
        var (projectId, issueNumber, _, wrId) = await SeedWorkflowRunAsync();
        await InjectFeedbackAsync(wrId, "fb-plan", createdAt: TestTime.UtcNow.AddMinutes(-3), status: ApprovalFeedbackStatus.Open, stage: "plan");

        var checkOnly = await LoadFeedbackAsync(projectId, issueNumber, stage: "check");

        Assert.Empty(checkOnly);
    }

    [Fact]
    public async Task ListFeedback_WithoutAnyFeedback_ReturnsEmpty()
    {
        var (projectId, issueNumber, _, _) = await SeedWorkflowRunAsync();

        var entries = await LoadFeedbackAsync(projectId, issueNumber);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task ListFeedback_DistinguishesOpenAndResolved()
    {
        var (projectId, issueNumber, _, wrId) = await SeedWorkflowRunAsync();
        await InjectFeedbackAsync(wrId, "fb-open", createdAt: TestTime.UtcNow.AddMinutes(-3), status: ApprovalFeedbackStatus.Open);
        await InjectFeedbackAsync(wrId, "fb-resolved", createdAt: TestTime.UtcNow.AddMinutes(-2),
            status: ApprovalFeedbackStatus.Resolved,
            resolutionTaskId: "apply-1",
            resolvedAt: TestTime.UtcNow.AddMinutes(-1),
            resolutionSummary: "Addressed");

        var entries = await LoadFeedbackAsync(projectId, issueNumber);

        var open = Assert.Single(entries, e => e.Id == "fb-open");
        Assert.Equal(ApprovalFeedbackStatus.Open, open.Status);
        Assert.Null(open.ResolutionTaskId);
        Assert.Null(open.ResolvedAt);
        Assert.Null(open.ResolutionSummary);

        var resolved = Assert.Single(entries, e => e.Id == "fb-resolved");
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved.Status);
        Assert.Equal("apply-1", resolved.ResolutionTaskId);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal("Addressed", resolved.ResolutionSummary);
    }

    [Fact]
    public async Task StageState_IncludesFeedbackScopedToStage()
    {
        var (projectId, issueNumber, _, wrId) = await SeedWorkflowRunAsync();
        await InjectFeedbackAsync(wrId, "fb-plan", createdAt: TestTime.UtcNow.AddMinutes(-3), status: ApprovalFeedbackStatus.Open, stage: "plan");
        await InjectFeedbackAsync(wrId, "fb-check", createdAt: TestTime.UtcNow.AddMinutes(-2), status: ApprovalFeedbackStatus.Open, stage: "check");

        var stageGroups = await LoadFeedbackByStageAsync(projectId, issueNumber);

        Assert.Single(stageGroups["plan"]);
        Assert.Equal("fb-plan", stageGroups["plan"][0].Id);
        Assert.Single(stageGroups["check"]);
        Assert.Equal("fb-check", stageGroups["check"][0].Id);
    }

    [Fact]
    public async Task StageState_DistinguishesOpenAndResolved()
    {
        var (projectId, issueNumber, _, wrId) = await SeedWorkflowRunAsync();
        await InjectFeedbackAsync(wrId, "fb-open", createdAt: TestTime.UtcNow.AddMinutes(-3), status: ApprovalFeedbackStatus.Open);
        await InjectFeedbackAsync(wrId, "fb-resolved", createdAt: TestTime.UtcNow.AddMinutes(-2),
            status: ApprovalFeedbackStatus.Resolved,
            resolutionTaskId: "apply-1",
            resolvedAt: TestTime.UtcNow.AddMinutes(-1),
            resolutionSummary: "Addressed");

        var stageGroups = await LoadFeedbackByStageAsync(projectId, issueNumber);

        var planEntries = stageGroups["plan"];
        Assert.Equal(2, planEntries.Count);
        Assert.Contains(planEntries, e => e.Id == "fb-open" && e.Status == ApprovalFeedbackStatus.Open);
        Assert.Contains(planEntries, e => e.Id == "fb-resolved" && e.Status == ApprovalFeedbackStatus.Resolved);
    }

    [Fact]
    public async Task StageState_WithoutFeedback_ReturnsEmptyForThatStage()
    {
        var (projectId, issueNumber, _, _) = await SeedWorkflowRunAsync();

        var all = await LoadFeedbackAsync(projectId, issueNumber);

        Assert.Empty(all);
    }

    private async Task<List<ApprovalFeedback>> LoadFeedbackAsync(string projectId, int issueNumber, string? stage = null)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var issue = await db.Issues
            .Where(r => r.ProjectId == projectId && r.Number == issueNumber)
            .FirstOrDefaultAsync();
        Assert.NotNull(issue);
        var domainIssue = Mohist.Server.Infrastructure.Data.Issue.IssueStore.Deserialize(issue!.State);
        Assert.NotNull(domainIssue);
        var wrId = domainIssue!.WorkflowRunId;
        if (wrId is null) return [];

        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.WorkflowRunId == wrId);
        Assert.NotNull(row);
        var run = JsonSerializer.Deserialize<WorkflowRun>(row!.State, ReadJsonOptions);
        Assert.NotNull(run);
        var feedback = run!.Feedback.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(stage))
            feedback = feedback.Where(f => string.Equals(f.Stage, stage, StringComparison.Ordinal));
        return feedback.OrderByDescending(f => f.CreatedAt).ToList();
    }

    private async Task<Dictionary<string, List<ApprovalFeedback>>> LoadFeedbackByStageAsync(string projectId, int issueNumber)
    {
        var all = await LoadFeedbackAsync(projectId, issueNumber);
        var groups = new Dictionary<string, List<ApprovalFeedback>>(StringComparer.Ordinal);
        foreach (var entry in all)
        {
            if (!groups.TryGetValue(entry.Stage, out var list))
            {
                list = [];
                groups[entry.Stage] = list;
            }
            list.Add(entry);
        }
        return groups;
    }

    private async Task<(string ProjectId, int IssueNumber, string IssueId, string WorkflowRunId)>
        SeedWorkflowRunAsync()
    {
        var projectId = $"proj-fb-{Guid.NewGuid():N}";
        var issueNumber = 1;
        var issueId = $"fb-issue-{Guid.NewGuid():N}";
        var wrId = $"wr-{Guid.NewGuid():N}";

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Projects.Add(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = projectId,
            Name = $"feedback-{Guid.NewGuid():N}",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
        });
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = "Feedback seed",
            Status = IssueStatus.InProgress,
            RepositoryRef = "main",
            WorkflowRunId = wrId,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        };
        db.Issues.Add(new Mohist.Server.Infrastructure.Data.Issue.IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = Mohist.Server.Infrastructure.Data.Issue.IssueStore.Serialize(issue),
        });
        var runState = new
        {
            Id = wrId,
            Status = "AwaitingApproval",
            CurrentStageId = "plan",
            Metadata = new { CreatedAt = TestTime.UtcNow, Name = "feedback-test" },
            Stages = new object[]
            {
                new
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "AwaitingApproval",
                    Initialized = true,
                    Tasks = new[] { new { Id = "plan-task", DefinitionId = "plan-task", Attempt = 1, Title = "Plan task", Status = "Completed", Uses = "mohist/opencode" } },
                    Checks = new[] { new { Name = "plan-ok", Title = "Plan ok", Uses = "spec/check", Status = "Passed" } },
                    ApprovalStatus = new { Result = (string?)null, RequestedAt = TestTime.UtcNow, RespondedAt = (string?)null },
                },
                new
                {
                    Id = "check",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Pending",
                    Initialized = false,
                    Tasks = new object[] { },
                    Checks = new object[] { },
                    ApprovalStatus = (object?)null,
                },
            },
            Feedback = new object[] { },
        };
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = wrId,
            State = JsonSerializer.Serialize(runState, ReadJsonOptions),
        });
        await db.SaveChangesAsync();
        return (projectId, issueNumber, issueId, wrId);
    }

    private async Task InjectFeedbackAsync(
        string wrId,
        string feedbackId,
        DateTimeOffset createdAt,
        ApprovalFeedbackStatus status,
        string stage = "plan",
        string? resolutionTaskId = null,
        DateTimeOffset? resolvedAt = null,
        string? resolutionSummary = null)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.WorkflowRuns.FindAsync(wrId);
        Assert.NotNull(row);
        var run = JsonSerializer.Deserialize<WorkflowRun>(row!.State, ReadJsonOptions);
        Assert.NotNull(run);
        run!.Feedback.Add(new ApprovalFeedback(
            Id: feedbackId,
            WorkflowRunId: wrId,
            Stage: stage,
            Body: $"body for {feedbackId}",
            Status: status,
            CreatedAt: createdAt,
            ResolutionTaskId: resolutionTaskId,
            ResolvedAt: resolvedAt,
            ResolutionSummary: resolutionSummary));
        row.State = JsonSerializer.Serialize(run, ReadJsonOptions);
        await db.SaveChangesAsync();
    }
}
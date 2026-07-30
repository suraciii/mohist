using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetAndListAsync_UseProjectScopedIssueNumber()
    {
        var projectA = NewProject("A");
        var projectB = NewProject("B");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, projectA.Id, 42, "Project A issue");
        await SeedIssueAsync(db, projectB.Id, 42, "Project B issue");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await querier.GetAsync(projectA.Id, 42, projectA);
        var list = await querier.ListAsync(projectB.Id, projectB);

        Assert.NotNull(detail);
        Assert.Equal((projectA.Id, 42, "Project A issue"), (detail!.ProjectId, detail.Number, detail.Title));
        var item = Assert.Single(list);
        Assert.Equal((projectB.Id, 42, "Project B issue"), (item.ProjectId, item.Number, item.Title));
    }

    [Fact]
    public async Task ListAsync_FiltersCurrentIssueStateByStatusLabelAndPriority()
    {
        var project = NewProject("filters");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Frontend", "backlog", "p1", ["stream", "frontend"]);
        await SeedIssueAsync(db, project.Id, 2, "Backend", "backlog", "p1", ["stream", "backend"]);
        await SeedIssueAsync(db, project.Id, 3, "Done", "done", "p1", ["stream", "frontend"]);
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var result = await querier.ListAsync(
            project.Id,
            project,
            stage: "backlog",
            label: "stream=frontend",
            priority: "p1");

        var item = Assert.Single(result);
        Assert.Equal((1, "Frontend"), (item.Number, item.Title));
    }

    [Fact]
    public async Task ListAsync_UsesBuiltInWorkflowProfileWhenIssueHasNoSelection()
    {
        var project = NewProject("profile");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Profile");

        var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .ListAsync(project.Id, project));

        Assert.Equal(IssueWorkflowProfiles.LocalId, item.WorkflowProfileId);
    }

    [Fact]
    public async Task Reads_ProjectParentAndChildSummary_AndParentFilter()
    {
        var project = NewProject("parents");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Child one", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 3, "Child two", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 4, "Other");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parent = await querier.GetAsync(project.Id, 1, project);
        var child = await querier.GetAsync(project.Id, 2, project);
        var children = await querier.ListWithLabelFiltersAsync(project.Id, project, null, null, null, null, null, null, 1);
        var empty = await querier.ListWithLabelFiltersAsync(project.Id, project, null, null, null, null, null, null, 99);

        Assert.Equal((1, "Parent"), (child!.ParentIssueRef!.Number, child.ParentIssueRef.Title));
        Assert.Equal((true, 2), (parent!.ChildIssuesSummary!.HasChildren, parent.ChildIssuesSummary.Count));
        Assert.Equal((2, 0, 0, 0), (parent.ChildIssuesSummary.BacklogCount, parent.ChildIssuesSummary.InProgressCount, parent.ChildIssuesSummary.DoneCount, parent.ChildIssuesSummary.CancelledCount));
        Assert.Equal([2, 3], children.Select(issue => issue.Number).ToArray());
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Reads_CanBeParentFromTheAuthoritativeRelationshipRule()
    {
        var project = NewProject("parent-eligibility");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Eligible");
        await SeedIssueAsync(db, project.Id, 2, "Started", workflowRunId: "wr_started");
        await SeedIssueAsync(db, project.Id, 3, "Child", parentIssueNumber: 1);
        var started = db.Issues.Single(row => row.ProjectId == project.Id && row.Number == 2);
        var startedIssue = IssueStore.Deserialize(started.State)!;
        started.State = IssueStore.Serialize(new DomainIssue
        {
            ProjectId = startedIssue.ProjectId,
            Number = startedIssue.Number,
            Title = startedIssue.Title,
            Status = startedIssue.Status,
            Priority = startedIssue.Priority,
            CreatedAt = startedIssue.CreatedAt,
            UpdatedAt = startedIssue.UpdatedAt,
            WorkflowRunId = startedIssue.WorkflowRunId,
            HasWorkflowStarted = true,
            Labels = new Dictionary<string, string>(startedIssue.Labels, StringComparer.Ordinal),
        });
        await db.SaveChangesAsync();

        var issues = await scope.ServiceProvider.GetRequiredService<IssueQuerier>().ListAsync(project.Id, project);

        Assert.True(issues.Single(issue => issue.Number == 1).CanBeParent);
        Assert.False(issues.Single(issue => issue.Number == 2).CanBeParent);
        Assert.False(issues.Single(issue => issue.Number == 3).CanBeParent);
    }

    [Fact]
    public async Task ChildIssuesSummary_ComputesPerStatusBreakdownFromSameGroupBy()
    {
        var project = NewProject("status-breakdown");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Backlog child", status: "backlog", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 3, "Backlog child two", status: "backlog", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 4, "In-progress child", status: "inProgress", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 5, "Done child", status: "done", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 6, "Cancelled child", status: "cancelled", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 7, "Unrelated parent", status: "done");
        await SeedIssueAsync(db, project.Id, 8, "Unrelated child of unrelated parent", status: "done", parentIssueNumber: 7);
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parent = await querier.GetAsync(project.Id, 1, project);
        var unrelated = await querier.GetAsync(project.Id, 7, project);
        var orphanChild = await querier.GetAsync(project.Id, 8, project);

        Assert.NotNull(parent!.ChildIssuesSummary);
        Assert.Equal(5, parent.ChildIssuesSummary.Count);
        Assert.Equal(2, parent.ChildIssuesSummary.BacklogCount);
        Assert.Equal(1, parent.ChildIssuesSummary.InProgressCount);
        Assert.Equal(1, parent.ChildIssuesSummary.DoneCount);
        Assert.Equal(1, parent.ChildIssuesSummary.CancelledCount);

        Assert.NotNull(unrelated!.ChildIssuesSummary);
        Assert.Equal(1, unrelated.ChildIssuesSummary.Count);
        Assert.Equal((0, 0, 1, 0), (unrelated.ChildIssuesSummary.BacklogCount, unrelated.ChildIssuesSummary.InProgressCount, unrelated.ChildIssuesSummary.DoneCount, unrelated.ChildIssuesSummary.CancelledCount));

        Assert.NotNull(orphanChild!.ParentIssueRef);
        Assert.Equal(7, orphanChild.ParentIssueRef.Number);
        Assert.Null(orphanChild.ChildIssuesSummary);
    }

    [Fact]
    public async Task GetIssueRefForWorkflowRunAsync_ReturnsScopedIssueReadContext()
    {
        var project = NewProject("workflow");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 17, "Workflow issue", workflowRunId: "wr_17");

        var issue = await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .GetIssueRefForWorkflowRunAsync("wr_17");

        Assert.NotNull(issue);
        Assert.Equal((project.Id, 17, "Workflow issue"), (issue!.ProjectId, issue.Number, issue.Title));
    }

    [Fact]
    public async Task Children_ReturnsCurrentChildrenInAscendingIssueNumberOrder_WithTitleStatusHealthAndRepository()
    {
        var project = NewProject("children-order");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Backlog child", status: "backlog", parentIssueNumber: 1, repositoryRef: "server");
        await SeedIssueAsync(db, project.Id, 3, "Done child", status: "done", parentIssueNumber: 1, repositoryRef: "web");
        await SeedIssueAsync(db, project.Id, 4, "In-progress child", status: "inProgress", parentIssueNumber: 1, repositoryRef: "server");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parent = await querier.GetAsync(project.Id, 1, project);

        Assert.NotNull(parent);
        Assert.Equal(new[] { 2, 3, 4 }, parent!.Children.Select(c => c.Number).ToArray());
        var backlogChild = parent.Children[0];
        Assert.Equal((2, "Backlog child", "backlog", "active", "server"),
            (backlogChild.Number, backlogChild.Title, backlogChild.Status, backlogChild.Health, backlogChild.RepositoryName));
        var doneChild = parent.Children[1];
        Assert.Equal((3, "Done child", "done", "done", "web"),
            (doneChild.Number, doneChild.Title, doneChild.Status, doneChild.Health, doneChild.RepositoryName));
        var inProgressChild = parent.Children[2];
        Assert.Equal((4, "In-progress child", "in_progress", "active", "server"),
            (inProgressChild.Number, inProgressChild.Title, inProgressChild.Status, inProgressChild.Health, inProgressChild.RepositoryName));
    }

    [Fact]
    public async Task ChildIssuesSummary_DerivesTotalsAndBlockedCount_FromSameBatchedChildQuery()
    {
        var project = NewProject("summary-derived");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Done one", status: "done", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 3, "Done two", status: "done", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 4, "In-progress blocked", status: "inProgress", parentIssueNumber: 1, workflowRunId: "wr_child_blocked_4");
        await SeedIssueAsync(db, project.Id, 5, "In-progress active", status: "inProgress", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 6, "Cancelled", status: "cancelled", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 7, "Backlog", status: "backlog", parentIssueNumber: 1);
        await SeedFailedWorkflowRunAsync(db, "wr_child_blocked_4");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parent = await querier.GetAsync(project.Id, 1, project);

        Assert.NotNull(parent!.ChildIssuesSummary);
        var summary = parent.ChildIssuesSummary!;
        Assert.True(summary.HasChildren);
        Assert.Equal(6, summary.Count);
        Assert.Equal(2, summary.DoneCount);
        Assert.Equal(1, summary.CancelledCount);
        Assert.Equal(2, summary.InProgressCount);
        Assert.Equal(1, summary.BacklogCount);
        Assert.Equal(1, summary.BlockedCount);
    }

    [Fact]
    public async Task Children_ExcludesArchivedChildrenFromRowsAndTotals()
    {
        var project = NewProject("archived-children");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Active child", status: "inProgress", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 3, "Archived child", status: "done", parentIssueNumber: 1, archived: true);
        await SeedIssueAsync(db, project.Id, 4, "Archived blocked child", status: "inProgress", parentIssueNumber: 1, workflowRunId: "wr_archived_blocked_4", archived: true);
        await SeedFailedWorkflowRunAsync(db, "wr_archived_blocked_4");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parent = await querier.GetAsync(project.Id, 1, project);

        Assert.Equal(new[] { 2 }, parent!.Children.Select(c => c.Number).ToArray());
        Assert.Equal(1, parent.ChildIssuesSummary!.Count);
        Assert.Equal(1, parent.ChildIssuesSummary.InProgressCount);
        Assert.Equal(0, parent.ChildIssuesSummary.BlockedCount);
    }

    [Fact]
    public async Task Children_DetachingChild_RemovesFromFormerParentRowsAndTotalsOnNextRead()
    {
        var project = NewProject("detach-child");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Kept child", status: "done", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 3, "Detaching child", status: "inProgress", parentIssueNumber: 1);
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var beforeDetach = await querier.GetAsync(project.Id, 1, project);
        Assert.Equal(new[] { 2, 3 }, beforeDetach!.Children.Select(c => c.Number).ToArray());
        Assert.Equal(2, beforeDetach.ChildIssuesSummary!.Count);

        await DetachChildAsync(db, project.Id, 3);

        var afterDetach = await querier.GetAsync(project.Id, 1, project);
        Assert.Equal(new[] { 2 }, afterDetach!.Children.Select(c => c.Number).ToArray());
        Assert.Equal(1, afterDetach.ChildIssuesSummary!.Count);
        Assert.Equal(1, afterDetach.ChildIssuesSummary.DoneCount);

        var detached = await querier.GetAsync(project.Id, 3, project);
        Assert.Null(detached!.ParentIssueRef);
        Assert.Empty(detached.Children);
    }

    [Fact]
    public async Task Children_HealthChanges_UpdateBlockedCountWithoutRewritingParentState()
    {
        var project = NewProject("health-changes");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Toggling child", status: "inProgress", parentIssueNumber: 1, workflowRunId: "wr_toggle_2");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        await SeedFailedWorkflowRunAsync(db, "wr_toggle_2");
        var blocked = await querier.GetAsync(project.Id, 1, project);
        Assert.Equal(1, blocked!.ChildIssuesSummary!.BlockedCount);
        Assert.Equal("blocked", blocked.Children[0].Health);

        var completedState =
            "{\"Id\":\"wr_toggle_2\",\"Metadata\":{\"CreatedAt\":\"2026-07-19T00:00:00Z\",\"Name\":\"test\"},\"Status\":\"Completed\",\"CurrentStageId\":\"build\",\"Stages\":[{\"Id\":\"build\",\"Attempt\":1,\"RequiresApproval\":false,\"Initialized\":true,\"Status\":\"Completed\",\"Tasks\":[],\"Checks\":[]}],\"Assignment\":null,\"Feedback\":[]}";
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE WorkflowRuns SET State = {0} WHERE WorkflowRunId = {1}",
            completedState,
            "wr_toggle_2");

        var active = await querier.GetAsync(project.Id, 1, project);
        Assert.Equal(0, active!.ChildIssuesSummary!.BlockedCount);
        Assert.Equal("active", active.Children[0].Health);
    }

    [Fact]
    public async Task Children_DoesNotLeakAcrossProjects()
    {
        var projectA = NewProject("isolation-a");
        var projectB = NewProject("isolation-b");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, projectA.Id, 1, "Parent A");
        await SeedIssueAsync(db, projectA.Id, 2, "Child A", status: "done", parentIssueNumber: 1);
        await SeedIssueAsync(db, projectB.Id, 1, "Parent B");
        await SeedIssueAsync(db, projectB.Id, 2, "Child B", status: "backlog", parentIssueNumber: 1);
        await SeedIssueAsync(db, projectB.Id, 3, "Child B2", status: "inProgress", parentIssueNumber: 1);
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parentA = await querier.GetAsync(projectA.Id, 1, projectA);
        var parentB = await querier.GetAsync(projectB.Id, 1, projectB);

        Assert.Equal(new[] { 2 }, parentA!.Children.Select(c => c.Number).ToArray());
        Assert.Equal(1, parentA.ChildIssuesSummary!.Count);
        Assert.Equal(new[] { 2, 3 }, parentB!.Children.Select(c => c.Number).ToArray());
        Assert.Equal(2, parentB.ChildIssuesSummary!.Count);
    }

    [Fact]
    public async Task ListAsync_AddsChildrenRowsForEveryParentInResultSet()
    {
        var project = NewProject("list-with-children");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent one");
        await SeedIssueAsync(db, project.Id, 2, "Parent two");
        await SeedIssueAsync(db, project.Id, 3, "Child of one", status: "done", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 4, "Child of two", status: "inProgress", parentIssueNumber: 2);
        await SeedIssueAsync(db, project.Id, 5, "Orphan");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await querier.ListAsync(project.Id, project);
        var byNumber = list.ToDictionary(i => i.Number);

        Assert.Equal(new[] { 3 }, byNumber[1].Children.Select(c => c.Number).ToArray());
        Assert.Equal(new[] { 4 }, byNumber[2].Children.Select(c => c.Number).ToArray());
        Assert.Empty(byNumber[5].Children);
        Assert.Null(byNumber[5].ChildIssuesSummary);
    }

    [Fact]
    public async Task ListAsync_ChildIssuesKeepExplicitAndProjectDefaultCustomProfiles()
    {
        var project = NewProject("child-custom-profiles");
        const string explicitProfileId = "custom/child-explicit";
        const string defaultProfileId = "custom/child-default";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.WorkflowProfileRecords.AddRange(
            CustomProfile(project.Id, explicitProfileId),
            CustomProfile(project.Id, defaultProfileId));
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = project.Id,
            DefaultWorkflowProfileId = defaultProfileId,
            DefaultWorkflowProfileIdKey = defaultProfileId,
        });
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Explicit custom child", parentIssueNumber: 1, workflowProfileId: explicitProfileId);
        await SeedIssueAsync(db, project.Id, 3, "Default custom child", parentIssueNumber: 1);

        var issues = await scope.ServiceProvider.GetRequiredService<IssueQuerier>().ListAsync(project.Id, project);
        var byNumber = issues.ToDictionary(issue => issue.Number);

        Assert.Equal([2, 3], byNumber[1].Children.Select(child => child.Number).ToArray());
        Assert.Equal(explicitProfileId, byNumber[2].WorkflowProfileId);
        Assert.Equal(defaultProfileId, byNumber[3].WorkflowProfileId);
    }

    [Fact]
    public async Task Children_OrdinaryIssueReturnsNoChildRows()
    {
        var project = NewProject("ordinary");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Standalone");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var issue = await querier.GetAsync(project.Id, 1, project);

        Assert.Empty(issue!.Children);
        Assert.Null(issue.ChildIssuesSummary);
    }

    private static ProjectInfo NewProject(string name) => new()
    {
        Id = $"proj-query-{name}-{Guid.NewGuid():N}",
        Name = name,
    };

    private static async Task SeedIssueAsync(
        MohistDbContext db,
        string projectId,
        int number,
        string title,
        string status = "backlog",
        string priority = "p2",
        string[]? label = null,
        string? workflowRunId = null,
        int? parentIssueNumber = null,
        string? repositoryRef = null,
        bool archived = false,
        string? workflowProfileId = null)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = Enum.Parse<IssueStatus>(status, ignoreCase: true),
            Priority = priority,
            WorkflowRunId = workflowRunId,
            ParentIssueNumber = parentIssueNumber,
            RepositoryRef = repositoryRef,
            WorkflowProfileId = workflowProfileId,
            ArchivedAt = archived ? TestTime.UtcDateTime : null,
            Labels = label is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { [label[0]] = label[1] },
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = issue.ProjectId,
            Number = issue.Number,
            State = IssueStore.Serialize(issue),
            ParentIssueNumber = issue.ParentIssueNumber,
        });
        await db.SaveChangesAsync();
    }

    private static WorkflowProfileRecordRow CustomProfile(string projectId, string profileId) => new()
    {
        ProjectId = projectId,
        ProfileId = profileId,
        Name = profileId,
        DefinitionSource = "stages: []\n",
    };

    private static async Task DetachChildAsync(
        MohistDbContext db,
        string projectId,
        int childNumber)
    {
        var tracked = db.Issues.First(r => r.ProjectId == projectId && r.Number == childNumber);
        var state = IssueStore.Deserialize(tracked.State)
            ?? throw new InvalidOperationException($"Issue #{childNumber} state could not be deserialized");
        var detached = new DomainIssue
        {
            ProjectId = state.ProjectId,
            Number = state.Number,
            Title = state.Title,
            Body = state.Body,
            Status = state.Status,
            Priority = state.Priority,
            Risk = state.Risk,
            CreatedAt = state.CreatedAt,
            UpdatedAt = TestTime.UtcDateTime,
            ArchivedAt = state.ArchivedAt,
            CompletedAt = state.CompletedAt,
            WorkflowRunId = state.WorkflowRunId,
            ParentIssueNumber = null,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        tracked.State = IssueStore.Serialize(detached);
        tracked.ParentIssueNumber = null;
        await db.SaveChangesAsync();
    }

    private static async Task SeedFailedWorkflowRunAsync(
        MohistDbContext db,
        string workflowRunId)
    {
        var state =
            "{\"Id\":\"" + workflowRunId + "\",\"Metadata\":{\"CreatedAt\":\"2026-07-19T00:00:00Z\",\"Name\":\"test\"},\"Status\":\"Failed\",\"CurrentStageId\":\"build\",\"Stages\":[{\"Id\":\"build\",\"Attempt\":1,\"RequiresApproval\":false,\"Initialized\":true,\"Status\":\"Failed\",\"Tasks\":[],\"Checks\":[],\"Failure\":{\"Reason\":\"TaskFailed\",\"Stage\":\"build\",\"Message\":\"blocked\"}}],\"Failure\":{\"Reason\":\"TaskFailed\",\"Stage\":\"build\",\"Message\":\"blocked\"},\"Assignment\":null,\"Feedback\":[]}";
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId,
            state);
    }
}

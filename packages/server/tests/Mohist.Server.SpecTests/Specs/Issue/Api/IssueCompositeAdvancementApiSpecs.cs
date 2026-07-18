using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// End-to-end API specs for compound advancement (issue-419 T-002). Covers:
/// <list type="bullet">
/// <item><c>POST /api/projects/{p}/issues/{n}/start</c> on a parent succeeds
///   (no <c>is_parent</c> blocker) and aggregates the parent to
///   <c>InProgress</c>.</item>
/// <item>Starting a parent with zero startable children still succeeds;
///   children stay Backlog until a blocker clears (next recompute).</item>
/// <item>The start fan-out creates one workflow run per startable child,
///   concurrently.</item>
/// <item>Detaching the last child reverts the parent to a normal issue;
///   subsequent <c>/start</c> on the now-empty parent runs the per-issue
///   start path and mints its own workflow run.</item>
/// <item>The recompute is idempotent: re-running it converges without
///   duplicate <c>IssueCompositeStatusChanged</c> events.</item>
/// </list>
/// Spec:
/// <c>openspec/changes/issue-419/specs/compound-advancement/spec.md</c>.
/// </summary>
[Collection("IssueLifecycle")]
public class IssueCompositeAdvancementApiSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IssueCompositeAdvancementApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task StartIssue_OnParentWithChildren_AggregatesToInProgress_AndStartsEachChild()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var children = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var child = await CreateIssueAsync(projectId, $"Child {i}", isDraft: false);
            await AttachChildAsync(projectId, child, parent.Number);
            children.Add(child.Number);
        }

        using var startResponse = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        var parentView = await GetIssueReadModelAsync(projectId, parent.Number);
        Assert.NotNull(parentView);
        // Parent's aggregated status is InProgress via composite start.
        Assert.Equal("in_progress", parentView!.Status);
        // The parent never acquires a workflow run (composite advancement).
        Assert.Null(parentView.WorkflowRunId);

        // After dispatching the work-started events, the children start
        // workflow runs in parallel; the parents own no run.
        await DispatchEventsAsync();
        foreach (var childNumber in children)
        {
            var childView = await GetIssueReadModelAsync(projectId, childNumber);
            Assert.NotNull(childView);
            Assert.Equal("in_progress", childView!.Status);
            Assert.NotNull(childView.WorkflowRunId);
        }
    }

    [Fact]
    public async Task StartIssue_OnParentWithAllChildrenBlocked_StillAggregatesToInProgress()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var prereq = await CreateIssueAsync(projectId, "Prereq", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);
        await AddPrerequisiteAsync(projectId, child.Number, prereq.Number);

        using var startResponse = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        var parentView = await GetIssueReadModelAsync(projectId, parent.Number);
        Assert.NotNull(parentView);
        // Parent still flips to InProgress even though no child can start.
        Assert.Equal("in_progress", parentView!.Status);
        Assert.Null(parentView.WorkflowRunId);

        // Child stays Backlog: prereq not done, no workflow run yet.
        var childView = await GetIssueReadModelAsync(projectId, child.Number);
        Assert.NotNull(childView);
        Assert.Equal("backlog", childView!.Status);
        Assert.Null(childView.WorkflowRunId);
    }

    [Fact]
    public async Task StartIssue_OnIssueWithoutChildren_RunsPerIssueStartPath()
    {
        var projectId = await CreateProjectAsync();
        var issue = await CreateIssueAsync(projectId, "Solo", isDraft: false);

        using var startResponse = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        await DispatchEventsAsync();
        var view = await GetIssueReadModelAsync(projectId, issue.Number);
        Assert.NotNull(view);
        Assert.Equal("in_progress", view!.Status);
        // Per-issue start: the issue itself owns the workflow run.
        Assert.NotNull(view.WorkflowRunId);
    }

    [Fact]
    public async Task GetStartReadiness_OnParentWithChildren_ReturnsCanStartAndNoBlocker()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);

        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{parent.Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Read model no longer surfaces the ParentHasChildren blocker.
        Assert.DoesNotContain("is_parent", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parent-has-children", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetachingLastChild_RevertsParentToNormalIssue_OnNextStart()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);

        // Compose first — start the parent to mark it composite-InProgress.
        using (var firstStart = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/start", null))
        {
            Assert.Equal(HttpStatusCode.OK, firstStart.StatusCode);
        }

        // Detach the (only) child. The recompute driven by the
        // parent-changed event finds an empty snapshot and turns the
        // parent back into a normal issue (no composite state).
        await DetachChildAsync(projectId, child.Number);

        // Re-start: now the parent is a normal issue, so its own start
        // creates a workflow run.
        using var secondStart = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, secondStart.StatusCode);

        await DispatchEventsAsync();
        var view = await GetIssueReadModelAsync(projectId, parent.Number);
        Assert.NotNull(view);
        // The parent now owns its own workflow run, like a normal issue.
        Assert.NotNull(view!.WorkflowRunId);
    }

    [Fact]
    public async Task RecomputeCompositeStatusAsync_RedeliveryIsIdempotent()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);

        // Pre-warm: start the parent so it transitions to InProgress.
        await _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, parent.Number))).StartWorkAsync();
        await DispatchEventsAsync();

        // Drive two recomputes back-to-back; the second must not emit
        // another IssueCompositeStarted event (already in InProgress).
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        await grain.RecomputeCompositeStatusAsync();
        await grain.RecomputeCompositeStatusAsync();

        var events = await LoadIssueEventsAsync(projectId, parent.Number);
        var compositeStartedCount = events.Count(e =>
            string.Equals(e.Envelope.Type, EventCatalog.ReverseDns.IssueCompositeStarted, StringComparison.Ordinal));
        Assert.Equal(1, compositeStartedCount);
    }

    [Fact]
    public async Task CloseParent_WithNonTerminalChild_ReturnsTypedConflict_WithoutCascade()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/close", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("parent_has_non_terminal_children", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("backlog", (await GetIssueReadModelAsync(projectId, child.Number))!.Status);
    }

    [Fact]
    public async Task ReopenParent_ReturnsToBacklog_AndCanAttachAndStartNewChild()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var existing = await CreateIssueAsync(projectId, "Existing", isDraft: false);
        await AttachChildAsync(projectId, existing, parent.Number);
        await _grains.GetGrain<IIssueGrain>(existing.IssueKey).CancelAsync();
        await _grains.GetGrain<IIssueGrain>(parent.IssueKey).RecomputeCompositeStatusAsync();

        using (var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/reopen", null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal("backlog", (await GetIssueReadModelAsync(projectId, parent.Number))!.Status);
        Assert.Equal("cancelled", (await GetIssueReadModelAsync(projectId, existing.Number))!.Status);

        var added = await CreateIssueAsync(projectId, "Added", isDraft: false);
        await AttachChildAsync(projectId, added, parent.Number);
        using var start = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("in_progress", (await GetIssueReadModelAsync(projectId, added.Number))!.Status);
    }

    [Fact]
    public async Task ArchiveParent_CascadesToDoneAndCancelledChildren()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var done = await CreateIssueAsync(projectId, "Done", isDraft: false);
        var cancelled = await CreateIssueAsync(projectId, "Cancelled", isDraft: false);
        await AttachChildAsync(projectId, done, parent.Number);
        await AttachChildAsync(projectId, cancelled, parent.Number);
        var parentGrain = _grains.GetGrain<IIssueGrain>(parent.IssueKey);
        var doneGrain = _grains.GetGrain<IIssueGrain>(done.IssueKey);
        var cancelledGrain = _grains.GetGrain<IIssueGrain>(cancelled.IssueKey);
        await parentGrain.StartCompositeAsync();
        await doneGrain.CompleteWorkAsync((await doneGrain.GetActiveWorkflowRunIdAsync())!);
        var cancelledWorkflowRunId = (await cancelledGrain.GetActiveWorkflowRunIdAsync())!;
        await _grains.GetGrain<IWorkflowGrain>(cancelledWorkflowRunId).StopAsync("test-cancel");
        await cancelledGrain.CancelAsync();
        await parentGrain.RecomputeCompositeStatusAsync();

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{parent.Number}/archive", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await GetIssueReadModelAsync(projectId, parent.Number))!.ArchivedAt);
        Assert.NotNull((await GetIssueReadModelAsync(projectId, done.Number))!.ArchivedAt);
        Assert.NotNull((await GetIssueReadModelAsync(projectId, cancelled.Number))!.ArchivedAt);
    }

    [Fact]
    public async Task DetachingDoneChild_FromDoneParent_RecomputesAgainstRemainingChild()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var first = await CreateIssueAsync(projectId, "First", isDraft: false);
        var second = await CreateIssueAsync(projectId, "Second", isDraft: false);
        await AttachChildAsync(projectId, first, parent.Number);
        await AttachChildAsync(projectId, second, parent.Number);
        var parentGrain = _grains.GetGrain<IIssueGrain>(parent.IssueKey);
        var firstGrain = _grains.GetGrain<IIssueGrain>(first.IssueKey);
        var secondGrain = _grains.GetGrain<IIssueGrain>(second.IssueKey);
        await parentGrain.StartCompositeAsync();
        await firstGrain.CompleteWorkAsync((await firstGrain.GetActiveWorkflowRunIdAsync())!);
        await secondGrain.CompleteWorkAsync((await secondGrain.GetActiveWorkflowRunIdAsync())!);
        await parentGrain.RecomputeCompositeStatusAsync();

        await DetachChildAsync(projectId, second.Number);

        Assert.Equal("done", (await GetIssueReadModelAsync(projectId, parent.Number))!.Status);
    }

    private async Task<string> CreateProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync($"mohist-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist-local.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return id;
    }

    private async Task<(int Number, string IssueKey)> CreateIssueAsync(string projectId, string title, bool isDraft)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, title, null, null, null, isDraft: isDraft);
        return (number, issueKey);
    }

    private async Task AttachChildAsync(string projectId, (int Number, string IssueKey) child, int parentNumber)
    {
        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{child.Number}",
            new { parentIssueNumber = parentNumber },
            JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private async Task DetachChildAsync(string projectId, int childNumber)
    {
        using var content = JsonContent.Create(new { parentIssueNumber = (int?)null }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var response = await _client.PatchAsync(
            $"/api/projects/{projectId}/issues/{childNumber}",
            content);
        response.EnsureSuccessStatusCode();
        await DispatchEventsAsync();
    }

    private async Task AddPrerequisiteAsync(string projectId, int dependent, int prereq)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{dependent}/prerequisites",
            new { prerequisiteNumber = prereq },
            JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private async Task DispatchEventsAsync()
    {
        await _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
    }

    private async Task<IssueReadModel?> GetIssueReadModelAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        var project = await projectGrain.GetAsync();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetAsync(projectId, number, project);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> LoadIssueEventsAsync(string projectId, int issueNumber)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        return await store.ListIssueEventsAsync(projectId, issueNumber, 200);
    }
}

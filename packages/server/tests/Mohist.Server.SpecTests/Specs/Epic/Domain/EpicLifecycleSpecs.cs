using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicLifecycleSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public EpicLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Fact]
    public async Task MarkDone_WhenOpenLinkedIssuesRemain_Returns4xxAndLeavesStatusUnchanged()
    {
        var project = await CreateProjectAsync();
        var delivered = await CreateIssueAsync(project.Id, "Delivered");
        await CompleteIssueAsync(project.Id, delivered);
        var pending = await CreateIssueAsync(project.Id, "Pending");
        var epic = await CreateEpicAsync(project.Id, "Lifecycle rejection");
        await LinkIssueAsync(project.Id, epic.Number, delivered.Number);
        var done = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("done", done.Status);
        await LinkIssueAsync(project.Id, epic.Number, pending.Number);

        var running = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("running", running.Status);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Number}/done", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_NOT_READY_TO_MARK_DONE", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal(1, envelope.Details!.OpenLinkedCount);

        var after = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("running", after.Status);
    }

    [Fact]
    public async Task MarkDone_WithMixedDoneAndCancelledLinkedIssues_SucceedsAndDeliveredCountCountsOnlyDone()
    {
        // Epic #18 scenario: at least one done issue and at least one
        // cancelled issue; all linked issues are terminal so the epic
        // is ready to Mark Done. deliveredCount counts only the done
        // issue.
        var project = await CreateProjectAsync();
        var done = await CreateIssueAsync(project.Id, "Done");
        var cancelled = await CreateIssueAsync(project.Id, "Cancelled");
        var epic = await CreateEpicAsync(project.Id, "Mixed done+cancelled epic");
        await LinkIssueAsync(project.Id, epic.Number, done.Number);
        await LinkIssueAsync(project.Id, epic.Number, cancelled.Number);
        await CompleteIssueAsync(project.Id, done);
        await CancelIssueAsync(project.Id, cancelled);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");

        Assert.Equal("done", detail.Status);
        Assert.True(detail.Progress.ReadyToMarkDone);
        Assert.Equal(1, detail.Progress.DeliveredCount);
        Assert.Equal(2, detail.Progress.TotalIssueCount);
    }

    [Fact]
    public async Task MarkDone_RepeatedOnDoneEpic_ReturnsAlreadyTerminalEnvelope()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Delivered");
        var epic = await CreateEpicAsync(project.Id, "Repeated done");
        await LinkIssueAsync(project.Id, epic.Number, issue.Number);
        await CompleteIssueAsync(project.Id, issue);
        var done = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("done", done.Status);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Number}/done", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("done", envelope.Details!.CurrentStatus);
        Assert.Equal("done", envelope.Details.RequestedStatus);

        var after = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("done", after.Status);
    }

    [Fact]
    public async Task Close_SetsStatusToClosedAndRetainsEpicIssueLinks()
    {
        // Issue-179: closing an epic is non-destructive — the linked-issue
        // membership set is preserved so progress and history remain readable
        // post-close (see spec epic-issue-membership#Membership retained
        // across epic close and epic-lifecycle#Close is non-destructive).
        var project = await CreateProjectAsync();
        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");
        var epic = await CreateEpicAsync(project.Id, "Container");
        await LinkIssueAsync(project.Id, epic.Number, first.Number);
        await LinkIssueAsync(project.Id, epic.Number, second.Number);

        var closed = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}/close", null);

        Assert.Equal("closed", closed.Status);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("closed", detail.Status);
        Assert.Equal(2, detail.LinkedIssues.Length);
        Assert.Contains(detail.LinkedIssues, i => i.Number == first.Number);
        Assert.Contains(detail.LinkedIssues, i => i.Number == second.Number);
        Assert.Equal(2, detail.Progress.TotalIssueCount);
        Assert.Equal(0, detail.Progress.DeliveredCount);
        Assert.False(detail.Progress.ReadyToMarkDone);
    }

    [Fact]
    public async Task Close_DoesNotChangeIssueStatusOrPrerequisitesOrWorkflow()
    {
        var project = await CreateProjectAsync();
        var blocker = await CreateIssueAsync(project.Id, "Blocker");
        await CompleteIssueAsync(project.Id, blocker);
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, dependent.Number)));
        await dependentGrain.AddPrerequisiteAsync(blocker.Number);

        var epic = await CreateEpicAsync(project.Id, "Container with deps");
        await LinkIssueAsync(project.Id, epic.Number, dependent.Number);
        var issueDetailBefore = await GetIssueInfoAsync(project.Id, dependent.Number);

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/close", null);

        var issueDetailAfter = await GetIssueInfoAsync(project.Id, dependent.Number);
        Assert.Equal(issueDetailBefore!.Status, issueDetailAfter!.Status);
        Assert.Equal(issueDetailBefore.PrerequisiteNumbers, issueDetailAfter.PrerequisiteNumbers);
        Assert.Equal(issueDetailBefore.Health, issueDetailAfter.Health);
        Assert.Equal(issueDetailBefore.WorkflowRunId, issueDetailAfter.WorkflowRunId);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("closed", detail.Status);
        // Issue-179: close is non-destructive — the link to the dependent
        // issue remains after close, so the linked-issue set is non-empty.
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(dependent.Number, detail.LinkedIssues[0].Number);
    }

    [Fact]
    public async Task UnlinkIssue_ByIssueNumber_RemovesMembershipAndDoesNotSilentNoOp()
    {
        var project = await CreateProjectAsync();
        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");
        var epic = await CreateEpicAsync(project.Id, "Unlink by number");
        await LinkIssueAsync(project.Id, epic.Number, first.Number);
        await LinkIssueAsync(project.Id, epic.Number, second.Number);

        // Regression: unlink by issue NUMBER (not internal id) must actually remove
        // the link. Previously the DELETE endpoint passed the number straight to
        // UnlinkIssueAsync, which matches on internal id — so it returned 200 but
        // changed nothing.
        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues/{first.Number}");

        response.EnsureSuccessStatusCode();

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(second.Number, detail.LinkedIssues[0].Number);
    }

    [Fact]
    public async Task MarkDone_AllDelivered_LinkTimeRecomputeAutoTransitionsToDone()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Delivered");
        await CompleteIssueAsync(project.Id, issue);
        var epic = await CreateEpicAsync(project.Id, "Auto complete on link");
        await LinkIssueAsync(project.Id, epic.Number, issue.Number);

        var afterLink = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("done", afterLink.Status);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.True(detail.Progress.ReadyToMarkDone);
        Assert.Equal(1, detail.Progress.DeliveredCount);
        Assert.Equal(1, detail.Progress.TotalIssueCount);
    }

    [Fact]
    public async Task EpicDetail_LinkedIssue_ExposesPrerequisiteNumbers()
    {
        var project = await CreateProjectAsync();
        var upstream = await CreateIssueAsync(project.Id, "Upstream");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, dependent.Number)));
        await dependentGrain.AddPrerequisiteAsync(upstream.Number);

        var epic = await CreateEpicAsync(project.Id, "Prereq numbers");
        await LinkIssueAsync(project.Id, epic.Number, upstream.Number);
        await LinkIssueAsync(project.Id, epic.Number, dependent.Number);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");

        var dependentRow = detail.LinkedIssues.Single(i => i.Number == dependent.Number);
        Assert.Equal(new[] { upstream.Number }, dependentRow.PrerequisiteNumbers);

        var upstreamRow = detail.LinkedIssues.Single(i => i.Number == upstream.Number);
        Assert.Empty(upstreamRow.PrerequisiteNumbers);
    }

    [Fact]
    public async Task EpicDetail_LinkedIssue_ExposesExternalPrerequisitesSummary()
    {
        var project = await CreateProjectAsync();
        var externalUpstream = await CreateIssueAsync(project.Id, "External upstream");
        var memberDependent = await CreateIssueAsync(project.Id, "Member dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, memberDependent.Number)));
        await dependentGrain.AddPrerequisiteAsync(externalUpstream.Number);

        var epic = await CreateEpicAsync(project.Id, "External prereq summary");
        await LinkIssueAsync(project.Id, epic.Number, memberDependent.Number);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");

        var dependentRow = detail.LinkedIssues.Single(i => i.Number == memberDependent.Number);
        var ghost = Assert.Single(dependentRow.ExternalPrerequisites);
        Assert.Equal(externalUpstream.Number, ghost.Number);
        Assert.Equal("External upstream", ghost.Title);
        Assert.False(string.IsNullOrEmpty(ghost.Stage));
        Assert.False(string.IsNullOrEmpty(ghost.Status));
    }

    [Fact]
    public async Task EpicDetail_LinkedIssue_InternalPrerequisiteIsNotExposedAsExternal()
    {
        var project = await CreateProjectAsync();
        var upstream = await CreateIssueAsync(project.Id, "Internal upstream");
        var dependent = await CreateIssueAsync(project.Id, "Internal dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, dependent.Number)));
        await dependentGrain.AddPrerequisiteAsync(upstream.Number);

        var epic = await CreateEpicAsync(project.Id, "Internal prereq");
        await LinkIssueAsync(project.Id, epic.Number, upstream.Number);
        await LinkIssueAsync(project.Id, epic.Number, dependent.Number);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");

        var dependentRow = detail.LinkedIssues.Single(i => i.Number == dependent.Number);
        Assert.Equal(new[] { upstream.Number }, dependentRow.PrerequisiteNumbers);
        Assert.Empty(dependentRow.ExternalPrerequisites);
    }

    [Fact]
    public async Task Reopen_OnClosedEpic_ReturnsIdleEpic()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Reopen me");
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/close", null);

        var reopened = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}/reopen", null);

        Assert.Equal("idle", reopened.Status);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("idle", detail.Status);
    }

    [Fact]
    public async Task Reopen_OnIdleEpic_Returns409EpIcNotTerminal()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Idle epic");

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Number}/reopen", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_NOT_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("idle", envelope.Details!.CurrentStatus);

        var after = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("idle", after.Status);
    }

    [Fact]
    public async Task Reopen_OnMissingEpic_Returns404()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/999/reopen", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reopen_OnClosedEpic_WithLinkedIssues_PreservesMembershipsAndUnblocksAdvancement()
    {
        // After reopen, the linked issues are still linked (close
        // never destroyed them); reopen re-establishes the active
        // membership so EpicQuerier surfaces them again as part of
        // the epic's progress.
        var project = await CreateProjectAsync();
        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");
        var epic = await CreateEpicAsync(project.Id, "Reopen preserves links");
        await LinkIssueAsync(project.Id, epic.Number, first.Number);
        await LinkIssueAsync(project.Id, epic.Number, second.Number);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/close", null);

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/reopen", null);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("idle", detail.Status);
        Assert.Equal(2, detail.LinkedIssues.Length);
        Assert.Equal(2, detail.Progress.TotalIssueCount);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-life-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return project;
    }

    private async Task<IssueDto> CreateIssueAsync(string projectId, string title)
    {
        return await _client.PostDataAsync<IssueDto>($"/api/projects/{projectId}/issues", new { title, isDraft = false });
    }

    private async Task CompleteIssueAsync(string projectId, IssueDto issueInfo)
    {
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueInfo.Number)));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(projectId, "Lifecycle Test", RepositoryBaseBranch: "main"));
        await grain.CompleteWorkAsync(wrId);
        await RecomputeOwningEpicAsync(projectId, issueInfo.Number);
    }

    private async Task CancelIssueAsync(string projectId, IssueDto issueInfo)
    {
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueInfo.Number)));
        await grain.CancelAsync();
        await RecomputeOwningEpicAsync(projectId, issueInfo.Number);
    }

    private async Task<EpicDto> CreateEpicAsync(string projectId, string title)
    {
        return await _client.PostDataAsync<EpicDto>($"/api/projects/{projectId}/epics", new { title, description = "lifecycle test", priority = "p2" });
    }

    private async Task LinkIssueAsync(string projectId, int epicNumber, int issueNumber)
    {
        await _client.PostOkAsync($"/api/projects/{projectId}/epics/{epicNumber}/issues", new { issueNumber });
        await _grains
            .GetGrain<IEpicGrain>(GrainKey.Epic(new EpicKey(projectId, epicNumber)))
            .RecomputeProgressAsync();
    }

    private async Task RecomputeOwningEpicAsync(string projectId, int issueNumber)
    {
        using var scope = _services.CreateScope();
        var epicNumber = await scope.ServiceProvider
            .GetRequiredService<EpicQuerier>()
            .GetEpicNumberForIssueAsync(projectId, issueNumber);
        if (epicNumber is null)
            return;

        await _grains
            .GetGrain<IEpicGrain>(GrainKey.Epic(new EpicKey(projectId, epicNumber.Value)))
            .RecomputeProgressAsync();
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetInfoAsync(projectId, number);
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, int[] PrerequisiteNumbers, string Status, string Health, string? WorkflowRunId);
    private sealed record EpicDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record EpicDetailDto(string Status, LinkedIssueDto[] LinkedIssues, EpicProgressDto Progress);
    private sealed record LinkedIssueDto(int Number, string Title, string Status, string Stage, string Health, string? Priority, bool CanStart = false, StartBlockerDto? StartBlocker = null, int[] PrerequisiteNumbers = null!, ExternalPrerequisiteDto[] ExternalPrerequisites = null!);
    private sealed record ExternalPrerequisiteDto(int Number, string Title = "", string Stage = "", string Status = "");
    private sealed record StartBlockerDto(string Kind);
    private sealed record EpicProgressDto(int DeliveredCount, int TotalIssueCount, EpicProgressIssueDto[] BlockedIssues, EpicProgressIssueDto[] ActiveIssues, EpicNextIssueDto? NextIssue, string? NextIssueReason, bool ReadyToMarkDone);
    private sealed record EpicProgressIssueDto(int Number, string Title, string Health);
    private sealed record EpicNextIssueDto(int Number, string Title);
    private sealed record ConflictEnvelope(bool Success, string? Code = null, string? Error = null, ConflictDetailsDto? Details = null);
    private sealed record ConflictDetailsDto(string CurrentStatus, string RequestedStatus, [property: System.Text.Json.Serialization.JsonPropertyName("openCount")] int OpenLinkedCount);
}

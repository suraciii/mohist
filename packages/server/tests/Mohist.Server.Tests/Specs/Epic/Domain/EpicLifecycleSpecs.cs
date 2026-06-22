using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Epic.Domain;

[Collection("MohistIntegration")]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task MarkDone_WhenNotAllLinkedIssuesDelivered_Returns4xxAndLeavesStatusUnchanged()
    {
        var project = await CreateProjectAsync();
        var delivered = await CreateIssueAsync(project.Id, "Delivered");
        await CompleteIssueAsync(project.Id, delivered);
        var pending = await CreateIssueAsync(project.Id, "Pending");
        var epic = await CreateEpicAsync(project.Id, "Lifecycle rejection");
        await LinkIssueAsync(project.Id, epic.Id, delivered.Number);
        await LinkIssueAsync(project.Id, epic.Id, pending.Number);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Id}/done", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_NOT_READY_TO_MARK_DONE", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal(1, envelope.Details!.UndeliveredCount);

        var after = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Equal("active", after.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task MarkDone_WhenAllLinkedIssuesDelivered_SucceedsAndChangesStatusToDone()
    {
        var project = await CreateProjectAsync();
        var first = await CreateIssueAsync(project.Id, "First");
        await CompleteIssueAsync(project.Id, first);
        var second = await CreateIssueAsync(project.Id, "Second");
        await CompleteIssueAsync(project.Id, second);
        var epic = await CreateEpicAsync(project.Id, "Lifecycle success");
        await LinkIssueAsync(project.Id, epic.Id, first.Number);
        await LinkIssueAsync(project.Id, epic.Id, second.Number);

        var marked = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Id}/done", null);

        Assert.Equal("done", marked.Status);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Equal("done", detail.Status);
        Assert.Equal(2, detail.Progress.DeliveredCount);
        Assert.Equal(2, detail.Progress.TotalIssueCount);
        Assert.True(detail.Progress.ReadyToMarkDone);
        Assert.Equal(2, detail.LinkedIssues.Length);
        Assert.Equal(first.Id, detail.LinkedIssues[0].Id);
        Assert.Equal("done", detail.LinkedIssues[0].Status);
        Assert.Equal(second.Id, detail.LinkedIssues[1].Id);
        Assert.Equal("done", detail.LinkedIssues[1].Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task MarkDone_RepeatedOnDoneEpic_ReturnsAlreadyTerminalEnvelope()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Delivered");
        await CompleteIssueAsync(project.Id, issue);
        var epic = await CreateEpicAsync(project.Id, "Repeated done");
        await LinkIssueAsync(project.Id, epic.Id, issue.Number);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/done", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Id}/done", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("done", envelope.Details!.CurrentStatus);
        Assert.Equal("done", envelope.Details.RequestedStatus);

        var after = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Equal("done", after.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task MarkDone_OnClosedEpic_ReturnsAlreadyTerminalEnvelope()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Linked");
        var epic = await CreateEpicAsync(project.Id, "Closed epic");
        await LinkIssueAsync(project.Id, epic.Id, issue.Number);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/close", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Id}/done", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("closed", envelope.Details!.CurrentStatus);
        Assert.Equal("done", envelope.Details.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Close_RepeatedOnClosedEpic_ReturnsAlreadyTerminalEnvelope()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Close twice");

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/close", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Id}/close", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("closed", envelope.Details!.CurrentStatus);
        Assert.Equal("closed", envelope.Details.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Close_RepeatedOnDoneEpic_ReturnsAlreadyTerminalEnvelope()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Delivered");
        await CompleteIssueAsync(project.Id, issue);
        var epic = await CreateEpicAsync(project.Id, "Done then close");
        await LinkIssueAsync(project.Id, epic.Id, issue.Number);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/done", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{epic.Id}/close", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("done", envelope.Details!.CurrentStatus);
        Assert.Equal("closed", envelope.Details.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Close_SetsStatusToClosedAndRemovesEpicIssueLinks()
    {
        var project = await CreateProjectAsync();
        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");
        var epic = await CreateEpicAsync(project.Id, "Container");
        await LinkIssueAsync(project.Id, epic.Id, first.Number);
        await LinkIssueAsync(project.Id, epic.Id, second.Number);

        var closed = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Id}/close", null);

        Assert.Equal("closed", closed.Status);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Equal("closed", detail.Status);
        Assert.Empty(detail.LinkedIssues);
        Assert.Equal(0, detail.Progress.TotalIssueCount);
        Assert.Equal(0, detail.Progress.DeliveredCount);
        Assert.False(detail.Progress.ReadyToMarkDone);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Close_DoesNotChangeIssueStatusOrPrerequisitesOrWorkflow()
    {
        var project = await CreateProjectAsync();
        var blocker = await CreateIssueAsync(project.Id, "Blocker");
        await CompleteIssueAsync(project.Id, blocker);
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(dependent.Id);
        await dependentGrain.AddPrerequisiteAsync(blocker.Number);

        var epic = await CreateEpicAsync(project.Id, "Container with deps");
        await LinkIssueAsync(project.Id, epic.Id, dependent.Number);
        var issueDetailBefore = await GetIssueInfoAsync(project.Id, dependent.Number);

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/close", null);

        var issueDetailAfter = await GetIssueInfoAsync(project.Id, dependent.Number);
        Assert.Equal(issueDetailBefore!.Status, issueDetailAfter!.Status);
        Assert.Equal(issueDetailBefore.PrerequisiteNumbers, issueDetailAfter.PrerequisiteNumbers);
        Assert.Equal(issueDetailBefore.Health, issueDetailAfter.Health);
        Assert.Equal(issueDetailBefore.WorkflowRunId, issueDetailAfter.WorkflowRunId);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Equal("closed", detail.Status);
        Assert.Empty(detail.LinkedIssues);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssue_ByIssueNumber_RemovesMembershipAndDoesNotSilentNoOp()
    {
        var project = await CreateProjectAsync();
        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");
        var epic = await CreateEpicAsync(project.Id, "Unlink by number");
        await LinkIssueAsync(project.Id, epic.Id, first.Number);
        await LinkIssueAsync(project.Id, epic.Id, second.Number);

        // Regression: unlink by issue NUMBER (not internal id) must actually remove
        // the link. Previously the DELETE endpoint passed the number straight to
        // UnlinkIssueAsync, which matches on internal id — so it returned 200 but
        // changed nothing.
        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}/issues/{first.Number}");

        response.EnsureSuccessStatusCode();

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(second.Id, detail.LinkedIssues[0].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssue_ByInternalIssueId_RemainsSupported()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Only");
        var epic = await CreateEpicAsync(project.Id, "Unlink by id");
        await LinkIssueAsync(project.Id, epic.Id, issue.Number);

        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}/issues/{issue.Id}");

        response.EnsureSuccessStatusCode();

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Empty(detail.LinkedIssues);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task MarkDone_AllDelivered_EpicIsNotMarkedDoneAutomaticallyUntilRequested()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Delivered");
        await CompleteIssueAsync(project.Id, issue);
        var epic = await CreateEpicAsync(project.Id, "No auto complete");
        await LinkIssueAsync(project.Id, epic.Id, issue.Number);

        var beforeMark = await _client.GetDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Equal("active", beforeMark.Status);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.True(detail.Progress.ReadyToMarkDone);
        Assert.Equal(1, detail.Progress.DeliveredCount);
        Assert.Equal(1, detail.Progress.TotalIssueCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicDetail_LinkedIssue_ExposesPrerequisiteNumbers()
    {
        var project = await CreateProjectAsync();
        var upstream = await CreateIssueAsync(project.Id, "Upstream");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(dependent.Id);
        await dependentGrain.AddPrerequisiteAsync(upstream.Number);

        var epic = await CreateEpicAsync(project.Id, "Prereq numbers");
        await LinkIssueAsync(project.Id, epic.Id, upstream.Number);
        await LinkIssueAsync(project.Id, epic.Id, dependent.Number);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");

        var dependentRow = detail.LinkedIssues.Single(i => i.Number == dependent.Number);
        Assert.Equal(new[] { upstream.Number }, dependentRow.PrerequisiteNumbers);

        var upstreamRow = detail.LinkedIssues.Single(i => i.Number == upstream.Number);
        Assert.Empty(upstreamRow.PrerequisiteNumbers);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicDetail_LinkedIssue_ExposesExternalPrerequisitesSummary()
    {
        var project = await CreateProjectAsync();
        var externalUpstream = await CreateIssueAsync(project.Id, "External upstream");
        var memberDependent = await CreateIssueAsync(project.Id, "Member dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(memberDependent.Id);
        await dependentGrain.AddPrerequisiteAsync(externalUpstream.Number);

        var epic = await CreateEpicAsync(project.Id, "External prereq summary");
        await LinkIssueAsync(project.Id, epic.Id, memberDependent.Number);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");

        var dependentRow = detail.LinkedIssues.Single(i => i.Number == memberDependent.Number);
        var ghost = Assert.Single(dependentRow.ExternalPrerequisites);
        Assert.Equal(externalUpstream.Number, ghost.Number);
        Assert.Equal("External upstream", ghost.Title);
        Assert.False(string.IsNullOrEmpty(ghost.Stage));
        Assert.False(string.IsNullOrEmpty(ghost.Status));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicDetail_LinkedIssue_InternalPrerequisiteIsNotExposedAsExternal()
    {
        var project = await CreateProjectAsync();
        var upstream = await CreateIssueAsync(project.Id, "Internal upstream");
        var dependent = await CreateIssueAsync(project.Id, "Internal dependent");
        var dependentGrain = _grains.GetGrain<IIssueGrain>(dependent.Id);
        await dependentGrain.AddPrerequisiteAsync(upstream.Number);

        var epic = await CreateEpicAsync(project.Id, "Internal prereq");
        await LinkIssueAsync(project.Id, epic.Id, upstream.Number);
        await LinkIssueAsync(project.Id, epic.Id, dependent.Number);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");

        var dependentRow = detail.LinkedIssues.Single(i => i.Number == dependent.Number);
        Assert.Equal(new[] { upstream.Number }, dependentRow.PrerequisiteNumbers);
        Assert.Empty(dependentRow.ExternalPrerequisites);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicDetail_ProgressOutputsAreUnchangedByPrerequisiteData()
    {
        var project = await CreateProjectAsync();
        var first = await CreateIssueAsync(project.Id, "First");
        await CompleteIssueAsync(project.Id, first);
        var second = await CreateIssueAsync(project.Id, "Second");
        var secondGrain = _grains.GetGrain<IIssueGrain>(second.Id);
        await secondGrain.AddPrerequisiteAsync(first.Number);

        var epic = await CreateEpicAsync(project.Id, "Progress additivity");
        await LinkIssueAsync(project.Id, epic.Id, first.Number);
        await LinkIssueAsync(project.Id, epic.Id, second.Number);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");

        Assert.Equal(1, detail.Progress.DeliveredCount);
        Assert.Equal(2, detail.Progress.TotalIssueCount);
        Assert.False(detail.Progress.ReadyToMarkDone);
        Assert.NotNull(detail.Progress.NextIssue);
        Assert.Equal(second.Number, detail.Progress.NextIssue!.Number);
        Assert.Equal(second.Id, detail.Progress.NextIssue.Id);
        Assert.Single(detail.Progress.ActiveIssues);
        Assert.Equal(second.Id, detail.Progress.ActiveIssues[0].Id);
        Assert.Empty(detail.Progress.BlockedIssues);

        var secondRow = detail.LinkedIssues.Single(i => i.Number == second.Number);
        Assert.Equal(new[] { first.Number }, secondRow.PrerequisiteNumbers);
        Assert.Empty(secondRow.ExternalPrerequisites);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new
        {
            name = $"epic-life-{Guid.NewGuid():N}",
        });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            isDefault = true,
        });
        return project;
    }

    private async Task<IssueDto> CreateIssueAsync(string projectId, string title)
    {
        return await _client.PostDataAsync<IssueDto>($"/api/projects/{projectId}/issues", new { title, isDraft = false });
    }

    private async Task CompleteIssueAsync(string projectId, IssueDto issueInfo)
    {
        var grain = _grains.GetGrain<IIssueGrain>(issueInfo.Id);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(projectId, "Lifecycle Test", RepositoryBaseBranch: "main"));
        await grain.CompleteWorkAsync(wrId);
    }

    private async Task<EpicDto> CreateEpicAsync(string projectId, string title)
    {
        return await _client.PostDataAsync<EpicDto>($"/api/projects/{projectId}/epics", new { title, description = "lifecycle test", priority = "p2" });
    }

    private async Task LinkIssueAsync(string projectId, string epicId, int issueNumber)
    {
        await _client.PostOkAsync($"/api/projects/{projectId}/epics/{epicId}/issues", new { issueId = issueNumber.ToString() });
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetInfoAsync(projectId, number);
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, string Id, int[] PrerequisiteNumbers, string Status, string Health, string? WorkflowRunId);
    private sealed record EpicDto(string Id, int? Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record EpicDetailDto(string Id, string Status, LinkedIssueDto[] LinkedIssues, EpicProgressDto Progress);
    private sealed record LinkedIssueDto(string Id, int Number, string Title, string Status, string Stage, string Health, string? Priority, bool CanStart = false, StartBlockerDto? StartBlocker = null, int[] PrerequisiteNumbers = null!, ExternalPrerequisiteDto[] ExternalPrerequisites = null!);
    private sealed record ExternalPrerequisiteDto(int Number, string Title = "", string Stage = "", string Status = "");
    private sealed record StartBlockerDto(string Kind);
    private sealed record EpicProgressDto(int DeliveredCount, int TotalIssueCount, EpicProgressIssueDto[] BlockedIssues, EpicProgressIssueDto[] ActiveIssues, EpicNextIssueDto? NextIssue, string? NextIssueReason, bool ReadyToMarkDone);
    private sealed record EpicProgressIssueDto(string Id, int Number, string Title, string Health);
    private sealed record EpicNextIssueDto(string Id, int Number, string Title);
    private sealed record ConflictEnvelope(bool Success, string? Code = null, string? Error = null, ConflictDetailsDto? Details = null);
    private sealed record ConflictDetailsDto(string CurrentStatus, string RequestedStatus, int UndeliveredCount);
}

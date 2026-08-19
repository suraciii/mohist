using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

public class IssueCompositeLifecycleGrainSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueCompositeLifecycleGrainSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private IGrainFactory Grains => _fixture.Grains;
    private IServiceProvider Services => _fixture.Services;

    [Fact]
    public async Task CloseCompositeAsync_RejectsNonTerminalChild_WithoutClosingChild()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent");
        var child = await CreateIssueAsync(projectId, "Child");
        await AttachChildAsync(projectId, child.Number, parent.Number);
        var parentGrain = IssueGrain(projectId, parent.Number);

        await parentGrain.StartCompositeAsync();

        var exception = await Assert.ThrowsAsync<IssueParentHasNonTerminalChildrenException>(
            () => parentGrain.CloseCompositeAsync());
        Assert.Equal([child.Number], exception.NonTerminalChildNumbers);
        Assert.Equal("in_progress", (await GetIssueAsync(projectId, child.Number))!.Status);
    }

    [Fact]
    public async Task ReopenCompositeAsync_ReopensParentWithoutMutatingCancelledChild()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent");
        var child = await CreateIssueAsync(projectId, "Child");
        await AttachChildAsync(projectId, child.Number, parent.Number);
        var parentGrain = IssueGrain(projectId, parent.Number);

        await IssueGrain(projectId, child.Number).CancelAsync();
        await parentGrain.RecomputeCompositeStatusAsync();
        await parentGrain.ReopenCompositeAsync();

        Assert.Equal("backlog", (await GetIssueAsync(projectId, parent.Number))!.Status);
        Assert.Equal("cancelled", (await GetIssueAsync(projectId, child.Number))!.Status);
    }

    [Fact]
    public async Task ArchiveAsync_OnParent_ArchivesTerminalChildren_AndSkipsArchivedChild()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent");
        var first = await CreateIssueAsync(projectId, "First");
        var second = await CreateIssueAsync(projectId, "Second");
        await AttachChildAsync(projectId, first.Number, parent.Number);
        await AttachChildAsync(projectId, second.Number, parent.Number);
        var parentGrain = IssueGrain(projectId, parent.Number);
        var firstGrain = IssueGrain(projectId, first.Number);
        var secondGrain = IssueGrain(projectId, second.Number);

        await parentGrain.StartCompositeAsync();
        await firstGrain.CompleteWorkAsync((await firstGrain.GetActiveWorkflowRunIdAsync())!);
        var secondWorkflowRunId = (await secondGrain.GetActiveWorkflowRunIdAsync())!;
        await Grains.GetGrain<IWorkflowGrain>(secondWorkflowRunId).StopAsync("test-cancel");
        await secondGrain.CancelAsync();
        await parentGrain.RecomputeCompositeStatusAsync();
        await firstGrain.ArchiveAsync();

        await parentGrain.ArchiveAsync();

        Assert.NotNull((await GetIssueAsync(projectId, parent.Number))!.ArchivedAt);
        Assert.NotNull((await GetIssueAsync(projectId, first.Number))!.ArchivedAt);
        Assert.NotNull((await GetIssueAsync(projectId, second.Number))!.ArchivedAt);
        var archivedEvents = (await LoadIssueEventsAsync(projectId, first.Number))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.IssueArchived);
        Assert.Equal(1, archivedEvents);
    }

    [Fact]
    public async Task ArchiveForParentCascadeAsync_ArchivesCancelledIssue_ButDirectArchiveRejectsIt()
    {
        var projectId = await CreateProjectAsync();
        var issue = await CreateIssueAsync(projectId, "Cancelled");
        var grain = IssueGrain(projectId, issue.Number);
        await grain.CancelAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.ArchiveAsync());
        await grain.ArchiveForParentCascadeAsync();

        Assert.NotNull((await GetIssueAsync(projectId, issue.Number))!.ArchivedAt);
    }

    private IIssueGrain IssueGrain(string projectId, int number) =>
        Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));

    private async Task<string> CreateProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        await Grains.GetGrain<IProjectGrain>(id).CreateAsync($"mohist-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist-local.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return id;
    }

    private async Task<(int Number, string IssueKey)> CreateIssueAsync(string projectId, string title)
    {
        var number = await Grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        await Grains.GetGrain<IIssueGrain>(issueKey)
            .CreateAsync(projectId, number, title, null, null, null, isDraft: false);
        return (number, issueKey);
    }

    private async Task AttachChildAsync(string projectId, int childNumber, int parentNumber)
    {
        await IssueGrain(projectId, childNumber).UpdateFullAsync(new UpdateIssueData(
            PresentFields: new HashSet<string>(StringComparer.Ordinal) { nameof(UpdateIssueData.ParentIssueNumber) },
            ParentIssueNumber: parentNumber));
    }

    private async Task<IssueReadModel?> GetIssueAsync(string projectId, int number)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IssueQuerier>().GetAsync(projectId, number);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> LoadIssueEventsAsync(string projectId, int issueNumber)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEventStore>()
            .ListIssueEventsAsync(projectId, issueNumber, 200);
    }
}

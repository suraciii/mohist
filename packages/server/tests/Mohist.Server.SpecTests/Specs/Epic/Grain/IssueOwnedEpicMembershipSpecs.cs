using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.TestSupport;
using Xunit;
using static Mohist.Server.SpecTests.Specs.Epic.Grain.EpicEventPublishTestSupport;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

public class IssueOwnedEpicMembershipSpecs
{
    private const string ProjectId = "project_1";

    [Fact]
    public async Task LinkIssueAsync_NewIssue_CommandsIssueToOwnAffiliation()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        var grains = new RecordingGrainFactory();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:1", new NoopEventStore(), new FakeTimeProvider(), grains);

        var outcome = await grain.LinkIssueAsync(1, ProjectId);

        Assert.Equal("linked", outcome.Status);
        Assert.Equal(1, outcome.OwningEpicNumber);
        Assert.Equal("Epic 1", outcome.OwningEpicTitle);
        var call = Assert.Single(grains.AffiliationCalls);
        Assert.Equal(GrainKey.Issue(new IssueKey(ProjectId, 1)), call.IssueKey);
        Assert.Equal(1, call.EpicNumber);
        Assert.True(call.IsLink);
        await using var verify = database.CreateDbContext();
        Assert.Null((await verify.Issues.SingleAsync()).EpicNumber);
    }

    [Fact]
    public async Task MembershipCommands_RejectProjectOutsideEpicGrainKey()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        var grains = new RecordingGrainFactory();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:1", new NoopEventStore(), new FakeTimeProvider(), grains);

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.LinkIssueAsync(1, "project_2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.UnlinkIssueAsync(1, "project_2"));

        Assert.Empty(grains.AffiliationCalls);
    }

    [Fact]
    public async Task LinkIssueAsync_AlreadyAssignedToClosedEpic_IsIdempotent()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Closed);
        await SeedIssueAsync(database, epicNumber: 1);
        var grains = new RecordingGrainFactory();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:1", new NoopEventStore(), new FakeTimeProvider(), grains);

        var outcome = await grain.LinkIssueAsync(1, ProjectId);

        Assert.Equal("already-linked", outcome.Status);
        Assert.Equal(1, outcome.OwningEpicNumber);
        Assert.Equal("Epic 1", outcome.OwningEpicTitle);
        Assert.Empty(grains.AffiliationCalls);
    }

    [Fact]
    public async Task LinkIssueAsync_NewIssueToClosedEpic_IsRejected()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Closed);
        await SeedIssueAsync(database);
        var grains = new RecordingGrainFactory();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:1", new NoopEventStore(), new FakeTimeProvider(), grains);

        await Assert.ThrowsAsync<EpicClosedCannotLinkException>(() => grain.LinkIssueAsync(1, ProjectId));

        Assert.Empty(grains.AffiliationCalls);
    }

    [Fact]
    public async Task UnlinkIssueAsync_UsesExpectedEpicNumber()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingGrainFactory();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:7", new NoopEventStore(), new FakeTimeProvider(), grains);

        await grain.UnlinkIssueAsync(42, ProjectId);

        var call = Assert.Single(grains.AffiliationCalls);
        Assert.Equal(GrainKey.Issue(new IssueKey(ProjectId, 42)), call.IssueKey);
        Assert.Null(call.EpicNumber);
        Assert.False(call.IsLink);
    }

    [Fact]
    public async Task LinkIssuesAsync_ReportsCurrentIssueStateAndRejectsExistingOwner()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database);
        await SeedEpicAsync(database, epicNumber: 7);
        await SeedIssueAsync(database, issueNumber: 1, epicNumber: 1);
        await SeedIssueAsync(database, issueNumber: 2, epicNumber: 7);
        var grains = new RecordingGrainFactory();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:1", new NoopEventStore(), new FakeTimeProvider(), grains);

        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("already", 1),
            new BatchMembershipRequestItem("move", 2),
            new BatchMembershipRequestItem("missing", 3),
        ], ProjectId);

        Assert.Collection(outcomes,
            result =>
            {
                Assert.Equal(("already", "already-linked", 1), (result.Identifier, result.Status, result.IssueNumber));
                Assert.Equal(1, result.OwningEpicNumber);
                Assert.Equal("Epic 1", result.OwningEpicTitle);
            },
            result =>
            {
                Assert.Equal(("move", "conflict", 2), (result.Identifier, result.Status, result.IssueNumber));
                Assert.Equal(7, result.OwningEpicNumber);
                Assert.Equal("Epic 7", result.OwningEpicTitle);
            },
            result =>
            {
                Assert.Equal(("missing", "not-found", (int?)null), (result.Identifier, result.Status, result.IssueNumber));
                Assert.Null(result.OwningEpicNumber);
                Assert.Null(result.OwningEpicTitle);
            });
        Assert.Empty(grains.AffiliationCalls);
    }
}

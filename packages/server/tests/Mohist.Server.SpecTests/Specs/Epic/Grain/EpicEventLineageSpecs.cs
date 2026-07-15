using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Xunit;
using static Mohist.Server.SpecTests.Specs.Epic.Grain.EpicEventPublishTestSupport;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

public class EpicEventLineageSpecs
{
    private const string ProjectId = "project_1";
    private const string EpicId = "epic_1";
    private static readonly DateTimeOffset FixedTime = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_StampsConformingProjectAndEpicLineage()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, new FakeTimeProvider(FixedTime));

        var dto = await grain.CreateAsync(ProjectId, "Auth epic", "description", "p1");

        var created = Assert.Single(eventStore.Appended, e => e.Envelope.Source.ToString() == $"/mohist/epics/{dto.Id}");
        Assert.Equal(ProjectId, created.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(dto.Id, created.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(created.Envelope.Extensions.ContainsKey("epicno"));
        EnvelopeConformance.AssertRequired(created.Envelope);
    }

    [Fact]
    public async Task TransactionalMutations_StampConformingProjectAndEpicLineage()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await SeedEpicAsync(database);
        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, new FakeTimeProvider(FixedTime));

        await grain.StartAsync();

        var statusChange = Assert.Single(eventStore.Appended, e => e.Envelope.Type == EventCatalog.ReverseDns.EpicStatusChanged);
        Assert.Equal(ProjectId, statusChange.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(EpicId, statusChange.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(statusChange.Envelope.Extensions.ContainsKey("epicno"));
        EnvelopeConformance.AssertRequired(statusChange.Envelope);
    }

    [Fact]
    public async Task LinkIssueAsync_StampsLineageAndPushesAffiliation()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        var grains = new RecordingGrainFactory();
        var grain = new EpicGrain(database.Factory, grains, new FakeTimeProvider(FixedTime), eventStore, NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:{EpicId}",
        };

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        var linked = Assert.Single(eventStore.Appended);
        Assert.Equal(EventCatalog.ReverseDns.EpicIssueLinked, linked.Envelope.Type);
        Assert.Equal(ProjectId, linked.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(EpicId, linked.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(linked.Envelope.Extensions.ContainsKey("epicno"));
        EnvelopeConformance.AssertRequired(linked.Envelope);
        Assert.Equal(EpicId, Assert.Single(grains.AffiliationCalls).EpicId);
    }

    [Fact]
    public async Task UnlinkIssueAsync_StampsLineageAndClearsAffiliation()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        await SeedLinkAsync(database, "issue_1", 1);
        var grains = new RecordingGrainFactory();
        var grain = new EpicGrain(database.Factory, grains, new FakeTimeProvider(FixedTime), eventStore, NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:{EpicId}",
        };

        await grain.UnlinkIssueAsync("issue_1", ProjectId);

        var unlinked = Assert.Single(eventStore.Appended, e => e.Envelope.Type == EventCatalog.ReverseDns.EpicIssueUnlinked);
        Assert.Equal(ProjectId, unlinked.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(EpicId, unlinked.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(unlinked.Envelope.Extensions.ContainsKey("epicno"));
        EnvelopeConformance.AssertRequired(unlinked.Envelope);
        Assert.Null(Assert.Single(grains.AffiliationCalls).EpicId);
    }

    [Fact]
    public async Task LinkIssueAsync_PushFailure_DoesNotRollbackCommittedMembership()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        var grain = new EpicGrain(database.Factory, new ThrowingAffiliationGrainFactory(), new FakeTimeProvider(FixedTime), eventStore, NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:{EpicId}",
        };

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        Assert.Single(await verify.EpicIssues.Where(link => link.ProjectId == ProjectId && link.EpicId == EpicId).ToListAsync());
    }
}

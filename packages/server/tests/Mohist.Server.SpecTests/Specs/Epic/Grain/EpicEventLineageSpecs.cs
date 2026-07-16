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
    private const int EpicNumber = 1;
    private static readonly DateTimeOffset FixedTime = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_StampsConformingProjectAndEpicLineage()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicNumber}", eventStore, new FakeTimeProvider(FixedTime));

        var dto = await grain.CreateAsync(ProjectId, EpicNumber, "Auth epic", "description", "p1");

        var created = Assert.Single(eventStore.Appended, e => e.Envelope.Source.ToString() == $"/mohist/projects/{ProjectId}/epics/{dto.Number}");
        Assert.Equal(ProjectId, created.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(dto.Number.ToString(), created.Envelope.Extensions[EventCatalog.Lineage.Epic]);
        Assert.False(created.Envelope.Extensions.ContainsKey("epicno"));
        EnvelopeConformance.AssertRequired(created.Envelope);
    }

    [Fact]
    public async Task TransactionalMutations_StampConformingProjectAndEpicLineage()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await SeedEpicAsync(database);
        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicNumber}", eventStore, new FakeTimeProvider(FixedTime));

        await grain.StartAsync();

        var statusChange = Assert.Single(eventStore.Appended, e => e.Envelope.Type == EventCatalog.ReverseDns.EpicStatusChanged);
        Assert.Equal(ProjectId, statusChange.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(EpicNumber.ToString(), statusChange.Envelope.Extensions[EventCatalog.Lineage.Epic]);
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
            GrainKeyForTest = $"{ProjectId}:{EpicNumber}",
        };

        await grain.LinkIssueAsync(1, ProjectId);

        var linked = Assert.Single(eventStore.Appended);
        Assert.Equal(EventCatalog.ReverseDns.EpicIssueLinked, linked.Envelope.Type);
        Assert.Equal(ProjectId, linked.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(EpicNumber.ToString(), linked.Envelope.Extensions[EventCatalog.Lineage.Epic]);
        Assert.False(linked.Envelope.Extensions.ContainsKey("epicno"));
        EnvelopeConformance.AssertRequired(linked.Envelope);
        Assert.Equal(EpicNumber, Assert.Single(grains.AffiliationCalls).EpicNumber);
    }

    [Fact]
    public async Task UnlinkIssueAsync_StampsLineageAndClearsAffiliation()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        await SeedLinkAsync(database, 1);
        var grains = new RecordingGrainFactory();
        var grain = new EpicGrain(database.Factory, grains, new FakeTimeProvider(FixedTime), eventStore, NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:{EpicNumber}",
        };

        await grain.UnlinkIssueAsync(1, ProjectId);

        var unlinked = Assert.Single(eventStore.Appended, e => e.Envelope.Type == EventCatalog.ReverseDns.EpicIssueUnlinked);
        Assert.Equal(ProjectId, unlinked.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(EpicNumber.ToString(), unlinked.Envelope.Extensions[EventCatalog.Lineage.Epic]);
        Assert.False(unlinked.Envelope.Extensions.ContainsKey("epicno"));
        EnvelopeConformance.AssertRequired(unlinked.Envelope);
        Assert.Null(Assert.Single(grains.AffiliationCalls).EpicNumber);
    }

    [Fact]
    public async Task LinkIssueAsync_PushFailure_DoesNotRollbackCommittedMembership()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        var grain = new EpicGrain(database.Factory, new ThrowingAffiliationGrainFactory(), new FakeTimeProvider(FixedTime), eventStore, NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:{EpicNumber}",
        };

        await grain.LinkIssueAsync(1, ProjectId);

        await using var verify = database.CreateDbContext();
        Assert.Single(await verify.EpicIssues.Where(link => link.ProjectId == ProjectId && link.EpicNumber == EpicNumber).ToListAsync());
    }
}

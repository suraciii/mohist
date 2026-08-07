using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Xunit;
using static Mohist.Server.SpecTests.Specs.Epic.Grain.EpicEventPublishTestSupport;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

public class EpicEventPublishSpecs
{
    private const string ProjectId = "project_1";
    private const int EpicNumber = 1;

    [Fact]
    public async Task StartAsync_PersistsScopedEpicStatusEvent()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await using (database)
        {
        await SeedEpicAsync(database);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));
        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicNumber}", eventStore, time);

        await grain.StartAsync();

        var stored = Assert.Single(await eventStore.ListEpicEventsAsync(ProjectId, EpicNumber));
        Assert.Equal(EventCatalog.ReverseDns.EpicStatusChanged, stored.Envelope.Type);
        Assert.Equal(EpicEventPersistence.EpicSource(ProjectId, EpicNumber), stored.Envelope.Source.OriginalString);
        Assert.Equal(ProjectId, stored.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(EpicNumber.ToString(), stored.Envelope.Extensions[EventCatalog.Lineage.Epic]);
        Assert.Equal("idle", stored.Envelope.Data!.Value.GetProperty("oldStatus").GetString());
        Assert.Equal("running", stored.Envelope.Data!.Value.GetProperty("newStatus").GetString());
        }
    }

    [Fact]
    public async Task LinkIssueAsync_CommandsIssueWithoutRecordingEpicMembershipEvent()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await using (database)
        {
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        var grains = new RecordingGrainFactory();
        var grain = CreateGrain(
            database.Factory,
            $"{ProjectId}:{EpicNumber}",
            eventStore,
            new FakeTimeProvider(),
            grains);

        await grain.LinkIssueAsync(1, ProjectId);

        Assert.Single(grains.AffiliationCalls);
        Assert.Empty(await eventStore.ListEpicEventsAsync(ProjectId, EpicNumber));
        }
    }
}

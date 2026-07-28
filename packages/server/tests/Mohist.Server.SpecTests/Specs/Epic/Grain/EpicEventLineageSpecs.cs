using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
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
        await using (database)
        {
            var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicNumber}", eventStore, new FakeTimeProvider(FixedTime));

            var dto = await grain.CreateAsync(ProjectId, EpicNumber, "Auth epic", "description", "p1");

            var created = Assert.Single(eventStore.Appended, entry =>
                entry.Envelope.Source.ToString() == $"/mohist/projects/{ProjectId}/epics/{dto.Number}");
            Assert.Equal(ProjectId, created.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
            Assert.Equal(dto.Number.ToString(), created.Envelope.Extensions[EventCatalog.Lineage.Epic]);
            ProducerConformance.Assert(
                EventProducerFamily.Epic,
                created.Envelope.Extensions,
                new(ProjectId: ProjectId, Epic: dto.Number.ToString()));
        }
    }

    [Fact]
    public async Task CreateAsync_RejectsIdentityOutsideGrainKey()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await using (database)
        {
            var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicNumber}", eventStore, new FakeTimeProvider(FixedTime));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                grain.CreateAsync("project_2", EpicNumber, "cross-project epic", null, "p1"));

            await using var verify = database.CreateDbContext();
            Assert.Empty(await verify.Epics.ToListAsync());
            Assert.Empty(eventStore.Appended);
        }
    }

    [Fact]
    public async Task StartAsync_StampsConformingProjectAndEpicLineage()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await using (database)
        {
            await SeedEpicAsync(database);
            var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicNumber}", eventStore, new FakeTimeProvider(FixedTime));

            await grain.StartAsync();

            var statusChanged = Assert.Single(eventStore.Appended, entry =>
                entry.Envelope.Type == EventCatalog.ReverseDns.EpicStatusChanged);
            Assert.Equal(ProjectId, statusChanged.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
            Assert.Equal(EpicNumber.ToString(), statusChanged.Envelope.Extensions[EventCatalog.Lineage.Epic]);
            ProducerConformance.Assert(
                EventProducerFamily.Epic,
                statusChanged.Envelope.Extensions,
                new(ProjectId: ProjectId, Epic: EpicNumber.ToString()));
        }
    }

    [Fact]
    public async Task LifecycleCommands_StampEveryNormalEpicEventVariantThroughAppendPath()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await using (database)
        {
            var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicNumber}", eventStore, new FakeTimeProvider(FixedTime));

            await grain.CreateAsync(ProjectId, EpicNumber, "Auth epic", "description", "p1");
            await grain.UpdateAsync("Updated epic", null, "p2");
            await grain.StartAsync();
            await grain.PauseAsync("hold");

            await SeedEpicAsync(database, epicNumber: 2, status: "running");
            var terminalGrain = CreateGrain(database.Factory, $"{ProjectId}:2", eventStore, new FakeTimeProvider(FixedTime));
            await terminalGrain.SetStatusAsync("closed");
            await terminalGrain.ReopenAsync();

            Assert.Contains(eventStore.Appended, entry => entry.Envelope.Type == EventCatalog.ReverseDns.EpicPriorityChanged);
            Assert.Contains(eventStore.Appended, entry => entry.Envelope.Type == EventCatalog.ReverseDns.EpicUpdated);
            Assert.Contains(eventStore.Appended, entry => entry.Envelope.Type == EventCatalog.ReverseDns.EpicStatusChanged);
            Assert.Contains(eventStore.Appended, entry => entry.Envelope.Type == EventCatalog.ReverseDns.EpicClosed);
            Assert.Contains(eventStore.Appended, entry => entry.Envelope.Type == EventCatalog.ReverseDns.EpicReopened);

            foreach (var entry in eventStore.Appended)
            {
                var epicNumber = entry.Envelope.Source.ToString().EndsWith("/2", StringComparison.Ordinal)
                    ? "2"
                    : "1";
                ProducerConformance.Assert(
                    EventProducerFamily.Epic,
                    entry.Envelope.Extensions,
                    new(ProjectId: ProjectId, Epic: epicNumber));
            }
        }
    }

    [Fact]
    public async Task LinkAndUnlinkIssue_CommandTheIssueWithoutEmittingEpicMembershipEvents()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await using (database)
        {
            await SeedEpicAsync(database);
            await SeedIssueAsync(database);
            var grains = new RecordingGrainFactory();
            var identity = GrainTestContext.Create($"{ProjectId}:{EpicNumber}");
            var grain = new EpicGrain(identity.Context, identity.Runtime, database.Factory, grains,
                new FakeTimeProvider(FixedTime), eventStore, NullLogger<EpicGrain>.Instance);

            await grain.LinkIssueAsync(1, ProjectId);
            await grain.UnlinkIssueAsync(1, ProjectId);

            Assert.Collection(
                grains.AffiliationCalls,
                linked =>
                {
                    Assert.True(linked.IsLink);
                    Assert.Equal(EpicNumber, linked.EpicNumber);
                },
                unlinked =>
                {
                    Assert.False(unlinked.IsLink);
                    Assert.Null(unlinked.EpicNumber);
                });
            Assert.Empty(eventStore.Appended);
        }
    }

    [Fact]
    public async Task LinkIssueAsync_CommandFailureDoesNotCreateAnEpicOwnedMembership()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        await using (database)
        {
            await SeedEpicAsync(database);
            await SeedIssueAsync(database);
            var identity = GrainTestContext.Create($"{ProjectId}:{EpicNumber}");
            var grain = new EpicGrain(identity.Context, identity.Runtime, database.Factory,
                new ThrowingAffiliationGrainFactory(), new FakeTimeProvider(FixedTime), eventStore,
                NullLogger<EpicGrain>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.LinkIssueAsync(1, ProjectId));

            await using var verify = database.CreateDbContext();
            var issue = await verify.Issues.SingleAsync();
            Assert.Null(issue.EpicNumber);
            Assert.Empty(eventStore.Appended);
        }
    }
}

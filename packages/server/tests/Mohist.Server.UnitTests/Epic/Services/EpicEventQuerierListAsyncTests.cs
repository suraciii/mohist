using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Api;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.Epic.Services;

/// <summary>
/// Specs for issue-94 T-005: <see cref="EpicEventQuerier"/> is a
/// pass-through to <see cref="IEventStore.ListEpicEventsAsync"/>. These
/// specs seed envelopes via the recording store and assert the read path
/// returns them in chronological order, isolates per-epic, and yields an
/// empty list for an epic with no events.
/// </summary>
public class EpicEventQuerierListAsyncTests
{
    private const string ProjectId = "project_1";
    private const string EpicId = "epic_1";
    private const string OtherEpicId = "epic_2";

    [Fact]
    public async Task ListAsync_ReturnsEmptyForEpicWithNoEvents()
    {
        var eventStore = new RecordingEventStore();
        var querier = new EpicEventQuerier(eventStore);

        var events = await querier.ListAsync(EpicId);

        Assert.Empty(events);
    }

    [Fact]
    public async Task ListAsync_ReturnsEventsForEpicInChronologicalOrder()
    {
        var fixedTime = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var eventStore = new RecordingEventStore();
        await AppendEpicEventAsync(eventStore, EpicId, ProjectId,
            new EpicCreated("Auth", "desc", "p2"), fixedTime, subject: "1");
        await AppendEpicEventAsync(eventStore, EpicId, ProjectId,
            new EpicPriorityChanged("p2", "p0"), fixedTime.AddSeconds(1), subject: "1");
        await AppendEpicEventAsync(eventStore, EpicId, ProjectId,
            new EpicStatusChanged("idle", "running"), fixedTime.AddSeconds(2), subject: "1");

        var querier = new EpicEventQuerier(eventStore);
        var events = await querier.ListAsync(EpicId);

        Assert.Equal(3, events.Count);
        Assert.Equal(EventCatalog.ReverseDns.EpicCreated, events[0].Envelope.Type);
        Assert.Equal(EventCatalog.ReverseDns.EpicPriorityChanged, events[1].Envelope.Type);
        Assert.Equal(EventCatalog.ReverseDns.EpicStatusChanged, events[2].Envelope.Type);
        Assert.True(events[1].Id > events[0].Id);
        Assert.True(events[2].Id > events[1].Id);
    }

    [Fact]
    public async Task ListAsync_IsolatesEventsByEpicSource()
    {
        var eventStore = new RecordingEventStore();
        await AppendEpicEventAsync(eventStore, EpicId, ProjectId,
            new EpicCreated("Auth", "desc", "p2"), TestTime.UtcNow, subject: "1");
        await AppendEpicEventAsync(eventStore, OtherEpicId, ProjectId,
            new EpicCreated("Billing", "desc", "p2"), TestTime.UtcNow, subject: "1");

        var querier = new EpicEventQuerier(eventStore);
        var first = await querier.ListAsync(EpicId);
        var second = await querier.ListAsync(OtherEpicId);

        var firstSingle = Assert.Single(first);
        Assert.Equal(EventCatalog.ReverseDns.EpicCreated, firstSingle.Envelope.Type);
        Assert.Equal($"/mohist/epics/{EpicId}", firstSingle.Envelope.Source.ToString());

        var secondSingle = Assert.Single(second);
        Assert.Equal($"/mohist/epics/{OtherEpicId}", secondSingle.Envelope.Source.ToString());
    }

    [Fact]
    public async Task ListAsync_ReturnsEnvelopeShapeMatchingDtoContract()
    {
        // Each event envelope that survives the read path must expose
        // (id, type, time, source, data) — the fields the route surfaces
        // through StoredCloudEventDto. The DTO-level mapping itself is
        // verified at the route spec; this asserts the querier is a
        // faithful pass-through.
        var fixedTime = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var eventStore = new RecordingEventStore();
        await AppendEpicEventAsync(eventStore, EpicId, ProjectId,
            new EpicCreated("Auth epic", "auth-related description", "p2"),
            fixedTime, subject: "1");

        var querier = new EpicEventQuerier(eventStore);
        var events = await querier.ListAsync(EpicId);
        var stored = Assert.Single(events);

        Assert.NotEqual(0, stored.Id);
        Assert.Equal(EventCatalog.ReverseDns.EpicCreated, stored.Envelope.Type);
        Assert.Equal(fixedTime, stored.Envelope.Time);
        Assert.Equal(new Uri($"/mohist/epics/{EpicId}", UriKind.Relative), stored.Envelope.Source);
        Assert.Equal("1", stored.Envelope.Subject);
        Assert.NotNull(stored.Envelope.Data);
        Assert.Equal("Auth epic", stored.Envelope.Data!.Value.GetProperty("title").GetString());
        Assert.Equal("p2", stored.Envelope.Data!.Value.GetProperty("priority").GetString());
        Assert.Equal(ProjectId, stored.Envelope.Extensions["projectid"]);
        Assert.Equal(EpicId, stored.Envelope.Extensions["epicid"]);
    }

    [Fact]
    public async Task ListAsync_RespectsLimitTailForLargeHistory()
    {
        var fixedTime = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var eventStore = new RecordingEventStore();
        for (var i = 0; i < 5; i++)
        {
            await AppendEpicEventAsync(eventStore, EpicId, ProjectId,
                new EpicUpdated("title " + i, null, null),
                fixedTime.AddSeconds(i),
                subject: "1");
        }

        var querier = new EpicEventQuerier(eventStore);
        var lastTwo = await querier.ListAsync(EpicId, limit: 2);

        Assert.Equal(2, lastTwo.Count);
        // TakeLast preserves chronological order on the returned tail.
        Assert.Equal("title 3", lastTwo[0].Envelope.Data!.Value.GetProperty("title").GetString());
        Assert.Equal("title 4", lastTwo[1].Envelope.Data!.Value.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ListAsync_DtoFromMappingRoundTripsPayloadToJsonElement()
    {
        // Mirrors the route-level conversion: each StoredCloudEvent is
        // converted to a StoredCloudEventDto whose Data is the same
        // JsonElement produced by the serializer.
        var fixedTime = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var eventStore = new RecordingEventStore();
        await AppendEpicEventAsync(eventStore, EpicId, ProjectId,
            new EpicPriorityChanged("p2", "p0"), fixedTime, subject: "1");

        var querier = new EpicEventQuerier(eventStore);
        var events = await querier.ListAsync(EpicId);
        var stored = Assert.Single(events);

        var dto = StoredCloudEventDto.From(stored);
        Assert.Equal(EventCatalog.ReverseDns.EpicPriorityChanged, dto.Type);
        Assert.Equal(fixedTime.ToString("o"), dto.Time);
        Assert.Equal(JsonValueKind.Object, dto.Data.ValueKind);
        Assert.Equal("p2", dto.Data.GetProperty("oldPriority").GetString());
        Assert.Equal("p0", dto.Data.GetProperty("newPriority").GetString());
    }

    private static async Task AppendEpicEventAsync(
        RecordingEventStore eventStore,
        string epicId,
        string projectId,
        EpicEvent payload,
        DateTimeOffset time,
        string subject)
    {
        var source = EpicEventPersistence.EpicSource(epicId);
        var extensions = new Dictionary<string, string>
        {
            ["projectid"] = projectId,
            ["epicid"] = epicId,
            ["epicno"] = subject,
        };

        await eventStore.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: EpicEventSerializer.BusType(payload),
            time: time,
            data: EpicEventSerializer.ToData(payload),
            subject: subject,
            extensions: extensions));
    }
}
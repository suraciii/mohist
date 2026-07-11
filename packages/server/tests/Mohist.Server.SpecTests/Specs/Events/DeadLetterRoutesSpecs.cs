using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Collection("IntegrationMisc")]
public sealed class DeadLetterRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public DeadLetterRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task List_ReturnsUnresolvedRowsAndSupportsHandlerFilter()
    {
        var store = _fixture.Services.GetRequiredService<IDeadLetterStore>();
        var row = BuildRow("test.list.handler");
        await store.WriteAsync(row);

        try
        {
            using var response = await _fixture.Client.GetAsync(
                "/api/events/dead-letters?limit=10&handler=test.list.handler");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var listed = Assert.Single(body.GetProperty("data").EnumerateArray());
            Assert.Equal(row.DeadLetterId, listed.GetProperty("id").GetInt64());
            Assert.Equal(row.EventId, listed.GetProperty("eventId").GetString());
            Assert.Equal("test.list.handler", listed.GetProperty("handler").GetString());
            Assert.Equal(row.AttemptCount, listed.GetProperty("attempts").GetInt32());
        }
        finally
        {
            await store.DeleteAsync(row.DeadLetterId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Redeliver_RetriesRecordedHandlerAndDeletesResolvedRow()
    {
        var store = _fixture.Services.GetRequiredService<IDeadLetterStore>();
        var row = BuildRow(typeof(EventBridge).FullName!);
        await store.WriteAsync(row);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/events/dead-letters/{row.DeadLetterId}/redeliver",
            new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(row.DeadLetterId, data.GetProperty("id").GetInt64());
        Assert.True(data.GetProperty("delivered").GetBoolean());
        Assert.Equal(1, data.GetProperty("attempts").GetInt32());
        Assert.Null(await store.GetAsync(row.DeadLetterId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task List_RejectsOutOfRangeLimit()
    {
        using var response = await _fixture.Client.GetAsync(
            "/api/events/dead-letters?limit=501");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static DeadLetterRow BuildRow(string failingHandler) =>
        new()
        {
            Origin = nameof(EventOrigin.Issue),
            Id = 42,
            Source = "/mohist/issues/issue_dead_letter",
            EventId = $"evt_dead_letter_{Guid.NewGuid():N}",
            Type = "com.mohist.test.dead-letter",
            Time = new DateTimeOffset(2026, 7, 11, 1, 0, 0, TimeSpan.Zero),
            SpecVersion = "1.0",
            Subject = "362",
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(new { issueNumber = 362 }),
            ExtensionsJson = "{}",
            FailingHandler = failingHandler,
            ErrorMessage = "handler unavailable",
            ErrorStack = "test stack",
            AttemptCount = 3,
            DeadLetteredAt = new DateTimeOffset(2026, 7, 11, 1, 1, 0, TimeSpan.Zero),
        };
}

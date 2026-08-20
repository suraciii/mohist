using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Events.WebSocket;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public sealed class DeadLetterRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public DeadLetterRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task List_ReturnsUnresolvedRowsThroughTheEnvelopeShape()
    {
        var store = _fixture.Services.GetRequiredService<IDeadLetterStore>();
        var row = BuildRow("test.list.handler");
        await store.WriteAsync(row);

        try
        {
            using var response = await _fixture.Client.GetAsync(
                "/api/events/dead-letters?limit=10&handler=test.list.handler");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var listed = Assert.Single(body.GetProperty("data").EnumerateArray());
            Assert.Equal(row.DeadLetterId, listed.GetProperty("id").GetInt64());
            Assert.Equal(row.EventId, listed.GetProperty("eventId").GetString());
            Assert.Equal("test.list.handler", listed.GetProperty("handler").GetString());
            Assert.False(listed.TryGetProperty("errorStack", out _));
        }
        finally
        {
            await store.DeleteAsync(row.DeadLetterId);
        }
    }

    [Fact]
    public async Task Redeliver_RejectsEventSocketBridgeBecauseItIsNotDurable()
    {
        var store = _fixture.Services.GetRequiredService<IDeadLetterStore>();
        var row = BuildRow(typeof(EventSocketDomainBridge).FullName!);
        await store.WriteAsync(row);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/events/dead-letters/{row.DeadLetterId}/redeliver",
            new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var resolved = await store.GetAsync(row.DeadLetterId);
        Assert.NotNull(resolved);
        Assert.Equal(DeadLetterStatus.Pending, resolved.Status);
    }

    [Fact]
    public async Task List_RejectsOutOfRangeLimit()
    {
        using var response = await _fixture.Client.GetAsync(
            "/api/events/dead-letters?limit=501");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:3456", true)]
    [InlineData("http://localhost:3456", true)]
    [InlineData("http://192.168.1.10:3456", false)]
    [InlineData("http://0.0.0.0:3456", false)]
    [InlineData("http://*:3456", false)]
    [InlineData("http://[::]:3456", false)]
    public void OperatorBoundary_MapsRoutesOnlyOnLoopbackListener(string url, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = url })
            .Build();

        Assert.Equal(expected, DeadLetterRoutes.UsesLoopbackOnlyListener(configuration));
    }

    [Fact]
    public async Task Redeliver_RejectsUnauthenticatedCallerAndHasNoSideEffect()
    {
        var store = _fixture.Services.GetRequiredService<IDeadLetterStore>();
        var row = BuildRow(typeof(EventSocketDomainBridge).FullName!);
        await store.WriteAsync(row);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/events/dead-letters/{row.DeadLetterId}/redeliver")
            {
                Content = JsonContent.Create(new { }),
            };
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");

            using var anonymous = _fixture.CreateClient();
            using var response = await anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var stored = await store.GetAsync(row.DeadLetterId);
            Assert.NotNull(stored);
            Assert.Equal(DeadLetterStatus.Pending, stored.Status);
            Assert.Null(stored.RedeliveryAttemptedAt);
        }
        finally
        {
            await store.DeleteAsync(row.DeadLetterId);
        }
    }

    private static DeadLetterRow BuildRow(string failingHandler)
    {
        var key = Guid.NewGuid().ToString("N");
        return new()
        {
            Origin = nameof(EventOrigin.Issue),
            Id = 42,
            Source = $"/mohist/issues/issue_dead_letter_{key}",
            EventId = $"evt_dead_letter_{key}",
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
}

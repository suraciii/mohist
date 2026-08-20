using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// Calculation specs for the dead-letter read path behind
/// <c>GET /api/events/dead-letters</c>: the <c>IDeadLetterStore</c> query
/// (unresolved rows only, optional handler filter, limit) and the
/// <c>OperatorDiagnostic.Summarize</c> redaction of embedded stack frames,
/// file paths, and UNC paths. Both run without an HTTP round-trip. The
/// route contract (401/409/400, loopback-only listener gate, one list
/// success-path shape) stays in <c>DeadLetterRoutesSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class DeadLetterQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public DeadLetterQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private IDeadLetterStore CreateStore() => _fixture.Services.GetRequiredService<IDeadLetterStore>();

    [Fact]
    public async Task QueryAsync_ReturnsOnlyUnresolvedRowsForTheRequestedHandler()
    {
        var store = CreateStore();
        var wanted = BuildRow("sink.handler-a");
        var otherHandler = BuildRow("sink.handler-b");
        var resolved = BuildRow("sink.handler-a");
        resolved.Status = DeadLetterStatus.Resolved;
        await store.WriteAsync(wanted);
        await store.WriteAsync(otherHandler);
        await store.WriteAsync(resolved);

        try
        {
            var rows = await store.QueryAsync("sink.handler-a", limit: 100);

            var matched = Assert.Single(rows);
            Assert.Equal(wanted.DeadLetterId, matched.DeadLetterId);
            Assert.Equal(DeadLetterStatus.Pending, matched.Status);
        }
        finally
        {
            await store.DeleteAsync(wanted.DeadLetterId);
            await store.DeleteAsync(otherHandler.DeadLetterId);
            await store.DeleteAsync(resolved.DeadLetterId);
        }
    }

    [Fact]
    public async Task QueryAsync_RespectsLimitAcrossAllHandlers()
    {
        var store = CreateStore();
        var first = BuildRow("sink.limit");
        var second = BuildRow("sink.limit");
        await store.WriteAsync(first);
        await store.WriteAsync(second);

        try
        {
            var rows = await store.QueryAsync(failingHandler: null, limit: 1);
            Assert.Single(rows);
        }
        finally
        {
            await store.DeleteAsync(first.DeadLetterId);
            await store.DeleteAsync(second.DeadLetterId);
        }
    }

    [Fact]
    public void Summarize_RedactsEmbeddedStackFramesAndPaths()
    {
        var message = "handler failed at Example.Handler() in /tmp/private/Handler.cs:line 42 path=/srv/private/db.sqlite";

        var summarized = OperatorDiagnostic.Summarize(message);

        Assert.Contains("[stack]", summarized, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/private", summarized, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/private", summarized, StringComparison.Ordinal);
        Assert.DoesNotContain("Example.Handler", summarized, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_RedactsUncPaths()
    {
        var message = @"handler failed at \\fileserver\share\secret.txt";

        var summarized = OperatorDiagnostic.Summarize(message);

        Assert.Contains("[path]", summarized, StringComparison.Ordinal);
        Assert.DoesNotContain("fileserver", summarized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.txt", summarized, StringComparison.OrdinalIgnoreCase);
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

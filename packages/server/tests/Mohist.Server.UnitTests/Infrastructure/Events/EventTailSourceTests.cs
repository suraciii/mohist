using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Infrastructure.Events;

/// <summary>
/// Lower-owner coverage for the project-scoped live event tail source
/// (<see cref="EventTailSource"/>, the production singleton behind
/// <c>GET /api/projects/{projectRef}/events/tail</c>). The HTTP layer keeps
/// only its wire contract (NDJSON shape, 400/404); selection semantics live
/// here: match filtering, strict project isolation, unprojected suppression,
/// envelope-only matching, best-effort no-replay, and subscription release.
/// </summary>
public sealed class EventTailSourceTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryEventTailSource _source = new();

    private static EventMatchExpression CompileMatch(string source)
    {
        var compiled = EventMatchExpression.Compile(source);
        Assert.True(compiled.IsSuccess, compiled.Diagnostic?.Message ?? "match compile failed");
        return compiled.Expression!;
    }

    private static CloudEvent Envelope(
        string projectId,
        string type,
        IReadOnlyDictionary<string, string>? extensions = null,
        JsonElement? payload = null)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectid"] = projectId,
        };
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
                all[key] = value;
        }
        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: string.IsNullOrEmpty(projectId)
                ? new Uri("/mohist/orphan", UriKind.Relative)
                : new Uri($"/mohist/projects/{projectId}/issues/1", UriKind.Relative),
            type: type,
            time: FixedTime,
            data: payload,
            subject: "1",
            extensions: all);
    }

    private static async Task<List<CloudEvent>> ReadAsync(EventTailSubscription subscription, int expected)
    {
        var read = new List<CloudEvent>();
        while (read.Count < expected)
        {
            while (subscription.Reader.TryRead(out var envelope))
                read.Add(envelope);
            if (read.Count < expected)
                await subscription.Reader.ReadAsync();
        }
        return read;
    }

    [Fact]
    public async Task WithoutMatch_DeliversEveryLiveProjectEvent()
    {
        await using var subscription = _source.Open("proj-tail-all", match: null);

        _source.Publish(Envelope("proj-tail-all", "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" }));
        _source.Publish(Envelope("proj-tail-all", "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" }));

        var envelopes = await ReadAsync(subscription, expected: 2);

        Assert.Equal("com.mohist.issue.created", envelopes[0].Type);
        Assert.Equal("com.mohist.issue.completed", envelopes[1].Type);
        Assert.Null(envelopes[0].Data);
    }

    [Fact]
    public async Task WithMatch_DeliversOnlyMatchingEnvelopesAndSuppressesNonMatches()
    {
        await using var subscription = _source.Open(
            "proj-tail-match",
            CompileMatch("event.type == \"com.mohist.issue.completed\""));

        _source.Publish(Envelope("proj-tail-match", "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" }));
        _source.Publish(Envelope("proj-tail-match", "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" }));

        var envelopes = await ReadAsync(subscription, expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("com.mohist.issue.completed", envelopes[0].Type);
    }

    [Fact]
    public async Task StrictIsolation_NeverDeliversOtherProjectEvents()
    {
        await using var subscription = _source.Open("proj-tail-p", match: null);

        _source.Publish(Envelope("proj-tail-q", "com.mohist.issue.created"));
        _source.Publish(Envelope("proj-tail-p", "com.mohist.issue.created"));
        _source.Publish(Envelope("proj-tail-q", "com.mohist.issue.completed"));

        var envelopes = await ReadAsync(subscription, expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("proj-tail-p", envelopes[0].Extensions["projectid"]);
    }

    [Fact]
    public async Task StrictIsolation_NeverDeliversUnprojectedEvents()
    {
        await using var subscription = _source.Open("proj-tail-orphan", match: null);

        var unprojected = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/projects/p", UriKind.Relative),
            type: "com.mohist.orphan",
            time: FixedTime,
            data: null,
            subject: null,
            extensions: new Dictionary<string, string>());
        _source.Publish(unprojected);
        _source.Publish(Envelope("proj-tail-orphan", "com.mohist.issue.created"));

        var envelopes = await ReadAsync(subscription, expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("com.mohist.issue.created", envelopes[0].Type);
    }

    [Fact]
    public async Task Match_DoesNotConsultPayload()
    {
        await using var subscription = _source.Open(
            "proj-tail-payload",
            CompileMatch("event.type == \"com.mohist.issue.completed\""));

        var payloadOnlyWouldMatch = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            type = "com.mohist.issue.completed",
            status = "ok",
        });
        var envelopeTypeCreated = Envelope(
            "proj-tail-payload",
            "com.mohist.issue.created",
            new Dictionary<string, string> { ["issue"] = "1" },
            payload: payloadOnlyWouldMatch);
        _source.Publish(envelopeTypeCreated);
        _source.Publish(Envelope("proj-tail-payload", "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" }));

        var envelopes = await ReadAsync(subscription, expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("com.mohist.issue.completed", envelopes[0].Type);
        Assert.Null(envelopes[0].Data);
    }

    [Fact]
    public async Task EventsBeforeSubscription_AreNotReplayed()
    {
        _source.Publish(Envelope("proj-tail-replay", "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" }));
        _source.Publish(Envelope("proj-tail-replay", "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "2" }));

        await using var subscription = _source.Open("proj-tail-replay", match: null);

        _source.Publish(Envelope("proj-tail-replay", "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "3" }));

        var envelopes = await ReadAsync(subscription, expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("3", envelopes[0].Extensions["issue"]);
    }

    [Fact]
    public async Task Disposal_ReleasesSubscriptionWithoutLeakingChannel()
    {
        Assert.Equal(0, _source.ActiveSubscriptionCount);

        var subscription = _source.Open("proj-tail-release", match: null);
        _source.Publish(Envelope("proj-tail-release", "com.mohist.issue.created"));
        _ = await ReadAsync(subscription, expected: 1);
        Assert.Equal(1, _source.ActiveSubscriptionCount);

        await subscription.DisposeAsync();

        Assert.Equal(0, _source.ActiveSubscriptionCount);
    }
}

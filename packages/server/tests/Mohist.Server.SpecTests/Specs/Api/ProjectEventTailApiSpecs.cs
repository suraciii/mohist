using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Specs for issue-413 T-002 — the project-scoped NDJSON live event
/// tail endpoint (<c>GET /api/projects/{projectRef}/events/tail</c>).
/// Covers match filter, strict project isolation, unprojected
/// suppression, 400-with-location on invalid match, one-line-per-event
/// NDJSON shape, cancellation/release on disconnect, envelope-only
/// matching (payloads never influence the filter), and best-effort
/// no-replay semantics. Driven through the <see cref="IEventTailSource"/>
/// fake — the durable event dispatcher and a wall clock are not
/// touched.
/// </summary>
[Collection("IntegrationApi")]
public class ProjectEventTailApiSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public ProjectEventTailApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    private InMemoryEventTailSource Source =>
        _fixture.Services.GetRequiredService<InMemoryEventTailSource>();

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task WithoutMatch_DeliversEveryLiveProjectEventAsOneLinePerEnvelope()
    {
        var project = await CreateProjectAsync("tail-all");

        using var session = new TailSession(_client, Source, project.Id, match: null);
        session.Open();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "2" });

        var envelopes = await session.WaitForLinesAsync(expected: 2);

        Assert.Equal("com.mohist.issue.created", envelopes[0].Type);
        Assert.Equal("com.mohist.issue.completed", envelopes[1].Type);
        Assert.Equal("1", envelopes[0].Extensions["issue"]);
        Assert.Equal("1", envelopes[1].Extensions["issue"]);
        Assert.False(envelopes[0].HasPayload);
        Assert.False(envelopes[1].HasPayload);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task WithMatch_DeliversOnlyMatchingEnvelopesAndSuppressesNonMatches()
    {
        var project = await CreateProjectAsync("tail-match");

        using var session = new TailSession(
            _client,
            Source,
            project.Id,
            match: "event.type == \"com.mohist.issue.completed\"");
        session.Open();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "2" });
        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "2" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("com.mohist.issue.completed", envelopes[0].Type);
        Assert.Equal("1", envelopes[0].Extensions["issue"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task StrictIsolation_NeverDeliversOtherProjectEvents()
    {
        var projectP = await CreateProjectAsync("tail-isolation-p");
        var projectQ = await CreateProjectAsync("tail-isolation-q");

        using var session = new TailSession(_client, Source, projectP.Id, match: null);
        session.Open();

        Publish(projectQ.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(projectP.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(projectQ.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal(projectP.Id, envelopes[0].Extensions["projectid"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task StrictIsolation_NeverDeliversUnprojectedEvents()
    {
        var project = await CreateProjectAsync("tail-unprojected");

        using var session = new TailSession(_client, Source, project.Id, match: null);
        session.Open();

        Source.Publish(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/projects/p", UriKind.Relative),
            type: "com.mohist.orphan",
            time: FixedTime,
            data: null,
            subject: null,
            extensions: new Dictionary<string, string>()));
        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("com.mohist.issue.created", envelopes[0].Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Match_DoesNotConsultPayload()
    {
        var project = await CreateProjectAsync("tail-payload-isolation");

        using var session = new TailSession(
            _client,
            Source,
            project.Id,
            match: "event.type == \"com.mohist.issue.completed\"");
        session.Open();

        var payloadOnlyWouldMatch = JsonSerializer.SerializeToElement(new
        {
            type = "com.mohist.issue.completed",
            status = "ok",
        });
        Source.Publish(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/projects/{project.Id}/issues/1", UriKind.Relative),
            type: "com.mohist.issue.created",
            time: FixedTime,
            data: payloadOnlyWouldMatch,
            subject: "1",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = project.Id,
                ["issue"] = "1",
            }));

        Publish(project.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("com.mohist.issue.completed", envelopes[0].Type);
        Assert.False(envelopes[0].HasPayload);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task InvalidMatch_Returns400WithStructuredLocationBeforeAnyStream()
    {
        var project = await CreateProjectAsync("tail-400");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/events/tail?match=" + Uri.EscapeDataString("(event.type == \"x\""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(envelope.GetProperty("success").GetBoolean());
        Assert.Equal("invalid_match_expression", envelope.GetProperty("code").GetString());
        var details = envelope.GetProperty("details");
        Assert.Equal(1, details.GetProperty("line").GetInt32());
        Assert.True(details.GetProperty("column").GetInt32() > 0);
        Assert.True(details.GetProperty("offset").GetInt32() > 0);
        Assert.Contains("(event.type ==", details.GetProperty("source").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task StreamContentType_IsNdjsonAndOneLinePerEvent()
    {
        var project = await CreateProjectAsync("tail-shape");

        using var session = new TailSession(_client, Source, project.Id, match: null);
        session.Open();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.commented", new Dictionary<string, string> { ["issue"] = "1" });

        var envelopes = await session.WaitForLinesAsync(expected: 3);

        foreach (var envelope in envelopes)
        {
            Assert.False(envelope.Raw.StartsWith('['));
            Assert.False(envelope.Raw.EndsWith(']'));
            Assert.True(envelope.Raw.Trim().Length > 0);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task EachLineCarriesEnvelopeFieldsAndExtensionsWithoutPayload()
    {
        var project = await CreateProjectAsync("tail-line-shape");

        using var session = new TailSession(_client, Source, project.Id, match: null);
        session.Open();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string>
        {
            ["issue"] = "42",
            ["epic"] = "7",
        });

        var envelopes = await session.WaitForLinesAsync(expected: 1);
        Assert.Single(envelopes);
        var element = JsonSerializer.Deserialize<JsonElement>(envelopes[0].Raw);
        Assert.Equal("com.mohist.issue.created", element.GetProperty("type").GetString());
        Assert.True(element.GetProperty("source").GetString()!.Length > 0);
        Assert.True(element.GetProperty("id").GetString()!.Length > 0);
        Assert.True(element.GetProperty("time").GetString()!.Length > 0);
        Assert.Equal("1.0", element.GetProperty("specversion").GetString());
        Assert.Equal("42", element.GetProperty("subject").GetString());
        var extensions = element.GetProperty("extensions");
        Assert.Equal("42", extensions.GetProperty("issue").GetString());
        Assert.Equal("7", extensions.GetProperty("epic").GetString());
        Assert.Equal(project.Id, extensions.GetProperty("projectid").GetString());
        Assert.False(element.TryGetProperty("data", out _));
        Assert.False(element.TryGetProperty("payload", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Cancellation_ReleasesSubscriptionWithoutLeakingChannel()
    {
        var project = await CreateProjectAsync("tail-cancel");

        Assert.Equal(0, Source.ActiveSubscriptionCount);

        var session = new TailSession(_client, Source, project.Id, match: null);
        session.Open();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        await session.WaitForLinesAsync(expected: 1);
        Assert.Equal(1, Source.ActiveSubscriptionCount);

        await session.CancelAsync();

        Assert.Equal(0, Source.ActiveSubscriptionCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task EventsBeforeSubscription_AreNotReplayed()
    {
        var project = await CreateProjectAsync("tail-no-replay");

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "2" });

        using var session = new TailSession(_client, Source, project.Id, match: null);
        session.Open();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "3" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("3", envelopes[0].Extensions["issue"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj_does_not_exist_{Guid.NewGuid():N}/events/tail");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<ProjectRecord> CreateProjectAsync(string nameSuffix)
    {
        var name = $"{nameSuffix}-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectRecord>(
            "/api/projects", name);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return project;
    }

    private void Publish(
        string projectId,
        string type,
        IReadOnlyDictionary<string, string> extensions)
    {
        var extensionsDict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectid"] = projectId,
        };
        foreach (var kvp in extensions)
        {
            extensionsDict[kvp.Key] = kvp.Value;
        }
        var envelope = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/projects/{projectId}", UriKind.Relative),
            type: type,
            time: FixedTime,
            data: null,
            subject: null,
            extensions: extensionsDict);
        Source.Publish(envelope);
    }

    private sealed record ProjectRecord(string Id, string Name);

    private sealed record ParsedEnvelope(string Type, string Raw, Dictionary<string, string> Extensions, bool HasPayload);

    /// <summary>
    /// Owns one open NDJSON tail session: the HTTP request/response, the
    /// stream-reading task, and the cancellation token source. A
    /// bounded <see cref="Channel{T}"/> buffers decoded lines so tests
    /// can drive events deterministically without sleeping.
    /// </summary>
    private sealed class TailSession : IDisposable
    {
        private readonly HttpClient _client;
        private readonly InMemoryEventTailSource _source;
        private readonly string _projectId;
        private readonly string? _match;
        private CancellationTokenSource? _cts;
        private Channel<string>? _lines;
        private Task? _reader;
        private TaskCompletionSource<HttpResponseMessage>? _responseTcs;
        private int _disposed;

        public TailSession(HttpClient client, InMemoryEventTailSource source, string projectId, string? match)
        {
            _client = client;
            _source = source;
            _projectId = projectId;
            _match = match;
        }

        public void Open()
        {
            if (_cts is not null)
                throw new InvalidOperationException("Session already opened");

            var cts = new CancellationTokenSource();
            var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

            var url = $"/api/projects/{_projectId}/events/tail";
            if (!string.IsNullOrEmpty(_match))
                url += "?match=" + Uri.EscapeDataString(_match);

            // TestHost buffers the entire response before returning
            // from SendAsync, so the request must be issued from a
            // background thread that reads the body as the endpoint
            // produces it. We wait for the endpoint to register its
            // subscription (via IEventTailSource.ActiveSubscriptionCount)
            // on a dedicated thread before returning, so the caller can
            // publish events that the endpoint will see.
            var initialCount = _source.ActiveSubscriptionCount;
            var responseTcs = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var reader = Task.Run(async () =>
            {
                HttpResponseMessage? response = null;
                try
                {
                    response = await _client
                        .SendAsync(new HttpRequestMessage(HttpMethod.Get, url), cts.Token)
                        .ConfigureAwait(false);
                    responseTcs.TrySetResult(response);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.NotNull(response.Content.Headers.ContentType);
                    Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);

                    await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var textReader = new StreamReader(stream, Encoding.UTF8);
                    while (!cts.IsCancellationRequested)
                    {
                        var line = await textReader.ReadLineAsync().ConfigureAwait(false);
                        if (line is null)
                            break;
                        if (line.Length == 0)
                            continue;
                        await lines.Writer.WriteAsync(line, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    responseTcs.TrySetCanceled(cts.Token);
                }
                catch (Exception ex)
                {
                    responseTcs.TrySetException(ex);
                }
                finally
                {
                    lines.Writer.TryComplete();
                }
            }, cts.Token);

            // Wait for the endpoint to register its subscription on a
            // dedicated thread so the request thread (running the
            // endpoint) is not blocked. The ActiveSubscriptionCount
            // transition is the deterministic test-side signal that
            // gates when the test may publish events.
            Task.Run(() =>
            {
                while (_source.ActiveSubscriptionCount <= initialCount
                    && !responseTcs.Task.IsCompleted)
                {
                    Thread.Yield();
                }
            }).GetAwaiter().GetResult();

            _cts = cts;
            _lines = lines;
            _reader = reader;
            _responseTcs = responseTcs;
        }

        public async Task<List<ParsedEnvelope>> WaitForLinesAsync(int expected)
        {
            if (_lines is null)
                throw new InvalidOperationException("Session not opened");
            var envelopes = new List<ParsedEnvelope>(expected);
            for (var i = 0; i < expected; i++)
            {
                var line = await _lines.Reader.ReadAsync().ConfigureAwait(false);
                envelopes.Add(Parse(line));
            }
            return envelopes;
        }

        public async Task CancelAsync()
        {
            if (_disposed != 0 || _cts is null)
                return;
            _cts.Cancel();
            if (_reader is not null)
            {
                try { await _reader.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_cts is null)
                return;
            try
            {
                _cts.Cancel();
                _reader?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            if (_responseTcs is { Task.IsCompletedSuccessfully: true })
            {
                _responseTcs.Task.GetAwaiter().GetResult().Dispose();
            }
            _cts.Dispose();
        }

        private static ParsedEnvelope Parse(string line)
        {
            var element = JsonSerializer.Deserialize<JsonElement>(line);
            var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (element.TryGetProperty("extensions", out var extElement)
                && extElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in extElement.EnumerateObject())
                    extensions[property.Name] = property.Value.GetString() ?? string.Empty;
            }
            return new ParsedEnvelope(
                Type: element.GetProperty("type").GetString() ?? string.Empty,
                Raw: line,
                Extensions: extensions,
                HasPayload: element.TryGetProperty("data", out _) || element.TryGetProperty("payload", out _));
        }
    }
}
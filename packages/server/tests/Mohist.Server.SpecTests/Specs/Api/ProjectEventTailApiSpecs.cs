using System.Net;
using System.Net.Http.Json;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Services;
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

    [Fact]
    public async Task WithoutMatch_DeliversEveryLiveProjectEventAsOneLinePerEnvelope()
    {
        var project = await CreateProjectAsync("tail-all");

        await using var session = new TailSession(Source, project.Id, match: null);
        await session.OpenAsync();

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

    [Fact]
    public async Task WithMatch_DeliversOnlyMatchingEnvelopesAndSuppressesNonMatches()
    {
        var project = await CreateProjectAsync("tail-match");

        await using var session = new TailSession(
            Source,
            project.Id,
            match: "event.type == \"com.mohist.issue.completed\"");
        await session.OpenAsync();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "2" });
        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "2" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("com.mohist.issue.completed", envelopes[0].Type);
        Assert.Equal("1", envelopes[0].Extensions["issue"]);
    }

    [Fact]
    public async Task StrictIsolation_NeverDeliversOtherProjectEvents()
    {
        var projectP = await CreateProjectAsync("tail-isolation-p");
        var projectQ = await CreateProjectAsync("tail-isolation-q");

        await using var session = new TailSession(Source, projectP.Id, match: null);
        await session.OpenAsync();

        Publish(projectQ.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(projectP.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(projectQ.Id, "com.mohist.issue.completed", new Dictionary<string, string> { ["issue"] = "1" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal(projectP.Id, envelopes[0].Extensions["projectid"]);
    }

    [Fact]
    public async Task StrictIsolation_NeverDeliversUnprojectedEvents()
    {
        var project = await CreateProjectAsync("tail-unprojected");

        await using var session = new TailSession(Source, project.Id, match: null);
        await session.OpenAsync();

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

    [Fact]
    public async Task Match_DoesNotConsultPayload()
    {
        var project = await CreateProjectAsync("tail-payload-isolation");

        await using var session = new TailSession(
            Source,
            project.Id,
            match: "event.type == \"com.mohist.issue.completed\"");
        await session.OpenAsync();

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

    [Fact]
    public async Task StreamContentType_IsNdjsonAndOneLinePerEvent()
    {
        var project = await CreateProjectAsync("tail-shape");

        await using var session = new TailSession(Source, project.Id, match: null);
        await session.OpenAsync();

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

    [Fact]
    public async Task EachLineCarriesEnvelopeFieldsAndExtensionsWithoutPayload()
    {
        var project = await CreateProjectAsync("tail-line-shape");

        await using var session = new TailSession(Source, project.Id, match: null);
        await session.OpenAsync();

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

    [Fact]
    public async Task Cancellation_ReleasesSubscriptionWithoutLeakingChannel()
    {
        var project = await CreateProjectAsync("tail-cancel");

        Assert.Equal(0, Source.ActiveSubscriptionCount);

        await using var session = new TailSession(Source, project.Id, match: null);
        await session.OpenAsync();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        await session.WaitForLinesAsync(expected: 1);
        Assert.Equal(1, Source.ActiveSubscriptionCount);

        await session.CancelAsync();

        Assert.Equal(0, Source.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task EventsBeforeSubscription_AreNotReplayed()
    {
        var project = await CreateProjectAsync("tail-no-replay");

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "1" });
        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "2" });

        await using var session = new TailSession(Source, project.Id, match: null);
        await session.OpenAsync();

        Publish(project.Id, "com.mohist.issue.created", new Dictionary<string, string> { ["issue"] = "3" });

        var envelopes = await session.WaitForLinesAsync(expected: 1);

        Assert.Single(envelopes);
        Assert.Equal("3", envelopes[0].Extensions["issue"]);
    }

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
        extensions.TryGetValue("issue", out var subject);
        var envelope = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/projects/{projectId}", UriKind.Relative),
            type: type,
            time: FixedTime,
            data: null,
            subject: subject,
            extensions: extensionsDict);
        Source.Publish(envelope);
    }

    private sealed record ProjectRecord(string Id, string Name);

    private sealed record ParsedEnvelope(string Type, string Raw, Dictionary<string, string> Extensions, bool HasPayload);

    /// <summary>
    /// Owns one open NDJSON tail session: the in-memory response pipe,
    /// the handler task running <see cref="ProjectEventTailRoutes.HandleTailAsync"/>,
    /// the line reader, and the cancellation token source. A
    /// <see cref="Channel{T}"/> buffers decoded lines so tests can drive
    /// events deterministically without sleeping.
    /// </summary>
    /// <remarks>
    /// The handler is invoked directly against a <see cref="DefaultHttpContext"/>
    /// rather than through HTTP. <c>WebApplicationFactory&lt;Program&gt;.CreateClient()</c>
    /// returns an HttpClient backed by TestServer, which buffers the entire
    /// response before returning from <c>SendAsync</c>; the streaming
    /// response then never completes under that client, and the spec test
    /// deadlocks. Invoking the handler directly with the project already
    /// resolved on <c>HttpContext.Items</c> lets the test own its own
    /// pipe without going through the buffered HTTP pipeline.
    /// </remarks>
    private sealed class TailSession : IAsyncDisposable
    {
        private readonly InMemoryEventTailSource _source;
        private readonly string _projectId;
        private readonly string? _match;
        private CancellationTokenSource? _cts;
        private Channel<string>? _lines;
        private Task? _reader;
        private Task? _handler;
        private Pipe? _pipe;
        private int _disposed;

        public TailSession(InMemoryEventTailSource source, string projectId, string? match)
        {
            _source = source;
            _projectId = projectId;
            _match = match;
        }

        public async Task OpenAsync()
        {
            if (_cts is not null)
                throw new InvalidOperationException("Session already opened");

            var cts = new CancellationTokenSource();
            var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

            var pipe = new Pipe();
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var openedObserver = _source.ObserveSubscriptionOpened(projectId =>
            {
                if (projectId == _projectId)
                    opened.TrySetResult();
            });

            var httpContext = new DefaultHttpContext
            {
                RequestAborted = cts.Token,
            };
            httpContext.Response.Body = pipe.Writer.AsStream(leaveOpen: true);
            httpContext.Items[ProjectResolutionEndpointFilter.ProjectInfoItemKey] =
                new ProjectInfo { Id = _projectId };

            var handler = Task.Run(async () =>
            {
                try
                {
                    await ProjectEventTailRoutes.HandleTailAsync(
                        httpContext, _match, _source, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
                finally
                {
                    openedObserver.Dispose();
                    pipe.Writer.Complete();
                }
            });

            await opened.Task.ConfigureAwait(false);

            var reader = Task.Run(async () =>
            {
                try
                {
                    using var textReader = new StreamReader(
                        pipe.Reader.AsStream(leaveOpen: true),
                        Encoding.UTF8);
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
                }
                finally
                {
                    lines.Writer.TryComplete();
                }
            });

            _cts = cts;
            _lines = lines;
            _reader = reader;
            _handler = handler;
            _pipe = pipe;
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
            if (_handler is not null)
            {
                try { await _handler.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (_reader is not null)
            {
                try { await _reader.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_cts is null)
                return;
            _cts.Cancel();
            if (_handler is not null)
            {
                try { await _handler.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (_reader is not null)
            {
                try { await _reader.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
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

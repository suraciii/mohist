using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Webhooks;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Webhooks;
using Mohist.Server.Webhooks.Domain;
using Mohist.Server.Webhooks.Services;
using Mohist.Server.Webhooks.Subscriptions;
using Xunit;

namespace Mohist.Server.UnitTests.Webhooks;

public sealed class WebhookDispatchHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public void RenderProducesStructuredModeCloudEventWithFlattenedExtensions()
    {
        var payload = new WebhookPayloadRenderer().Render(Event());

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("specversion").GetString());
        Assert.Equal("evt-1", root.GetProperty("id").GetString());
        Assert.Equal("mohist://projects/proj-1", root.GetProperty("source").GetString());
        Assert.Equal("com.mohist.issue.completed", root.GetProperty("type").GetString());
        Assert.Equal("2026-08-01T12:00:00+00:00", root.GetProperty("time").GetString());
        Assert.Equal("issue-42", root.GetProperty("subject").GetString());
        Assert.Equal("application/json", root.GetProperty("datacontenttype").GetString());
        Assert.Equal(42, root.GetProperty("data").GetProperty("issueNumber").GetInt32());
        Assert.Equal("proj-1", root.GetProperty("projectid").GetString());
        Assert.Equal("custom-value", root.GetProperty("custom").GetString());
        Assert.False(root.TryGetProperty("extensions", out _));
    }


    [Fact]
    public async Task HandleAsyncPostsStructuredCloudEventAndSignsWhenSubscriptionHasSecret()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddSubscriptionAsync("hook-1", "event.type == \"com.mohist.issue.completed\"", "https://hooks.test/signed");
        var secrets = new FakeSecretStore();
        secrets.Set("proj-1", "hook-1", "shared-secret"u8.ToArray());
        var handler = new RecordingHttpMessageHandler();
        using var http = new HttpClient(handler);
        using var services = CreateServices(database, secrets, new WebhookHttpClient(http));
        var dispatch = CreateHandler(services);

        await dispatch.HandleAsync(Event(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://hooks.test/signed", request.Uri);
        Assert.Equal("application/cloudevents+json", request.ContentType);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("1.0", payload.RootElement.GetProperty("specversion").GetString());
        Assert.Equal("evt-1", payload.RootElement.GetProperty("id").GetString());
        Assert.Equal("mohist://projects/proj-1", payload.RootElement.GetProperty("source").GetString());
        Assert.Equal("com.mohist.issue.completed", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("issue-42", payload.RootElement.GetProperty("subject").GetString());
        Assert.Equal("application/json", payload.RootElement.GetProperty("datacontenttype").GetString());
        Assert.Equal("proj-1", payload.RootElement.GetProperty("projectid").GetString());
        Assert.Equal("42", payload.RootElement.GetProperty("issue").GetString());
        Assert.Equal("custom-value", payload.RootElement.GetProperty("custom").GetString());
        Assert.False(payload.RootElement.TryGetProperty("extensions", out _));
        Assert.Equal(42, payload.RootElement.GetProperty("data").GetProperty("issueNumber").GetInt32());
        Assert.Equal(Sign(request.Body, "shared-secret"u8), request.Signature);
    }

    [Fact]
    public async Task HandleAsyncOmitsSignatureForNoSecretAndSkipsNonMatchingSubscription()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddSubscriptionAsync("unsigned", "event.type == \"com.mohist.issue.completed\"", "https://hooks.test/unsigned");
        await database.AddSubscriptionAsync("other", "event.type == \"com.mohist.issue.created\"", "https://hooks.test/other");
        var handler = new RecordingHttpMessageHandler();
        using var http = new HttpClient(handler);
        using var services = CreateServices(database, new FakeSecretStore(), new WebhookHttpClient(http));
        var dispatch = CreateHandler(services);

        await dispatch.HandleAsync(Event(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://hooks.test/unsigned", request.Uri);
        Assert.Null(request.Signature);
    }

    [Fact]
    public async Task HandleAsyncFansOutToEveryMatchingSubscription()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddSubscriptionAsync("first", "event.type == \"com.mohist.issue.completed\"", "https://hooks.test/first");
        await database.AddSubscriptionAsync("second", "event.issue == \"42\"", "https://hooks.test/second");
        var handler = new RecordingHttpMessageHandler();
        using var http = new HttpClient(handler);
        using var services = CreateServices(database, new FakeSecretStore(), new WebhookHttpClient(http));
        var dispatch = CreateHandler(services);

        await dispatch.HandleAsync(Event(), CancellationToken.None);

        Assert.Equal(
            ["https://hooks.test/first", "https://hooks.test/second"],
            handler.Requests.Select(request => request.Uri).Order());
    }

    [Fact]
    public async Task HandleAsyncRecordsFailureAndContinuesWithOtherMatchingSubscriptions()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddSubscriptionAsync("fails", "event.type == \"com.mohist.issue.completed\"", "https://hooks.test/fails");
        await database.AddSubscriptionAsync("succeeds", "event.type == \"com.mohist.issue.completed\"", "https://hooks.test/succeeds");
        var handler = new RecordingHttpMessageHandler(uri => uri.EndsWith("/fails", StringComparison.Ordinal)
            ? HttpStatusCode.InternalServerError
            : HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        var time = new FakeTimeProvider(Now);
        using var services = CreateServices(database, new FakeSecretStore(), new WebhookHttpClient(http), time);
        var dispatch = CreateHandler(services, time);

        await dispatch.HandleAsync(Event(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        await using var context = await database.Factory.CreateDbContextAsync();
        var failure = Assert.Single(await context.WebhookDeliveryFailures.ToListAsync());
        Assert.Equal("fails", failure.SubscriptionId);
        Assert.Equal("evt-1", failure.EventId);
        Assert.Equal("com.mohist.issue.completed", failure.EventType);
        Assert.Equal("https://hooks.test/fails", failure.TargetUrl);
        Assert.Equal(500, failure.ResponseStatus);
        Assert.Equal(Now, failure.OccurredAt);
        Assert.Equal("endpoint responded 500", failure.ErrorSummary);
    }

    [Fact]
    public async Task HandleAsyncDeliversOnlySelectedEventTypes()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddSubscriptionAsync("selected", "", "https://hooks.test/selected",
            eventSelectionMode: "selected", eventTypes: ["com.mohist.issue.created"]);
        await database.AddSubscriptionAsync("all", "", "https://hooks.test/all");
        var handler = new RecordingHttpMessageHandler();
        using var http = new HttpClient(handler);
        using var services = CreateServices(database, new FakeSecretStore(), new WebhookHttpClient(http));
        var dispatch = CreateHandler(services);

        await dispatch.HandleAsync(Event(), CancellationToken.None); // Event is com.mohist.issue.completed

        // "selected" subscribes to issue.created only, so a .completed event is not delivered to it.
        var delivered = handler.Requests.Select(r => r.Uri).Order();
        Assert.Equal(["https://hooks.test/all"], delivered);
    }

    [Fact]
    public async Task HandleAsyncAppliesCustomHeaderAuthFromSecretStore()
    {
        await using var database = await TestDatabase.CreateAsync();
        var subscription = await database.AddSubscriptionAsync(
            "buzz", "", "https://hooks.test/buzz",
            authType: "custom");
        var secrets = new FakeSecretStore();
        // Custom-header credential stored under the v1 auth address (subscriptionId + ":auth").
        secrets.Set("proj-1", subscription + ":auth",
            Encoding.UTF8.GetBytes("{\"X-Webhook-Secret\":\"buzz-secret-value\"}"));
        var handler = new RecordingHttpMessageHandler();
        using var http = new HttpClient(handler);
        using var services = CreateServices(database, secrets, new WebhookHttpClient(http));
        var dispatch = CreateHandler(services);

        await dispatch.HandleAsync(Event(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("buzz-secret-value", request.CustomHeader);
        Assert.Null(request.Signature); // v1 does not sign unless a legacy signing secret is present
    }

    private static WebhookDispatchHandler CreateHandler(ServiceProvider services, TimeProvider? time = null) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            time ?? new FakeTimeProvider(Now),
            NullLogger<WebhookDispatchHandler>.Instance);

    private static ServiceProvider CreateServices(TestDatabase database, ISecretStore secrets, IWebhookHttpClient client, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MohistDbContext>>(database.Factory);
        services.AddSingleton(time ?? new FakeTimeProvider(Now));
        services.AddSingleton<ISecretStore>(secrets);
        services.AddSingleton<IWebhookHttpClient>(client);
        services.AddSingleton<WebhookPayloadRenderer>();
        services.AddScoped<WebhookSubscriptionStore>();
        return services.BuildServiceProvider();
    }

    private static CloudEvent Event() => new(
        "evt-1",
        new Uri("mohist://projects/proj-1"),
        "com.mohist.issue.completed",
        Now,
        JsonSerializer.SerializeToElement(new { issueNumber = 42, title = "completed" }, CloudEvent.JsonOptions),
        subject: "issue-42",
        extensions: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = "proj-1",
            [EventCatalog.Lineage.Issue] = "42",
            ["custom"] = "custom-value",
        });

    private static string Sign(string body, ReadOnlySpan<byte> secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private sealed class TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options) : IAsyncDisposable
    {
        public IDbContextFactory<MohistDbContext> Factory { get; } = new TestContextFactory(options);

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(connection).Options;
            await using var context = new MohistDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, options);
        }

        public async Task<string> AddSubscriptionAsync(
            string id, string match, string targetUrl,
            string eventSelectionMode = "all", string[]? eventTypes = null, string authType = "none")
        {
            await using var context = await Factory.CreateDbContextAsync();
            context.WebhookSubscriptions.Add(new WebhookSubscriptionRow
            {
                Id = id,
                ProjectId = "proj-1",
                Name = id,
                Match = match,
                TargetUrl = targetUrl,
                Status = WebhookSubscriptionStatus.Active,
                EventSelectionMode = eventSelectionMode,
                EventTypes = System.Text.Json.JsonSerializer.Serialize(eventTypes ?? []),
                AuthType = authType,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await context.SaveChangesAsync();
            return id;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestContextFactory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MohistDbContext(options));
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _secrets = [];

        public void Set(string projectId, string subscriptionId, byte[] secret) =>
            _secrets[new SecretStoreAddress(projectId, subscriptionId, SecretKind.WebhookSecret)] = secret;

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _secrets[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.TryGetValue(address, out var secret) ? secret : null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class RecordingHttpMessageHandler(Func<string, HttpStatusCode>? responseStatus = null) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.Method,
                uri,
                request.Content?.Headers.ContentType?.MediaType,
                body,
                request.Headers.TryGetValues(WebhookHttpClient.SignatureHeader, out var values) ? values.SingleOrDefault() : null,
                request.Headers.TryGetValues("X-Webhook-Secret", out var custom) ? custom.SingleOrDefault() : null));
            return new HttpResponseMessage(responseStatus?.Invoke(uri) ?? HttpStatusCode.NoContent);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? ContentType,
        string Body,
        string? Signature,
        string? CustomHeader);
}

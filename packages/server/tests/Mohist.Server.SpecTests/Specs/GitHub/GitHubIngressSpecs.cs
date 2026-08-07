using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("IntegrationRunner")]
public sealed class GitHubIngressSpecs(MohistIntegrationFixture fixture)
{
    private HttpClient Client => fixture.Client;

    private const string RepoName = "hello-world";

    private static readonly string LabeledPayload = """
        {
          "action": "labeled",
          "number": 42,
          "issue": {
            "number": 42,
            "title": "Fix the bug",
            "state": "open",
            "labels": [ { "name": "mohist" } ]
          },
          "repository": {
            "name": "hello-world",
            "full_name": "octocat/hello-world",
            "owner": { "login": "octocat" }
          }
        }
        """;

    private static string Sign(byte[] payload, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();

    private static string UniqueOwner() => $"octocat-{Guid.NewGuid():N}";

    private async Task<(ProjectInfo Project, JsonElement Connection)> ConnectNewAsync(string? owner = null)
    {
        owner ??= UniqueOwner();
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-ingress-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = RepoName });
        return (project, created);
    }

    private async Task<HttpResponseMessage> DeliverAsync(
        string connectionId,
        string secret,
        string eventHeader,
        string deliveryId,
        string payload,
        string? signature = null,
        bool includeSignature = true)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventHeader);
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        if (includeSignature)
            request.Headers.Add("X-Hub-Signature-256", signature ?? Sign(bytes, secret));
        return await Client.SendAsync(request);
    }

    private async Task<IReadOnlyList<IngressEventRow>> ReadIngressRowsAsync(string projectId, string connectionId)
    {
        var source = $"/mohist/projects/{projectId}/github-connections/{connectionId}";
        var dbFactory = fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.IngressEvents.AsNoTracking().Where(r => r.Source == source).ToListAsync();
    }

    private static string Extension(IngressEventRow row, string key) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.ExtensionsJson)![key];

    [Fact]
    public async Task LabeledEvent_WithValidSignature_EntersEventStreamWithCatalogType()
    {
        var (project, connection) = await ConnectNewAsync();
        var connectionId = connection.GetProperty("id").GetString()!;
        var secret = connection.GetProperty("webhookSecret").GetString()!;

        using var response = await DeliverAsync(
            connectionId, secret, "issues", "delivery-labeled-1", LabeledPayload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await ReadIngressRowsAsync(project.Id, connectionId));
        Assert.Equal(EventCatalog.ReverseDns.GitHubIssuesLabeled, row.Type);
        Assert.Equal("delivery-labeled-1", row.EventId);
        Assert.Equal($"/mohist/projects/{project.Id}/github-connections/{connectionId}", row.Source);
        Assert.Equal(project.Id, Extension(row, EventCatalog.Lineage.ProjectId));
        Assert.Equal("octocat/hello-world", Extension(row, EventCatalog.Lineage.GitHubRepo));
        Assert.Equal("42", Extension(row, EventCatalog.Lineage.GitHubIssue));
        Assert.Equal(LabeledPayload, row.Data.GetRawText());
    }

    [Theory]
    [InlineData("issues", "closed")]
    [InlineData("issues", "reopened")]
    public async Task IssueLifecycleEvents_MapToTheirCatalogTypes(string header, string action)
    {
        var (project, connection) = await ConnectNewAsync();
        var connectionId = connection.GetProperty("id").GetString()!;
        var payload = LabeledPayload.Replace("\"labeled\"", $"\"{action}\"");

        using var response = await DeliverAsync(
            connectionId,
            connection.GetProperty("webhookSecret").GetString()!,
            header,
            $"delivery-{action}-1",
            payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await ReadIngressRowsAsync(project.Id, connectionId));
        var expected = action == "closed"
            ? EventCatalog.ReverseDns.GitHubIssuesClosed
            : EventCatalog.ReverseDns.GitHubIssuesReopened;
        Assert.Equal(expected, row.Type);
    }

    [Fact]
    public async Task WrongSignature_Returns401_WithoutPersistingEvent()
    {
        var (project, connection) = await ConnectNewAsync();
        var connectionId = connection.GetProperty("id").GetString()!;

        using var response = await DeliverAsync(
            connectionId,
            connection.GetProperty("webhookSecret").GetString()!,
            "issues",
            "delivery-bad-sig",
            LabeledPayload,
            signature: Sign(Encoding.UTF8.GetBytes(LabeledPayload), "wrong-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await ReadIngressRowsAsync(project.Id, connectionId));
    }

    [Fact]
    public async Task MissingSignatureHeader_Returns401_WithoutPersistingEvent()
    {
        var (project, connection) = await ConnectNewAsync();
        var connectionId = connection.GetProperty("id").GetString()!;

        using var response = await DeliverAsync(
            connectionId,
            connection.GetProperty("webhookSecret").GetString()!,
            "issues",
            "delivery-no-sig",
            LabeledPayload,
            includeSignature: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await ReadIngressRowsAsync(project.Id, connectionId));
    }

    [Fact]
    public async Task DuplicateDelivery_SameDeliveryId_AppendsBothRows()
    {
        var (project, connection) = await ConnectNewAsync();
        var connectionId = connection.GetProperty("id").GetString()!;
        var secret = connection.GetProperty("webhookSecret").GetString()!;

        using var first = await DeliverAsync(connectionId, secret, "issues", "delivery-dup-1", LabeledPayload);
        using var second = await DeliverAsync(connectionId, secret, "issues", "delivery-dup-1", LabeledPayload);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var rows = await ReadIngressRowsAsync(project.Id, connectionId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("delivery-dup-1", row.EventId));
    }

    [Fact]
    public async Task PingEvent_Returns200_WithoutPersistingEvent()
    {
        var (project, connection) = await ConnectNewAsync();
        var connectionId = connection.GetProperty("id").GetString()!;

        using var response = await DeliverAsync(
            connectionId,
            connection.GetProperty("webhookSecret").GetString()!,
            "ping",
            "delivery-ping-1",
            """{ "zen": "keep it simple" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadIngressRowsAsync(project.Id, connectionId));
    }

    [Fact]
    public async Task Connect_UnregisteredRepository_RejectedWithGuidance()
    {
        var owner = UniqueOwner();
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-ingress-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");

        using var response = await Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/github-connections",
            new { owner, repo = "not-registered" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("repository_not_registered", body.GetProperty("code").GetString());
        Assert.Contains("register the repository first", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledConnection_RejectsIngress()
    {
        var (project, connection) = await ConnectNewAsync();
        var connectionId = connection.GetProperty("id").GetString()!;
        var secret = connection.GetProperty("webhookSecret").GetString()!;
        await Client.PostOkAsync($"/api/projects/{project.Id}/github-connections/{connectionId}/disable");

        using var response = await DeliverAsync(connectionId, secret, "issues", "delivery-disabled-1", LabeledPayload);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await ReadIngressRowsAsync(project.Id, connectionId));
    }
}

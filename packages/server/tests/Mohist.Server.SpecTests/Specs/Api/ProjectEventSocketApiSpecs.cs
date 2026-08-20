using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationRunner")]
public sealed class ProjectEventSocketApiSpecs(MohistIntegrationFixture fixture)
{
    [Fact]
    public async Task BearerUpgradeAcceptsSubscriptionWithoutOrigin()
    {
        var project = await CreateProjectAsync();
        var client = fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers.Authorization = $"Bearer {MohistIntegrationFixture.OperatorToken}";
        using var socket = await client.ConnectAsync(
            new Uri($"ws://localhost/api/projects/{project.Id}/events/socket"),
            TestContext.Current.CancellationToken);

        await socket.SendAsync(Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","id":"set","method":"subscription.set","params":{"domain":null,"transcript":null,"taskLogs":[]}}
            """), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
        var response = JsonDocument.Parse(buffer.AsMemory(0, received.Count)).RootElement;
        Assert.Equal("set", response.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, response.GetProperty("result").ValueKind);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CookieUpgradeRequiresMatchingOrigin()
    {
        var project = await CreateProjectAsync();
        var missingOrigin = CookieClient(origin: null);
        await Assert.ThrowsAnyAsync<Exception>(() => missingOrigin.ConnectAsync(
            new Uri($"ws://localhost/api/projects/{project.Id}/events/socket"),
            TestContext.Current.CancellationToken));

        var matchingOrigin = CookieClient("http://localhost");
        using var socket = await matchingOrigin.ConnectAsync(
            new Uri($"ws://localhost/api/projects/{project.Id}/events/socket"),
            TestContext.Current.CancellationToken);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CutoverRemovesLegacyLiveEndpoints()
    {
        var project = await CreateProjectAsync();
        using var tail = await fixture.Client.GetAsync(
            $"/api/projects/{project.Id}/events/tail",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, tail.StatusCode);
        var routes = fixture.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.DoesNotContain("/hubs/events", routes);
    }

    [Fact]
    public async Task SocketRouteRequiresAuthenticationAndUpgrade()
    {
        var project = await CreateProjectAsync();
        using var nonUpgrade = await fixture.Client.GetAsync(
            $"/api/projects/{project.Id}/events/socket",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, nonUpgrade.StatusCode);

        var anonymous = fixture.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.ConnectAsync(
            new Uri($"ws://localhost/api/projects/{project.Id}/events/socket"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SocketRouteRejectsEveryQueryParameter()
    {
        var project = await CreateProjectAsync();
        using var response = await fixture.Client.GetAsync(
            $"/api/projects/{project.Id}/events/socket?projectId={project.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("query_not_supported", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task IntegrationCredentialCannotUpgradeAnotherProject()
    {
        var ownProject = await CreateProjectAsync();
        var otherProject = await CreateProjectAsync();
        var token = CredentialToken.Generate(CredentialKind.Integration);
        await fixture.Services.GetRequiredService<ICredentialStore>().CreateAsync(new Credential(
            $"credential-{Guid.NewGuid():N}",
            "integration-spec",
            CredentialKind.Integration,
            CredentialToken.Hash(token),
            [Scope.Readonly],
            "event-socket-spec",
            CredentialToken.DisplayPrefix(token),
            ownProject.Id,
            null,
            null,
            null,
            fixture.TimeProvider.GetUtcNow()));

        var wrongProject = fixture.CreateWebSocketClient();
        wrongProject.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
        await Assert.ThrowsAnyAsync<Exception>(() => wrongProject.ConnectAsync(
            new Uri($"ws://localhost/api/projects/{otherProject.Id}/events/socket"),
            TestContext.Current.CancellationToken));

        var own = fixture.CreateWebSocketClient();
        own.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
        using var socket = await own.ConnectAsync(
            new Uri($"ws://localhost/api/projects/{ownProject.Id}/events/socket"),
            TestContext.Current.CancellationToken);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    private Microsoft.AspNetCore.TestHost.WebSocketClient CookieClient(string? origin)
    {
        var client = fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = $"mohist_session={MohistIntegrationFixture.OperatorToken}";
            if (origin is not null) request.Headers["Origin"] = origin;
        };
        return client;
    }

    private Task<ProjectInfo> CreateProjectAsync() =>
        fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects",
            $"event-socket-{Guid.NewGuid():N}");
}

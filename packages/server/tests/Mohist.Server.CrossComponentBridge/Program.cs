using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Specs.Slack;
using Mohist.Server.SpecTests.Support;

const string operatorToken = MohistIntegrationFixture.OperatorToken;
const string adapterId = SlackRuntimeLeaseTestSupport.AdapterId;
const string projectId = "cross-component-project";
const string connectionId = "cross-component-connection";
const string agentId = "cross-component-agent";
const string workspaceTeamId = "T-cross-component";
const string appId = "A-cross-component";
const string botUserId = "U-cross-component";
const string appToken = "xapp-cross-component";
const string botToken = "xoxb-cross-component";

var input = Console.In;
var output = Console.Out;
var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
var databaseName = $"cross-component-{Guid.NewGuid():N}";
await using var keeper = new SqliteConnection($"Data Source={databaseName};Mode=Memory;Cache=Shared");
await keeper.OpenAsync();
MigratedSqliteTemplate.CopyTo(keeper);

var factory = new CrossComponentWebApplicationFactory(
    keeper.ConnectionString,
    "/mohist-tests/cross-component/runner",
    "/mohist-tests/cross-component/system-update.json",
    "/mohist-tests/cross-component/logs",
    time);
await factory.EnsureSchemaAsync();
_ = factory.Services;
await SeedAsync(factory.Services, time);

var client = factory.CreateClient();
client.DefaultRequestHeaders.Add("Authorization", $"Bearer {operatorToken}");
client.DefaultRequestHeaders.Add(SlackAdapterOperatorAuthenticator.OperatorIdHeaderName, adapterId);
Write(new { type = "ready" });
while (true)
{
    var line = await input.ReadLineAsync();
    if (line is null) break;

    try
    {
        var message = JsonSerializer.Deserialize<BridgeMessage>(line, JSON.Options)
            ?? throw new InvalidOperationException("Bridge message was empty.");
        if (message.Type == "stop") break;
        if (message.Type == "scenario")
        {
            await ApplyScenarioAsync(message.Scenario);
            Write(new { type = "scenarioApplied", scenario = message.Scenario });
            continue;
        }
        if (message.Type == "inspect")
        {
            await WriteSnapshotAsync();
            continue;
        }
        if (message.Type != "request" || message.Id is null || message.Url is null)
            continue;

        var response = await ForwardAsync(client, message);
        Write(new
        {
            type = "response",
            id = message.Id,
            status = (int)response.StatusCode,
            responseBody = await response.Content.ReadAsStringAsync(),
        });
    }
    catch (Exception error)
    {
        Write(new { type = "error", message = error.Message });
    }
}

factory.Dispose();

async Task<HttpResponseMessage> ForwardAsync(HttpClient http, BridgeMessage request)
{
    JsonElement? body = request.Body is null ? null : JsonSerializer.Deserialize<JsonElement>(request.Body, JSON.Options);
    var path = new Uri(request.Url!, UriKind.Absolute).AbsolutePath;
    if (path.EndsWith("/targets", StringComparison.Ordinal))
    {
        return await http.GetAsync("/api/slack-adapter/leases/targets");
    }

    if (path.EndsWith("/acquire", StringComparison.Ordinal))
    {
        return await http.PostAsJsonAsync("/api/slack-adapter/leases/acquire", body, JSON.Options);
    }

    if (path.EndsWith("/hello", StringComparison.Ordinal))
    {
        return await http.PostAsJsonAsync("/api/slack-adapter/leases/hello", body, JSON.Options);
    }

    if (path.EndsWith("/renew", StringComparison.Ordinal))
    {
        return await http.PostAsJsonAsync("/api/slack-adapter/leases/renew", body, JSON.Options);
    }

    if (path.EndsWith("/ingress", StringComparison.Ordinal))
    {
        return await http.PostAsJsonAsync(
            $"/api/projects/{projectId}/slack-connections/{connectionId}/ingress",
            body,
            JSON.Options);
    }

    if (path.EndsWith("/deliveries/claim-uncertain", StringComparison.Ordinal))
    {
        return await http.PostAsJsonAsync(
            $"/api/projects/{projectId}/slack-connections/{connectionId}/deliveries/claim-uncertain",
            body,
            JSON.Options);
    }

    if (path.EndsWith("/deliveries/claim", StringComparison.Ordinal))
    {
        return await http.PostAsJsonAsync(
            $"/api/projects/{projectId}/slack-connections/{connectionId}/deliveries/claim",
            body,
            JSON.Options);
    }

    if (path.EndsWith("/deliveries/ack", StringComparison.Ordinal))
    {
        return await http.PostAsJsonAsync(
            $"/api/projects/{projectId}/slack-connections/{connectionId}/deliveries/ack",
            body,
            JSON.Options);
    }

    throw new InvalidOperationException($"Bridge does not forward {request.Method} {path}.");
}

async Task ApplyScenarioAsync(string? scenario)
{
    await using var scope = factory.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
    var connection = await db.AgentConnections.SingleAsync(row => row.Id == connectionId);
    connection.ConnectionHealth = scenario switch
    {
        "backpressure" => ConnectionHealthKind.Degraded,
        "server-owned" => ConnectionHealthKind.Unhealthy,
        _ => ConnectionHealthKind.Healthy,
    };
    connection.HealthReason = scenario switch
    {
        "backpressure" => SlackProviderBackpressureReasons.OutboxOverflow,
        "server-owned" => "Slack service is offline.",
        _ => null,
    };
    await db.SaveChangesAsync();
}

async Task WriteSnapshotAsync()
{
    await using var scope = factory.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
    var rows = await db.SlackOutboxRows.AsNoTracking()
        .Where(row => row.ConnectionId == connectionId)
        .ToListAsync();
    var inbox = await db.SlackProviderInboxRows.AsNoTracking()
        .CountAsync(row => row.ConnectionId == connectionId);
    var sessions = await db.AgentSessions.AsNoTracking()
        .CountAsync(row => row.LabelConnectionId == connectionId);
    var jobs = await db.AgentJobs.AsNoTracking()
        .CountAsync(row => row.ProjectId == projectId);
    Write(new
    {
        type = "snapshot",
        outboxCount = rows.Count,
        nudgeCount = rows.Count(row => row.Kind == SlackOutboxKinds.UserAction),
        deliveredNudgeCount = rows.Count(row => row.Kind == SlackOutboxKinds.UserAction && row.State == SlackOutboxStates.Delivered),
        inboxCount = inbox,
        sessionCount = sessions,
        jobCount = jobs,
    });
}

async Task SeedAsync(IServiceProvider services, FakeTimeProvider clock)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
    var now = clock.GetUtcNow();
    db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
    db.Agents.Add(new AgentRow
    {
        Id = agentId,
        ProjectId = projectId,
        Name = agentId,
        Status = AgentStatus.Active,
        State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentId,
            Status = AgentStatus.Active,
            Instructions = "Handle Slack requests.",
            AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
        }, JSON.Options),
    });
    await db.SaveChangesAsync();
    db.AgentConnections.Add(new AgentConnectionRow
    {
        Id = connectionId,
        ProjectId = projectId,
        AgentId = agentId,
        ProviderKind = ConnectionProviderKind.Slack,
        WorkspaceTeamId = workspaceTeamId,
        AppId = appId,
        BotUserId = botUserId,
        BotName = "Cross component Bot",
        SetupProgress = SetupProgressKind.Complete,
        DesiredState = DesiredStateKind.Enabled,
        ConnectionHealth = ConnectionHealthKind.Healthy,
        AgentReadiness = AgentReadinessKind.Ready,
        OwnerSlackUserId = "U-owner",
        LastHeartbeatAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    });
    db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
    {
        Id = "cross-component-enrollment",
        WorkspaceTeamId = workspaceTeamId,
        Lifecycle = SlackEnrollmentLifecycle.Active,
        ManagerCapability = SlackManagerCapability.Available,
        PlanCode = "unknown",
        AuditJson = "[]",
        CreatedAt = now,
        UpdatedAt = now,
    });
    db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
    {
        Id = "cross-component-agent-app",
        EnrollmentId = "cross-component-enrollment",
        WorkspaceTeamId = workspaceTeamId,
        AgentConnectionId = connectionId,
        AppId = appId,
        BotUserId = botUserId,
        AppLifecycle = SlackAppLifecycle.Created,
        Authorization = SlackAuthorizationState.Authorized,
        DesiredManifestVersion = 1,
        DesiredManifestHash = "cross-component",
        VerifiedScopesJson = "[]",
        RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
        BindingState = SlackAgentAppBindingState.Bound,
        AuditJson = "[]",
        CreatedAt = now,
        UpdatedAt = now,
    });
    await db.SaveChangesAsync();

    var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
    await secrets.StoreAsync(
        SecretStoreAddress.ForManagedSlackAgentApp("cross-component-agent-app", SecretKind.AppToken),
        System.Text.Encoding.UTF8.GetBytes(appToken));
    await secrets.StoreAsync(
        SecretStoreAddress.ForManagedSlackAgentApp("cross-component-agent-app", SecretKind.BotToken),
        System.Text.Encoding.UTF8.GetBytes(botToken));
}

void Write(object value) => output.WriteLine(JsonSerializer.Serialize(value, JSON.Options));

sealed record BridgeMessage(string Type, int? Id, string? Url, string? Method, string? Body, string? Scenario);

sealed class CrossComponentWebApplicationFactory(
    string connectionString,
    string runnerRoot,
    string systemUpdateStatePath,
    string logsPath,
    FakeTimeProvider timeProvider)
    : MohistWebApplicationFactory(connectionString, runnerRoot, systemUpdateStatePath, logsPath, timeProvider)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(AppContext.BaseDirectory);
        base.ConfigureWebHost(builder);
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Slack;

public sealed record SlackSeedOptions
{
    public string? ProjectId { get; init; }
    public string WorkspaceTeamId { get; init; } = "T123";
    public string OwnerSlackUserId { get; init; } = "U_OWNER";
    public string AppId { get; init; } = "A123";
    public string AgentNameSuffix { get; init; } = "";
    public string? BotUserId { get; init; }
    public string? ManagedAppAppId { get; init; }
    public string AppToken { get; init; } = "xapp";
    public string BotToken { get; init; } = "xoxb";
    public string? ConnectionAppToken { get; init; }
    public string? ConnectionBotToken { get; init; }
    public bool WriteConnectionSecrets { get; init; } = true;
    public bool WriteConnectionAppSecret { get; init; } = true;
    public bool WithAgent { get; init; } = true;
    public bool WithManagedApp { get; init; } = true;
    public bool WithRuntimeLease { get; init; } = true;
    public string? AgentName { get; init; }
    public IReadOnlyList<string>? AllowedMembers { get; init; }
    public string AccessPolicy { get; init; } = AccessPolicyKind.OwnerOnly;
    public string AgentInstructions { get; init; } = "Handle Slack requests.";
}

/// <summary>
/// Fully provisioned Slack connection plus its runtime lease id, ready for
/// adapter-facing route calls that require a lease.
/// </summary>
public sealed record SeededSlackConnection(AgentConnection Connection, string LeaseId);

/// <summary>
/// Single owner of the Slack managed-connection test seed: rows, secrets,
/// and the runtime lease must be written by one unit because the lease
/// credential fingerprint is derived from the stored secret values. Specs
/// keep locally constructed abnormal states (stale leases, non-owner
/// members, conflicting generations) — those are the contract under test,
/// not setup.
/// </summary>
public static class SlackManagedConnectionSeed
{
    public static async Task<SeededSlackConnection> CreateAsync(
        MohistIntegrationFixture fixture, SlackSeedOptions? options = null)
    {
        options ??= new SlackSeedOptions();
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = options.ProjectId ?? $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var agentName = options.AgentName
            ?? (options.AgentNameSuffix.Length == 0
                ? "Mohist Agent"
                : $"Mohist Agent {options.AgentNameSuffix}");
        var botName = options.AgentNameSuffix.Length == 0
            ? "Mohist"
            : $"Mohist {options.AgentNameSuffix}".Trim();
        var botUserId = options.BotUserId ?? DeriveBotUserId(options.AgentNameSuffix);
        var now = fixture.TimeProvider.GetUtcNow();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var projectExists = options.ProjectId is not null
            && await db.Projects.AnyAsync(project => project.Id == projectId);
        if (!projectExists)
        {
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        if (options.WithAgent)
        {
            db.Agents.Add(new AgentRow
            {
                Id = agentId,
                ProjectId = projectId,
                Name = agentName,
                Status = AgentStatus.Active,
                State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
                {
                    Id = agentId,
                    ProjectId = projectId,
                    Name = agentName,
                    Status = AgentStatus.Active,
                    Instructions = options.AgentInstructions,
                    AgentConfig = JsonSerializer.SerializeToElement(
                        new { model = "openai/gpt-4o", runtime = "opencode" }),
                }, JSON.Options),
            });
        }

        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = options.WorkspaceTeamId,
            AppId = options.AppId,
            BotUserId = botUserId,
            BotName = botName,
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = options.OwnerSlackUserId,
            AccessPolicy = options.AccessPolicy,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var managedAppRowId = $"agent_app_{Guid.NewGuid():N}";
        string? enrollmentId = null;
        if (options.WithManagedApp)
        {
            enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(
                fixture, options.WorkspaceTeamId);
            db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
            {
                Id = managedAppRowId,
                EnrollmentId = enrollmentId,
                WorkspaceTeamId = options.WorkspaceTeamId,
                AgentConnectionId = id,
                AppId = options.ManagedAppAppId ?? $"A_SPEC_{Guid.NewGuid():N}",
                BotUserId = botUserId,
                AppLifecycle = SlackAppLifecycle.Created,
                Authorization = SlackAuthorizationState.Authorized,
                RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
                DesiredManifestVersion = 1,
                DesiredManifestHash = "desired",
                VerifiedScopesJson = "[]",
                OperationFence = 0,
                AppLevelTokenRef = managedAppRowId,
                BotTokenRef = managedAppRowId,
                BindingState = SlackAgentAppBindingState.Bound,
                AuditJson = "[]",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        if (options.AllowedMembers is { Count: > 0 })
        {
            foreach (var member in options.AllowedMembers)
            {
                db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
                {
                    Id = $"slkalm_{Guid.NewGuid():N}",
                    ProjectId = projectId,
                    ConnectionId = id,
                    SlackUserId = member,
                    WorkspaceTeamId = options.WorkspaceTeamId,
                    CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var writes = new List<SecretStoreWrite>();
        if (options.WriteConnectionSecrets && options.WriteConnectionAppSecret)
        {
            writes.Add(new SecretStoreWrite(
                new SecretStoreAddress(projectId, id, SecretKind.AppToken),
                Encoding.UTF8.GetBytes(options.ConnectionAppToken ?? options.AppToken)));
        }

        if (options.WriteConnectionSecrets)
        {
            writes.Add(new SecretStoreWrite(
                new SecretStoreAddress(projectId, id, SecretKind.BotToken),
                Encoding.UTF8.GetBytes(options.ConnectionBotToken ?? options.BotToken)));
        }

        if (options.WithManagedApp)
        {
            writes.Add(new SecretStoreWrite(
                SecretStoreAddress.ForManagedSlackAgentApp(managedAppRowId, SecretKind.AppToken),
                Encoding.UTF8.GetBytes(options.AppToken)));
            writes.Add(new SecretStoreWrite(
                SecretStoreAddress.ForManagedSlackAgentApp(managedAppRowId, SecretKind.BotToken),
                Encoding.UTF8.GetBytes(options.BotToken)));
        }

        if (writes.Count > 0)
        {
            await secrets.StoreAtomicallyAsync(writes);
        }

        var leaseId = options.WithRuntimeLease
            ? await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(fixture, projectId, id)
            : "";

        var connection = new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = options.WorkspaceTeamId,
            AppId = options.AppId,
            BotUserId = botUserId,
            BotName = botName,
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = options.OwnerSlackUserId,
            AccessPolicy = options.AccessPolicy,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return new SeededSlackConnection(connection, leaseId);
    }

    private static string DeriveBotUserId(string agentNameSuffix)
    {
        if (agentNameSuffix.Length == 0)
            return "U123";
        return $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
    }
}

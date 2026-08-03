using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Services;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class ManagerActorAccessDecider : IScopedService
{
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly ProjectQuerier _projects;
    private readonly AgentQuerier _agents;
    private readonly AgentConnectionStore _connections;

    public ManagerActorAccessDecider(
        SlackWorkspaceEnrollmentStore enrollments,
        ProjectQuerier projects,
        AgentQuerier agents,
        AgentConnectionStore connections)
    {
        _enrollments = enrollments;
        _projects = projects;
        _agents = agents;
        _connections = connections;
    }

    public async Task<ManagerActorAuthentication> AuthenticateAsync(
        string workspaceTeamId,
        string slackUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceTeamId)
            || string.IsNullOrWhiteSpace(slackUserId))
            return ManagerActorAuthentication.Deny;

        var enrollment = await _enrollments.GetActiveByTeamAsync(workspaceTeamId, ct);
        if (enrollment is null
            || enrollment.ManagerCapability != SlackManagerCapability.Available
            || enrollment.ManagerReadiness != SlackManagerReadiness.Ready
            || string.IsNullOrWhiteSpace(enrollment.ManagerActorId)
            || !string.Equals(enrollment.ClaimedSlackUserId, slackUserId, StringComparison.Ordinal))
            return ManagerActorAuthentication.Deny;

        return new(
            true,
            null,
            new ManagerActorContext(
                enrollment.Id,
                enrollment.WorkspaceTeamId,
                enrollment.ManagerActorId,
                slackUserId));
    }

    public async Task<ManagerAccessDecision> AuthorizeAsync(
        ManagerActorContext actor,
        ManagerResourceTarget? target = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var authentication = await AuthenticateAsync(actor.WorkspaceTeamId, actor.SlackUserId, ct);
        if (!authentication.Allowed
            || authentication.Actor is null
            || !string.Equals(authentication.Actor.EnrollmentId, actor.EnrollmentId, StringComparison.Ordinal)
            || !string.Equals(authentication.Actor.ManagerActorId, actor.ManagerActorId, StringComparison.Ordinal))
            return ManagerAccessDecision.Deny("manager_actor_not_authorized");

        if (target is null)
            return ManagerAccessDecision.Allow;
        if (string.IsNullOrWhiteSpace(target.ProjectId))
            return ManagerAccessDecision.Deny("manager_resource_not_found");

        var exists = target.Kind switch
        {
            ManagerResourceKinds.Project => await ProjectExistsAsync(target.ProjectId),
            ManagerResourceKinds.Agent => await AgentExistsAsync(target.ProjectId, target.ResourceId, ct),
            ManagerResourceKinds.Connection => await ConnectionExistsAsync(actor, target, ct),
            _ => false,
        };
        return exists
            ? ManagerAccessDecision.Allow
            : ManagerAccessDecision.Deny("manager_resource_not_found");
    }

    private async Task<bool> ProjectExistsAsync(string projectId) =>
        await _projects.GetByIdAsync(projectId) is not null;

    private async Task<bool> AgentExistsAsync(string projectId, string? agentId, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(agentId)
        && await _agents.GetByIdAsync(projectId, agentId, ct) is not null;

    private async Task<bool> ConnectionExistsAsync(
        ManagerActorContext actor,
        ManagerResourceTarget target,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.ResourceId))
            return false;
        var connection = await _connections.GetAsync(target.ProjectId, target.ResourceId, ct);
        return connection is not null
            && connection.ProviderKind == ConnectionProviderKind.Slack
            && string.Equals(connection.WorkspaceTeamId, actor.WorkspaceTeamId, StringComparison.Ordinal);
    }
}

public sealed record ManagerActorContext(
    string EnrollmentId,
    string WorkspaceTeamId,
    string ManagerActorId,
    string SlackUserId);

public sealed record ManagerActorAuthentication(
    bool Allowed,
    string? Reason,
    ManagerActorContext? Actor = null)
{
    public static ManagerActorAuthentication Deny { get; } = new(false, "manager_actor_not_authorized");
}

public sealed record ManagerAccessDecision(bool Allowed, string? Reason)
{
    public static ManagerAccessDecision Allow { get; } = new(true, null);

    public static ManagerAccessDecision Deny(string reason) => new(false, reason);
}

public static class ManagerResourceKinds
{
    public const string Project = "project";
    public const string Agent = "agent";
    public const string Connection = "connection";
}

public sealed record ManagerResourceTarget(
    string Kind,
    string ProjectId,
    string? ResourceId = null);

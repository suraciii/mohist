using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManagerToolAuthorization : IScopedService
{
    private readonly ManagerActorAccessDecider _access;

    public SlackManagerToolAuthorization(ManagerActorAccessDecider access) => _access = access;

    public async Task<SlackManagerToolDecision> AuthorizeAsync(
        ManagerActorContext actor,
        string tool,
        ManagerResourceTarget? target = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var toolDecision = EvaluateTool(tool);
        if (!toolDecision.Allowed)
            return toolDecision;

        var access = await _access.AuthorizeAsync(actor, target, ct);
        return access.Allowed
            ? SlackManagerToolDecision.Allow
            : SlackManagerToolDecision.Deny(access.Reason ?? "manager_actor_not_authorized");
    }

    public static SlackManagerToolDecision EvaluateTool(string? tool)
    {
        if (!SlackManagerAgentTools.IsAllowed(tool))
        {
            var reason = SlackManagerAgentTools.IsForbidden(tool)
                ? "manager_tool_not_available"
                : "manager_tool_not_authorized";
            return SlackManagerToolDecision.Deny(reason);
        }
        return SlackManagerToolDecision.Allow;
    }
}

public sealed record SlackManagerToolDecision(bool Allowed, string? Reason)
{
    public static SlackManagerToolDecision Allow { get; } = new(true, null);

    public static SlackManagerToolDecision Deny(string reason) => new(false, reason);
}

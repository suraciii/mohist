using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

/// <summary>
/// Identity fences for the Session's mutation entry points. Every append or
/// replay on an existing Session must first prove the caller carries the
/// accepted Project/Agent tuple, so an unattributable or foreign command can
/// never mutate owner facts.
/// </summary>
public sealed partial class AgentSessionGrain
{
    private static void EnsureExpectedFollowupIdentity(
        AgentSession session,
        AcceptFollowupCommand command)
    {
        var hasExpectedProject = command.ExpectedProjectId is not null;
        var hasExpectedAgent = command.ExpectedAgentId is not null;
        if (!hasExpectedProject && !hasExpectedAgent)
            return;
        if (!hasExpectedProject
            || !hasExpectedAgent
            || string.IsNullOrWhiteSpace(command.ExpectedProjectId)
            || string.IsNullOrWhiteSpace(command.ExpectedAgentId))
        {
            throw new ArgumentException(
                "Expected project and Agent identities must be supplied together.",
                nameof(command));
        }

        EnsureSessionIdentity(session, command.ExpectedProjectId, command.ExpectedAgentId);
    }

    private static void EnsureInitialLaunchIdentity(
        AgentSession session,
        EnsureInitialLaunchCommand command)
    {
        var expectedProjectId = command.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var expectedAgentId = command.Metadata?.Label(GenericAgentSessionMetadata.AgentId);
        if (string.IsNullOrWhiteSpace(expectedProjectId) || string.IsNullOrWhiteSpace(expectedAgentId))
        {
            throw new ArgumentException(
                "Initial launch on an existing AgentSession requires the canonical project and Agent identity labels.",
                nameof(command));
        }

        EnsureSessionIdentity(session, expectedProjectId!, expectedAgentId!);
    }

    private static void EnsureSessionIdentity(
        AgentSession session,
        string expectedProjectId,
        string expectedAgentId)
    {
        var actualProjectId = session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var actualAgentId = session.Metadata.Label(GenericAgentSessionMetadata.AgentId);
        if (!string.Equals(actualProjectId, expectedProjectId, StringComparison.Ordinal)
            || !string.Equals(actualAgentId, expectedAgentId, StringComparison.Ordinal))
        {
            throw new AgentSessionIdentityMismatchException(
                session.Id,
                expectedProjectId,
                expectedAgentId,
                actualProjectId,
                actualAgentId);
        }
    }
}

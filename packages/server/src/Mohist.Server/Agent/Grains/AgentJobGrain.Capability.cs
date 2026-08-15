using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private bool MatchesCapabilityExpectation(
        CapabilityClaimExpectation expectation,
        WorkDispatch dispatch,
        string runnerId)
    {
        if (!string.Equals(expectation.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
            || !string.Equals(expectation.OwnerId, Key, StringComparison.Ordinal)
            || !string.Equals(expectation.WorkId, State.WorkId, StringComparison.Ordinal)
            || !string.Equals(expectation.WorkId, dispatch.WorkId, StringComparison.Ordinal))
        {
            return false;
        }

        var definition = dispatch.AgentDefinition;
        return definition is not null
            && string.Equals(definition.Runtime, expectation.Runtime, StringComparison.Ordinal)
            && string.Equals(definition.Model, expectation.Model, StringComparison.Ordinal)
            && string.Equals(definition.ReasoningEffort, expectation.ReasoningEffort, StringComparison.Ordinal)
            && string.Equals(definition.Variant, expectation.Variant, StringComparison.Ordinal)
            && (expectation.CapabilityRevision is null || expectation.CapabilityRevision.Length > 0)
            && !string.IsNullOrWhiteSpace(runnerId);
    }
}

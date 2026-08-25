using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    private static bool MatchesExecutionBinding(
        AgentExecutionBinding expected,
        AgentExecutionBinding actual) =>
        string.Equals(expected.TaskRunId, actual.TaskRunId, StringComparison.Ordinal)
        && string.Equals(expected.WorkId, actual.WorkId, StringComparison.Ordinal)
        && string.Equals(expected.RunnerId, actual.RunnerId, StringComparison.Ordinal)
        && string.Equals(expected.AgentSessionId, actual.AgentSessionId, StringComparison.Ordinal)
        && string.Equals(expected.AgentTurnId, actual.AgentTurnId, StringComparison.Ordinal)
        && string.Equals(expected.Runtime, actual.Runtime, StringComparison.Ordinal)
        && string.Equals(expected.RuntimeSessionId, actual.RuntimeSessionId, StringComparison.Ordinal);

    private static bool MatchesReceiptBinding(
        AgentResultSettlement settlement,
        RuntimeRecoveryReceipt receipt) =>
        string.Equals(settlement.TaskRunId, receipt.TaskRunId, StringComparison.Ordinal)
        && string.Equals(settlement.WorkId, receipt.WorkId, StringComparison.Ordinal)
        && string.Equals(settlement.RunnerId, receipt.RunnerId, StringComparison.Ordinal)
        && string.Equals(settlement.AgentSessionId, receipt.AgentSessionId, StringComparison.Ordinal)
        && string.Equals(settlement.AgentTurnId, receipt.AgentTurnId, StringComparison.Ordinal)
        && string.Equals(settlement.Runtime, receipt.Runtime, StringComparison.Ordinal)
        && string.Equals(settlement.RuntimeSessionId, receipt.RuntimeSessionId, StringComparison.Ordinal);
}

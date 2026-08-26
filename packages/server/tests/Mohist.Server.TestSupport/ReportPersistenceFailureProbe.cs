using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.TestSupport;

public sealed class ReportPersistenceFailureProbe :
    IWorkflowReportPersistenceFailureInjector,
    IAgentJobReportPersistenceFailureInjector
{
    private readonly object _gate = new();
    private readonly HashSet<(string OwnerId, string WorkId)> _workflowFailures = [];
    private readonly HashSet<(string OwnerId, string WorkId)> _agentJobFailures = [];

    public void FailNextWorkflowReport(string workflowRunId, string workId)
    {
        lock (_gate)
            _workflowFailures.Add((workflowRunId, workId));
    }

    public void FailNextAgentJobReport(string agentJobId, string workId)
    {
        lock (_gate)
            _agentJobFailures.Add((agentJobId, workId));
    }

    void IWorkflowReportPersistenceFailureInjector.BeforePersist(string workflowRunId, string workId)
    {
        lock (_gate)
        {
            if (_workflowFailures.Remove((workflowRunId, workId)))
                throw new DbUpdateConcurrencyException("Injected workflow report persistence conflict.");
        }
    }

    void IAgentJobReportPersistenceFailureInjector.BeforePersist(string agentJobId, string workId)
    {
        lock (_gate)
        {
            if (_agentJobFailures.Remove((agentJobId, workId)))
                throw new AgentJobLedgerConflictException("Injected AgentJob report persistence conflict.");
        }
    }
}

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowReportPersistenceFailureInjector
{
    void BeforePersist(string workflowRunId, string workId);
}

public sealed class NoopWorkflowReportPersistenceFailureInjector : IWorkflowReportPersistenceFailureInjector
{
    public static NoopWorkflowReportPersistenceFailureInjector Instance { get; } = new();

    private NoopWorkflowReportPersistenceFailureInjector()
    {
    }

    public void BeforePersist(string workflowRunId, string workId)
    {
    }
}

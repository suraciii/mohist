using Mohist.Server.Agent.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Grains;

public static class TestRunnerGenerationExtensions
{
    public const string ProcessGeneration = "test-generation";

    public static Task RegisterAsync(this IRunnerGrain runner, RunnerInfo info) =>
        runner.RegisterAsync(info, ProcessGeneration);

    public static Task<RunnerPollAdmission> TryBeginPollAsync(this IRunnerGrain runner) =>
        runner.TryBeginPollAsync(ProcessGeneration);

    public static Task<WorkItem?> TryClaimWorkflowAsync(
        this IRunnerGrain runner,
        string workflowRunId,
        string? projectId,
        bool assignWorker) =>
        runner.TryClaimWorkflowAsync(workflowRunId, projectId, assignWorker, ProcessGeneration);

    public static Task<ClaimResult?> TryClaimAgentJobAsync(
        this IRunnerGrain runner,
        string agentJobId,
        string? projectId,
        CapabilityClaimExpectation? expectation = null) =>
        runner.TryClaimAgentJobAsync(agentJobId, projectId, expectation, ProcessGeneration);

    public static Task<WorkItem?> ClaimNextAsync(this IWorkflowGrain workflow, string workerId) =>
        workflow.ClaimNextAsync(workerId, ProcessGeneration);

    public static Task<ClaimResult?> ClaimNextAsync(this IAgentJobGrain job, string runnerId) =>
        job.ClaimNextAsync(runnerId, ProcessGeneration);

    public static Task<ClaimResult?> ClaimNextAsync(
        this IAgentJobGrain job,
        string runnerId,
        CapabilityClaimExpectation expectation) =>
        job.ClaimNextAsync(runnerId, ProcessGeneration, expectation);
}

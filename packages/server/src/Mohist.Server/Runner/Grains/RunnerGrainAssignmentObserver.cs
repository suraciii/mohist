namespace Mohist.Server.Runner.Grains;

public interface IRunnerGrainAssignmentObserver
{
    Task AssignmentAdmissionAsync(string runnerId, WorkDispatch work);
}

public sealed class NoopRunnerGrainAssignmentObserver : IRunnerGrainAssignmentObserver
{
    public static NoopRunnerGrainAssignmentObserver Instance { get; } = new();

    private NoopRunnerGrainAssignmentObserver()
    {
    }

    public Task AssignmentAdmissionAsync(string runnerId, WorkDispatch work) => Task.CompletedTask;
}

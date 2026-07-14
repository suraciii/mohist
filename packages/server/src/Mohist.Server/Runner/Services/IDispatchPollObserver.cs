namespace Mohist.Server.Runner.Services;

public interface IDispatchPollObserver
{
    Task AfterRunnerInfoAsync(string runnerId);
}

public sealed class NoopDispatchPollObserver : IDispatchPollObserver
{
    public static NoopDispatchPollObserver Instance { get; } = new();

    private NoopDispatchPollObserver()
    {
    }

    public Task AfterRunnerInfoAsync(string runnerId) => Task.CompletedTask;
}

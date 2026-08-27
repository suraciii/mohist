namespace Mohist.Server.Runner.Services;

public interface IRunnerControlTransport
{
    bool IsConnected(string runnerId);

    Task<TResult> SendRequestAsync<TParams, TResult>(
        string runnerId,
        string method,
        TParams parameters,
        Action? requestEnqueued = null,
        CancellationToken ct = default);

    Task SendNotificationAsync<TParams>(
        string runnerId,
        string method,
        TParams parameters,
        CancellationToken ct = default);
}

public interface IRunnerSessionCommandTransport
{
    Task<string> GetCurrentProcessGenerationAsync(string runnerId, CancellationToken ct = default);

    Task<bool> IsCurrentProcessGenerationAsync(
        string runnerId,
        string processGeneration,
        CancellationToken ct = default);

    Task<TResult> SendRequestAsync<TParams, TResult>(
        string runnerId,
        string processGeneration,
        string method,
        TParams parameters,
        CancellationToken ct = default);
}

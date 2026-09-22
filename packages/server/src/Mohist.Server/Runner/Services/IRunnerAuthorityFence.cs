namespace Mohist.Server.Runner.Services;

/// <summary>
/// Fences the Server-side transport authority for one Runner. This does not
/// claim that a remote process or external side effect physically stopped.
/// </summary>
public interface IRunnerAuthorityFence
{
    Task<RunnerAuthorityFenceResult> FenceAsync(
        string runnerId,
        string? processGeneration,
        CancellationToken ct = default);
}

public sealed record RunnerAuthorityFenceResult(IReadOnlyList<string> DisconnectedSessionIds)
{
    public static RunnerAuthorityFenceResult Empty { get; } = new([]);
}

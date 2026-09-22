using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Boundary signal emitted after authentication has resolved the concrete
/// Runner request authority and before registration or control admission.
/// Production has no observer; deterministic tests use it to hold the exact
/// authenticated request across credential replacement.
/// </summary>
public sealed class RunnerAuthorityAdmissionObserver : ISingletonService
{
    internal Func<
        string,
        string,
        RunnerPresentedAuthority,
        CancellationToken,
        Task>? ObservedAsync { get; set; }

    public Task ObserveAsync(
        string operation,
        string runnerId,
        RunnerPresentedAuthority presentedAuthority,
        CancellationToken ct) =>
        ObservedAsync?.Invoke(operation, runnerId, presentedAuthority, ct)
        ?? Task.CompletedTask;
}

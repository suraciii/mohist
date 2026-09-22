using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Boundary signals for deterministic administrative-removal persistence tests.
/// Production has no observer.
/// </summary>
public sealed class RunnerAdministrativeRemovalObserver : ISingletonService
{
    internal Func<string, CancellationToken, Task>? BeforeIntentWriteAsync { get; set; }
    internal Func<string, CancellationToken, Task>? AfterIntentWriteAsync { get; set; }
    internal Func<string, CancellationToken, Task>? IntentRecordedAsync { get; set; }

    public Task BeforeIntentWrite(string runnerId, CancellationToken ct = default) =>
        BeforeIntentWriteAsync?.Invoke(runnerId, ct) ?? Task.CompletedTask;

    public Task AfterIntentWrite(string runnerId, CancellationToken ct = default) =>
        AfterIntentWriteAsync?.Invoke(runnerId, ct) ?? Task.CompletedTask;

    public Task IntentRecorded(string runnerId, CancellationToken ct = default) =>
        IntentRecordedAsync?.Invoke(runnerId, ct) ?? Task.CompletedTask;

    internal void Reset()
    {
        BeforeIntentWriteAsync = null;
        AfterIntentWriteAsync = null;
        IntentRecordedAsync = null;
    }
}

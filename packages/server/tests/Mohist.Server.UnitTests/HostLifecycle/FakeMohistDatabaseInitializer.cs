using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.UnitTests.HostLifecycle;

/// <summary>
/// Records every initialization invocation together with the supplied
/// service provider. Tests register expected services, return value
/// outcomes, and per-call exception behavior.
/// </summary>
public sealed class FakeMohistDatabaseInitializer : IMohistDatabaseInitializer
{
    private readonly List<InitializationInvocation> _invocations = new();
    private readonly Queue<Func<Task>> _outcomes = new();

    public IReadOnlyList<InitializationInvocation> Invocations => _invocations;
    public int InvocationCount => _invocations.Count;

    public FakeMohistDatabaseInitializer EnqueueSuccess() =>
        Enqueue(static () => Task.CompletedTask);

    public FakeMohistDatabaseInitializer EnqueueFailure(Exception exception) =>
        Enqueue(() => throw exception);

    public FakeMohistDatabaseInitializer Enqueue(Func<Task> outcome)
    {
        _outcomes.Enqueue(outcome);
        return this;
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : (Func<Task>)(static () => Task.CompletedTask);
        _invocations.Add(new InitializationInvocation(services, cancellationToken));
        await outcome().ConfigureAwait(false);
    }

    public sealed record InitializationInvocation(IServiceProvider Services, CancellationToken CancellationToken);
}

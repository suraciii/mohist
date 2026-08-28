using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.GitHub.Infrastructure;

/// <summary>
/// Per-connection coordination between outbound GitHub sends and connection
/// status transitions. A send and a Disable commit never overlap: whichever
/// acquires the connection's gate first wins. A send re-reads the connection
/// status inside the gate, so a Disable that commits first defers the send;
/// a send that won the gate settles before the waiting Disable commits.
/// The AsyncLocal guard lets a send's own failure handling (a credential
/// failure persists Disabled) re-enter the gate it already holds instead of
/// deadlocking.
/// </summary>
public sealed class GitHubConnectionGate : ISingletonService
{
    private static readonly AsyncLocal<string?> Current = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public Task<T> EnterAsync<T>(
        string connectionId,
        Func<CancellationToken, Task<T>> body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(body);
        if (string.Equals(Current.Value, connectionId, StringComparison.Ordinal))
            return body(ct);
        return EnterCoreAsync(connectionId, body, ct);
    }

    public Task EnterAsync(
        string connectionId,
        Func<CancellationToken, Task> body,
        CancellationToken ct = default) =>
        EnterAsync<object?>(connectionId, async token =>
        {
            await body(token);
            return null;
        }, ct);

    private async Task<T> EnterCoreAsync<T>(
        string connectionId,
        Func<CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(connectionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var previous = Current.Value;
            Current.Value = connectionId;
            try
            {
                return await body(ct);
            }
            finally
            {
                Current.Value = previous;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

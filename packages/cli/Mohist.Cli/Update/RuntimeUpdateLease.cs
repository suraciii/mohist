using System.Collections.Concurrent;

namespace Mohist.Cli;

/// <summary>
/// Holds an exclusive update transaction for one managed component. The real
/// implementation uses an operating-system file handle, so process exit releases
/// a stale owner without a timer or a recovery poll.
/// </summary>
internal interface IRuntimeUpdateLease : IDisposable
{
    ManagedRuntimeComponent Component { get; }
}

internal interface IRuntimeUpdateLeaseProvider
{
    IRuntimeUpdateLease? TryAcquire(ManagedRuntimeComponent component, string componentRoot);
}

internal sealed class RuntimeUpdateLeaseProvider : IRuntimeUpdateLeaseProvider
{
    private readonly IRuntimeUpdateLeaseProvider _inner;

    public RuntimeUpdateLeaseProvider(IFileSystem files)
    {
        _inner = files is RealFileSystem
            ? new FileRuntimeUpdateLeaseProvider()
            : new InMemoryRuntimeUpdateLeaseProvider();
    }

    public IRuntimeUpdateLease? TryAcquire(ManagedRuntimeComponent component, string componentRoot) =>
        _inner.TryAcquire(component, componentRoot);
}

internal sealed class FileRuntimeUpdateLeaseProvider : IRuntimeUpdateLeaseProvider
{
    public IRuntimeUpdateLease? TryAcquire(ManagedRuntimeComponent component, string componentRoot)
    {
        var lockPath = Path.Combine(componentRoot, ".update.lock");
        try
        {
            Directory.CreateDirectory(componentRoot);
            var handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new FileRuntimeUpdateLease(component, handle);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class FileRuntimeUpdateLease(ManagedRuntimeComponent component, FileStream handle) : IRuntimeUpdateLease
    {
        private FileStream? _handle = handle;

        public ManagedRuntimeComponent Component { get; } = component;

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }
}

/// <summary>
/// Deterministic test implementation. Tests that need to coordinate multiple
/// operations share one provider explicitly; production never uses this path.
/// </summary>
internal sealed class InMemoryRuntimeUpdateLeaseProvider : IRuntimeUpdateLeaseProvider
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    public IRuntimeUpdateLease? TryAcquire(ManagedRuntimeComponent component, string componentRoot)
    {
        var key = $"{component}:{Path.GetFullPath(componentRoot)}";
        return _held.TryAdd(key, 0) ? new Lease(component, key, _held) : null;
    }

    private sealed class Lease(
        ManagedRuntimeComponent component,
        string key,
        ConcurrentDictionary<string, byte> held) : IRuntimeUpdateLease
    {
        private ConcurrentDictionary<string, byte>? _held = held;

        public ManagedRuntimeComponent Component { get; } = component;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _held, null);
            held?.TryRemove(key, out _);
        }
    }
}

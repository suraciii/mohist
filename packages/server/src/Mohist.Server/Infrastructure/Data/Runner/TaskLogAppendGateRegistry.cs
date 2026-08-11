namespace Mohist.Server.Infrastructure.Data.Runner;

internal readonly record struct TaskLogIdentity(string OwnerKind, string OwnerId, string WorkId);

internal sealed class TaskLogAppendGateRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<TaskLogIdentity, AppendGate> _gates = [];

    public Lease Acquire(TaskLogIdentity identity)
    {
        lock (_sync)
        {
            if (!_gates.TryGetValue(identity, out var gate))
            {
                gate = new AppendGate();
                _gates.Add(identity, gate);
            }

            gate.Users++;
            return new Lease(this, identity, gate);
        }
    }

    private void Release(TaskLogIdentity identity, AppendGate gate)
    {
        lock (_sync)
        {
            gate.Users--;
            if (gate.Users == 0
                && _gates.TryGetValue(identity, out var current)
                && ReferenceEquals(current, gate))
            {
                _gates.Remove(identity);
            }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly TaskLogAppendGateRegistry _registry;
        private readonly TaskLogIdentity _identity;
        private AppendGate? _gate;

        internal Lease(TaskLogAppendGateRegistry registry, TaskLogIdentity identity, AppendGate gate)
        {
            _registry = registry;
            _identity = identity;
            _gate = gate;
        }

        public SemaphoreSlim Semaphore => (_gate ?? throw new ObjectDisposedException(nameof(Lease))).Semaphore;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null)
                _registry.Release(_identity, gate);
        }
    }

    internal sealed class AppendGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users;
    }
}

namespace Mohist.Cli;

/// <summary>
/// Captures every mutable service fact before a candidate is activated. Raw unit
/// bytes are retained because a legacy local-source unit cannot be reconstructed
/// safely from managed-runtime options.
/// </summary>
internal sealed record RuntimeServiceSnapshot(
    string UnitPath,
    bool UnitExisted,
    string? UnitContents,
    ServiceInstallOptions PreviousOptions);

/// <summary>
/// A built and validated artifact whose service target has not been changed.
/// It owns the component lease until activation transfers it to the resulting
/// transaction, so a build failure leaves the active runtime untouched.
/// </summary>
internal sealed class PreparedRuntimeCandidate : IDisposable
{
    private IRuntimeUpdateLease? _lease;

    public PreparedRuntimeCandidate(
        UpdateSource source,
        InstalledRuntimeArtifact artifact,
        RuntimeServiceSnapshot serviceSnapshot,
        ServiceInstallOptions candidateOptions,
        IRuntimeUpdateLease lease)
    {
        Source = source;
        Artifact = artifact;
        ServiceSnapshot = serviceSnapshot;
        CandidateOptions = candidateOptions;
        _lease = lease;
    }

    public UpdateSource Source { get; }
    public InstalledRuntimeArtifact Artifact { get; }
    public RuntimeServiceSnapshot ServiceSnapshot { get; }
    public ServiceInstallOptions CandidateOptions { get; }

    public IRuntimeUpdateLease TakeLease() =>
        Interlocked.Exchange(ref _lease, null)
        ?? throw new InvalidOperationException("runtime candidate lease was already released");

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

internal sealed class PreparedRuntimeUpdate : IDisposable
{
    private IRuntimeUpdateLease? _lease;

    public PreparedRuntimeUpdate(
        UpdateSource source,
        RuntimeActivation activation,
        RuntimeServiceSnapshot serviceSnapshot,
        ServiceInstallOptions candidateOptions,
        IRuntimeUpdateLease lease)
    {
        Source = source;
        Activation = activation;
        ServiceSnapshot = serviceSnapshot;
        CandidateOptions = candidateOptions;
        _lease = lease;
    }

    public UpdateSource Source { get; }
    public RuntimeActivation Activation { get; }
    public RuntimeServiceSnapshot ServiceSnapshot { get; }
    public ServiceInstallOptions CandidateOptions { get; }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

/// <summary>
/// Holds independently leased component candidates after their identities and
/// service targets have read back. A batch commits only when all final checks
/// pass; otherwise callers restore these transactions in reverse order.
/// </summary>
internal sealed class StagedRuntimeUpdateBatch
{
    private readonly List<PreparedRuntimeUpdate> _updates = [];

    public bool HasStagedUpdates => _updates.Count > 0;

    public void Stage(PreparedRuntimeUpdate update)
    {
        if (_updates.Any(existing => ReferenceEquals(existing, update)))
            return;

        _updates.Add(update);
    }

    public IReadOnlyList<PreparedRuntimeUpdate> ReverseStaged() =>
        _updates.AsEnumerable().Reverse().ToArray();

    public void Remove(PreparedRuntimeUpdate update) =>
        _updates.RemoveAll(existing => ReferenceEquals(existing, update));

    public void Commit()
    {
        foreach (var update in _updates)
            update.Dispose();
        _updates.Clear();
    }
}

internal sealed record RuntimeRollbackResult(bool Restored, string Description);

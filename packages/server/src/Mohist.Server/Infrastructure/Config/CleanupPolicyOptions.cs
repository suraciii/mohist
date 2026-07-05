namespace Mohist.Server.Infrastructure.Config;

/// <summary>
/// Server-side workspace cleanup policy that is exposed to runners via the
/// dedicated runner config endpoint. Cleanup execution is exclusively a runner-side
/// responsibility — the server never scans a runner filesystem, never
/// schedules runner-side deletion, and never maintains a per-workspace
/// cleanup queue. Each field is optional; a <c>null</c> value is an
/// explicit unlimited/disabled sentinel that disables the corresponding
/// runner eviction strategy. This class is bound from the
/// <c>Mohist:WorkspaceCleanup</c> configuration section (overridable via
/// <c>MOHIST__WorkspaceCleanup__*</c>).
/// </summary>
public sealed class CleanupPolicyOptions
{
    public const string SectionName = "Mohist:WorkspaceCleanup";

    /// <summary>
    /// Retention window in days. Eligible workspaces older than this window
    /// are evicted by the runner. <c>null</c> disables age-based eviction.
    /// </summary>
    public int? RetentionDays { get; set; }

    /// <summary>
    /// Storage budget in bytes. When the runner's eligible workspace usage
    /// exceeds this budget, the runner evicts earliest-terminated eligible
    /// workspaces until usage drops to the target watermark. <c>null</c>
    /// disables budget-based eviction.
    /// </summary>
    public long? StorageBudgetBytes { get; set; }

    /// <summary>
    /// Target watermark in bytes. The runner stops evicting once usage is
    /// at or below this value. Only consulted when
    /// <see cref="StorageBudgetBytes"/> is configured; defaults to <c>null</c>.
    /// </summary>
    public long? StorageTargetWatermarkBytes { get; set; }

    /// <summary>
    /// True when at least one field is configured (i.e. the policy would
    /// cause the runner to evict anything once a workspace becomes eligible).
    /// </summary>
    public bool HasAnyEnabled =>
        RetentionDays.HasValue ||
        StorageBudgetBytes.HasValue ||
        StorageTargetWatermarkBytes.HasValue;
}

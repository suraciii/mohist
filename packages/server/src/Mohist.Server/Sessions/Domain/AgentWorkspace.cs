using Mohist.Server.Infrastructure;

namespace Mohist.Server.Sessions.Domain;

/// <summary>
/// Workspace source confirmation state for a Project-backed
/// AgentSession. The parent workDir is recorded at launch; the Runner
/// verifies ownership + origin on first execution and reports back, and
/// the Server advances the state.
/// </summary>
public enum WorkspaceRepositoryState
{
    Unconfirmed,
    Confirmed,
    Rejected,
}

/// <summary>
/// Why a Runner first-execution source check rejected the parent
/// workDir. Mirrors the Runner's <c>WorkspaceSourceRejectionReason</c>.
/// </summary>
public enum WorkspaceSourceRejectionReason
{
    OriginMismatch,
    NotRunnerOwned,
}

/// <summary>
/// Durable authoritative workspace source on an AgentSession. The
/// <see cref="Name"/>/<see cref="GitUrl"/>/<see cref="BaseBranch"/>
/// snapshot is immutable; only <see cref="State"/> advances
/// (<c>Unconfirmed -> Confirmed | Rejected</c>). A Session without a
/// Project-backed source has a <c>null</c> WorkspaceRepository and can
/// never become a worktree parent.
/// </summary>
[GenerateSerializer]
public sealed record WorkspaceRepository(
    [property: Id(0)] string Name,
    [property: Id(1)] string GitUrl,
    [property: Id(2)] string BaseBranch,
    [property: Id(3)] WorkspaceRepositoryState State,
    [property: Id(4)] WorkspaceSourceRejectionReason? RejectionReason = null)
{
    public WorkspaceRepositorySnapshot Snapshot => new(Name, GitUrl, BaseBranch);

    public bool IsConfirmed => State == WorkspaceRepositoryState.Confirmed;

    public WorkspaceRepository AsConfirmed() => this with
    {
        State = WorkspaceRepositoryState.Confirmed,
        RejectionReason = null,
    };
}

/// <summary>
/// Constrained spawn workspace intent. <see cref="Inherit"/> is increment-1 behaviour
/// (child reuses the parent authoritative workDir, no materialization);
/// <see cref="Worktree"/> triggers the managed-worktree contract.
/// </summary>
public enum WorkspaceMode
{
    Inherit,
    Worktree,
}

/// <summary>
/// Durable materialization progress persisted on the spawn launch plan.
/// Recovery/abort read it before the child Session exists.
/// </summary>
public enum MaterializeState
{
    None,
    Requested,
    Materialized,
    Rejected,
}

/// <summary>
/// Release progress for an abort that materialized a worktree.
/// </summary>
public enum WorkspaceReleaseState
{
    None,
    Pending,
    Released,
}

/// <summary>
/// Server-side view of the Runner's <c>MaterializeAgentWorkspace</c>
/// rejection reasons. Unknown outcomes do not advance the state
/// machine (the plan stays <see cref="MaterializeState.Requested"/>).
/// </summary>
public enum MaterializeRejectionReason
{
    Capacity,
    Permission,
    ParentWorkspaceUnavailable,
    RepositoryMismatch,
    Invalid,
}

/// <summary>
/// Normalizes a spawn <c>workspace</c> body value into the constrained
/// <see cref="WorkspaceMode"/>. <c>null</c>/empty/"inherit" map to
/// <see cref="WorkspaceMode.Inherit"/> (the default); "worktree" maps to
/// <see cref="WorkspaceMode.Worktree"/>. Any other value (including
/// case variants and paths) is invalid and rejected terminally as
/// <c>invalid-workspace-mode</c>.
/// </summary>
public static class WorkspaceModeNormalizer
{
    public static bool TryNormalize(string? workspace, out WorkspaceMode mode)
    {
        switch (workspace?.Trim())
        {
            case null or "" or "inherit":
                mode = WorkspaceMode.Inherit;
                return true;
            case "worktree":
                mode = WorkspaceMode.Worktree;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>
    /// Stable token folded into the spawn request fingerprint. Valid
    /// modes normalize to <c>inherit</c>/<c>worktree</c>; an invalid
    /// value keeps its trimmed raw text so the same invalid replay
    /// hashes to the same fingerprint (and therefore the same terminal
    /// rejection) while a different invalid value conflicts as 409.
    /// </summary>
    public static string FingerprintToken(string? workspace)
    {
        var trimmed = workspace?.Trim();
        return trimmed switch
        {
            null or "" or "inherit" => "inherit",
            "worktree" => "worktree",
            _ => trimmed,
        };
    }
}

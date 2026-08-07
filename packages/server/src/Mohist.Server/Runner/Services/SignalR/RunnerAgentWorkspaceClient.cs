using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Runner.Services.SignalR;

/// <summary>
/// Canonical request the Server sends to the pinned Runner to
/// materialize a managed worktree for a child AgentSession. The Runner
/// is the sole materialization authority; the Server only persists the
/// returned path/identity and never constructs or reads the directory.
/// </summary>
public sealed record MaterializeAgentWorkspaceRequest(
    string ProjectId,
    string ChildSessionId,
    string ParentWorkDir,
    WorkspaceRepositorySnapshot Repository);

public enum AgentWorkspaceMaterializeOutcome
{
    Materialized,
    Rejected,
    Unknown,
}

public sealed record MaterializeAgentWorkspaceResult(
    AgentWorkspaceMaterializeOutcome Outcome,
    string? WorkspaceIdentity = null,
    string? WorkDir = null,
    MaterializeRejectionReason? Reason = null)
{
    public static MaterializeAgentWorkspaceResult Unknown { get; } =
        new(AgentWorkspaceMaterializeOutcome.Unknown);
}

public sealed record ReleaseAgentWorkspaceRequest(
    string ChildSessionId,
    string WorkspaceIdentity);

public enum AgentWorkspaceReleaseOutcome
{
    Released,
    NotFound,
    Unknown,
}

public sealed record ReleaseAgentWorkspaceResult(AgentWorkspaceReleaseOutcome Outcome)
{
    public static ReleaseAgentWorkspaceResult Unknown { get; } =
        new(AgentWorkspaceReleaseOutcome.Unknown);
}

/// <summary>
/// Server-side seam for the Runner's managed-worktree materialization
/// and release primitives. The production implementation invokes the
/// pinned Runner over the existing SignalR command channel; tests
/// substitute a fake to exercise the durable state machine without a
/// real network or filesystem.
/// </summary>
public interface IAgentWorkspaceMaterializer
{
    Task<MaterializeAgentWorkspaceResult> MaterializeAsync(
        string runnerId,
        MaterializeAgentWorkspaceRequest request,
        CancellationToken ct = default);

    Task<ReleaseAgentWorkspaceResult> ReleaseAsync(
        string runnerId,
        ReleaseAgentWorkspaceRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// SignalR client for <c>MaterializeAgentWorkspace</c> /
/// <c>ReleaseAgentWorkspace</c> on the pinned Runner connection. Mirrors
/// the workspace-client pattern: resolve the connection id by RunnerId,
/// invoke with a bounded timeout, and map any unavailable / exceptional
/// outcome to <see cref="AgentWorkspaceMaterializeOutcome.Unknown"/> /
/// <see cref="AgentWorkspaceReleaseOutcome.Unknown"/> so the coordinator
/// state machine stays recoverable instead of guessing.
/// </summary>
public sealed class RunnerAgentWorkspaceClient : IAgentWorkspaceMaterializer
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IHubContext<RunnerHub> _hub;
    private readonly RunnerConnectionTracker _connections;
    private readonly ILogger<RunnerAgentWorkspaceClient> _log;

    public RunnerAgentWorkspaceClient(
        IHubContext<RunnerHub> hub,
        RunnerConnectionTracker connections,
        ILogger<RunnerAgentWorkspaceClient> log)
    {
        _hub = hub;
        _connections = connections;
        _log = log;
    }

    public async Task<MaterializeAgentWorkspaceResult> MaterializeAsync(
        string runnerId,
        MaterializeAgentWorkspaceRequest request,
        CancellationToken ct = default)
    {
        var connectionId = _connections.GetConnectionId(runnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
            return MaterializeAgentWorkspaceResult.Unknown;

        var payload = new
        {
            projectId = request.ProjectId,
            childSessionId = request.ChildSessionId,
            parentWorkDir = request.ParentWorkDir,
            repository = new
            {
                name = request.Repository.Name,
                gitUrl = request.Repository.GitUrl,
                baseBranch = request.Repository.BaseBranch,
            },
        };

        AgentWorkspaceMaterializeReply? reply;
        try
        {
            reply = await InvokeAsync<AgentWorkspaceMaterializeReply>(
                connectionId, "MaterializeAgentWorkspace", payload, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "MaterializeAgentWorkspace on runner {RunnerId} for {ChildSessionId} failed",
                runnerId, request.ChildSessionId);
            return MaterializeAgentWorkspaceResult.Unknown;
        }

        return reply switch
        {
            { Ok: true, WorkspaceIdentity: var identity, WorkDir: var workDir }
                when identity is not null && workDir is not null
                => new MaterializeAgentWorkspaceResult(
                    AgentWorkspaceMaterializeOutcome.Materialized, identity, workDir),
            { Ok: false, Kind: "rejected", Reason: var reason }
                => new MaterializeAgentWorkspaceResult(
                    AgentWorkspaceMaterializeOutcome.Rejected,
                    Reason: MapRejectionReason(reason)),
            _ => MaterializeAgentWorkspaceResult.Unknown,
        };
    }

    public async Task<ReleaseAgentWorkspaceResult> ReleaseAsync(
        string runnerId,
        ReleaseAgentWorkspaceRequest request,
        CancellationToken ct = default)
    {
        var connectionId = _connections.GetConnectionId(runnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
            return ReleaseAgentWorkspaceResult.Unknown;

        var payload = new
        {
            childSessionId = request.ChildSessionId,
            workspaceIdentity = request.WorkspaceIdentity,
        };

        AgentWorkspaceReleaseReply? reply;
        try
        {
            reply = await InvokeAsync<AgentWorkspaceReleaseReply>(
                connectionId, "ReleaseAgentWorkspace", payload, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "ReleaseAgentWorkspace on runner {RunnerId} for {ChildSessionId} failed",
                runnerId, request.ChildSessionId);
            return ReleaseAgentWorkspaceResult.Unknown;
        }

        return reply switch
        {
            { Ok: true } => new ReleaseAgentWorkspaceResult(AgentWorkspaceReleaseOutcome.Released),
            { Ok: false, Kind: "not-found" } => new ReleaseAgentWorkspaceResult(AgentWorkspaceReleaseOutcome.NotFound),
            _ => ReleaseAgentWorkspaceResult.Unknown,
        };
    }

    private async Task<T?> InvokeAsync<T>(string connectionId, string method, object payload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        return await _hub.Clients.Client(connectionId).InvokeAsync<T?>(method, payload, timeout.Token);
    }

    private static MaterializeRejectionReason MapRejectionReason(string? reason) =>
        reason switch
        {
            "capacity" => MaterializeRejectionReason.Capacity,
            "permission" => MaterializeRejectionReason.Permission,
            "parent-workspace-unavailable" => MaterializeRejectionReason.ParentWorkspaceUnavailable,
            "repository-mismatch" => MaterializeRejectionReason.RepositoryMismatch,
            _ => MaterializeRejectionReason.Invalid,
        };

    // Loose DTOs mirroring the Runner reply contract. Polymorphic by
    // (ok, kind); unknown shapes collapse to Unknown in the mapping above.
    private sealed record AgentWorkspaceMaterializeReply(
        bool Ok,
        string? Kind,
        string? WorkspaceIdentity,
        string? WorkDir,
        string? Reason,
        string? Message);

    private sealed record AgentWorkspaceReleaseReply(
        bool Ok,
        string? Kind,
        string? Message);
}

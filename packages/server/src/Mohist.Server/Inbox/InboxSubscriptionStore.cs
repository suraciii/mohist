using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Inbox;

/// <summary>
/// Persistence boundary for project-scoped inbox subscription preferences.
/// Reads synthesize all-four-enabled when no row exists (no eager
/// creation). Mutations upsert — first write creates, subsequent writes
/// mutate the existing row in place.
/// </summary>
public sealed class InboxSubscriptionStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public InboxSubscriptionStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the current subscription state for <paramref name="projectId"/>.
    /// When no row exists, returns a default with all kinds enabled.
    /// </summary>
    public async Task<InboxSubscriptionState> GetAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId required", nameof(projectId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.InboxSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);

        return row is null
            ? new InboxSubscriptionState()
            : ToState(row);
    }

    /// <summary>
    /// Persists <paramref name="state"/> for <paramref name="projectId"/>.
    /// Creates a new row if none exists; otherwise mutates the existing
    /// row in place. Returns the persisted state.
    /// </summary>
    public async Task<InboxSubscriptionState> SetAsync(
        string projectId,
        InboxSubscriptionState state,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId required", nameof(projectId));
        ArgumentNullException.ThrowIfNull(state);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.InboxSubscriptions
            .FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);

        var now = _timeProvider.GetUtcNow();

        if (row is null)
        {
            row = new InboxSubscriptionRow
            {
                ProjectId = projectId,
                WorkflowFailedEnabled = state.WorkflowFailedEnabled,
                ApprovalRequestedEnabled = state.ApprovalRequestedEnabled,
                IssueStartedEnabled = state.IssueStartedEnabled,
                IssueCompletedEnabled = state.IssueCompletedEnabled,
                AgentResponseFailedEnabled = state.AgentResponseFailedEnabled,
                UpdatedAt = now,
            };
            db.InboxSubscriptions.Add(row);
        }
        else
        {
            row.WorkflowFailedEnabled = state.WorkflowFailedEnabled;
            row.ApprovalRequestedEnabled = state.ApprovalRequestedEnabled;
            row.IssueStartedEnabled = state.IssueStartedEnabled;
            row.IssueCompletedEnabled = state.IssueCompletedEnabled;
            row.AgentResponseFailedEnabled = state.AgentResponseFailedEnabled;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return ToState(row);
    }

    private static InboxSubscriptionState ToState(InboxSubscriptionRow row) => new(
        WorkflowFailedEnabled: row.WorkflowFailedEnabled,
        ApprovalRequestedEnabled: row.ApprovalRequestedEnabled,
        IssueStartedEnabled: row.IssueStartedEnabled,
        IssueCompletedEnabled: row.IssueCompletedEnabled,
        AgentResponseFailedEnabled: row.AgentResponseFailedEnabled);
}

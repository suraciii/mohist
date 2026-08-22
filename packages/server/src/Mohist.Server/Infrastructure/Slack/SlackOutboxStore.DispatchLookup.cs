using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Slack;

public sealed partial class SlackOutboxStore
{
    /// <summary>
    /// Finds one durable delivery by its immutable dispatch identity. Ingress
    /// redelivery must consult this before re-evaluating admission: the row is
    /// the ownership decision even when readiness changed after the original
    /// request committed it.
    /// </summary>
    public async Task<SlackOutboxEntry?> FindByDispatchRefAsync(
        string projectId,
        string connectionId,
        string kind,
        string dispatchRef,
        CancellationToken ct = default,
        string ownerKind = SlackDeliveryOwnerKinds.Connection)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(dispatchRef))
            throw new ArgumentException("DispatchRef is required.", nameof(dispatchRef));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackOutboxRows.AsNoTracking().FirstOrDefaultAsync(candidate =>
            candidate.ProjectId == projectId
            && candidate.OwnerKind == ownerKind
            && candidate.ConnectionId == connectionId
            && candidate.Kind == kind
            && candidate.DispatchRef == dispatchRef, ct);
        return row is null ? null : ToEntry(row);
    }

}

using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Domain;

namespace Mohist.Server.Infrastructure.Data.Runner;

public sealed class TerminalLogOwnershipRow
{
    public string OwnerKind { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string RunnerId { get; set; } = string.Empty;
}

public static class TerminalLogOwnershipPersistence
{
    public static async Task StageAsync(
        MohistDbContext db,
        TerminalLogOwnership ownership,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownership.OwnerKind)
            || string.IsNullOrWhiteSpace(ownership.OwnerId)
            || string.IsNullOrWhiteSpace(ownership.WorkId)
            || string.IsNullOrWhiteSpace(ownership.RunnerId))
        {
            throw new InvalidOperationException("Terminal log ownership requires complete owner, work, and runner identity.");
        }

        var existing = await db.TerminalLogOwnerships
            .SingleOrDefaultAsync(row => row.OwnerKind == ownership.OwnerKind
                && row.OwnerId == ownership.OwnerId
                && row.WorkId == ownership.WorkId, ct);
        if (existing is null)
        {
            db.TerminalLogOwnerships.Add(new TerminalLogOwnershipRow
            {
                OwnerKind = ownership.OwnerKind,
                OwnerId = ownership.OwnerId,
                WorkId = ownership.WorkId,
                RunnerId = ownership.RunnerId,
            });
            return;
        }

        if (!string.Equals(existing.RunnerId, ownership.RunnerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Terminal log ownership for {ownership.OwnerKind}/{ownership.OwnerId}/{ownership.WorkId} is already assigned to another runner.");
        }
    }
}

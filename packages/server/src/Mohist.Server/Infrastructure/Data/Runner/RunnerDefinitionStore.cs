using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Runner;

public class RunnerDefinitionStore
{
    public const int DefaultSlots = 1;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public RunnerDefinitionStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<int> GetOrInitAsync(string runnerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
            throw new ArgumentException("runnerId must be non-empty", nameof(runnerId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, ct);
        if (existing is not null)
            return existing.Slots;

        var now = DateTimeOffset.UtcNow;
        var row = new RunnerRow
        {
            Id = runnerId,
            Slots = DefaultSlots,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Runners.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
            return DefaultSlots;
        }
        catch (DbUpdateException)
        {
            // Another caller initialized the row concurrently; fall through
            // to a follow-up read so the caller sees the persisted value.
            db.Entry(row).State = EntityState.Detached;
        }

        var persisted = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId, ct);
        return persisted?.Slots ?? DefaultSlots;
    }

    public async Task UpdateSlotsAsync(string runnerId, int slots, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runnerId))
            throw new ArgumentException("runnerId must be non-empty", nameof(runnerId));
        if (slots <= 0)
            throw new ArgumentOutOfRangeException(nameof(slots), slots, "slots must be a positive integer");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Runners.FirstOrDefaultAsync(r => r.Id == runnerId, ct);
        if (row is null)
            throw new InvalidOperationException($"Runner '{runnerId}' has no persisted definition state");

        row.Slots = slots;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

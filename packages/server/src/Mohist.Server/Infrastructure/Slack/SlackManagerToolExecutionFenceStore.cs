using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackManagerToolExecutionFenceStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackManagerToolExecutionFenceStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<bool> TryAcquireAsync(
        string jobKey,
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SlackManagerToolExecutionFences.Add(new SlackManagerToolExecutionFenceRow
        {
            JobKey = jobKey,
            SessionId = sessionId,
            State = SlackManagerToolExecutionFenceStates.Started,
            StartedAt = _timeProvider.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            if (await db.SlackManagerToolExecutionFences
                .AsNoTracking()
                .AnyAsync(row => row.JobKey == jobKey, ct))
            {
                return false;
            }

            throw new InvalidOperationException(
                $"Could not acquire the manager tool execution fence for '{jobKey}'.",
                ex);
        }
    }

    public async Task MarkCompletedAsync(string jobKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var completedAt = _timeProvider.GetUtcNow();
        await db.SlackManagerToolExecutionFences
            .Where(row => row.JobKey == jobKey
                && row.State == SlackManagerToolExecutionFenceStates.Started)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, SlackManagerToolExecutionFenceStates.Completed)
                .SetProperty(row => row.CompletedAt, completedAt), ct);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Workspace.Services;

public sealed record WorkspaceDirectoryObservation(
    string AttemptId,
    string RunnerId,
    string HomePath,
    string Outcome,
    DateTimeOffset ObservedAt,
    string? Reason = null,
    long? EstimatedBytes = null,
    DateTimeOffset? MeasuredAt = null,
    DateTimeOffset? ConfirmedRemovalAt = null);

public sealed class WorkspaceDirectoryObservationStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public WorkspaceDirectoryObservationStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<WorkspaceDirectoryObservation?> GetAsync(string projectId, string name, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(
            w => w.ProjectId == projectId && w.Name == name, ct);
        return Read(row?.DirectoryObservationJson);
    }

    public async Task InvalidateAsync(string projectId, string name, string runnerId, string homePath,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Workspaces
            .Where(w => w.ProjectId == projectId && w.Name == name && w.HomeRunnerId == runnerId && w.HomePath == homePath)
            .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.DirectoryObservationJson, (string?)null), ct);
    }

    public async Task<WorkspaceDirectoryObservation?> BeginAsync(
        string projectId, string name, string runnerId, string homePath, string attemptId, CancellationToken ct = default)
    {
        var current = await GetAsync(projectId, name, ct);
        if (current is { Outcome: "unknown", Reason: "result_pending" }) return null;
        return await SaveAsync(projectId, name,
            new(attemptId, runnerId, homePath, "unknown", _time.GetUtcNow(), "result_pending",
                ConfirmedRemovalAt: current?.ConfirmedRemovalAt ?? (current?.Outcome == "removed" ? current.ObservedAt : null)),
            requireAttempt: null, ct);
    }

    public async Task<WorkspaceDirectoryObservation?> CompleteAsync(
        string projectId, string name, string attemptId, string outcome, string? reason,
        CancellationToken ct = default)
    {
        var current = await GetAsync(projectId, name, ct);
        if (current is null || current.AttemptId != attemptId) return null;
        var now = _time.GetUtcNow();
        return await SaveAsync(projectId, name,
            current with { Outcome = outcome, Reason = reason, ObservedAt = now,
                ConfirmedRemovalAt = outcome == "removed" ? now : current.ConfirmedRemovalAt }, attemptId, ct);
    }

    public async Task<WorkspaceDirectoryObservation?> ReportAsync(
        string projectId, string name, WorkspaceDirectoryObservation observation, CancellationToken ct = default)
    {
        var current = await GetAsync(projectId, name, ct);
        if (current is { Outcome: "unknown", Reason: "result_pending" }) return null;
        var now = _time.GetUtcNow();
        return await SaveAsync(projectId, name, observation with { ObservedAt = now,
            ConfirmedRemovalAt = observation.Outcome == "removed"
                ? now : current?.ConfirmedRemovalAt ?? (current?.Outcome == "removed" ? current.ObservedAt : null) }, null, ct);
    }

    private async Task<WorkspaceDirectoryObservation?> SaveAsync(
        string projectId, string name, WorkspaceDirectoryObservation observation, string? requireAttempt, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Name == name, ct);
        if (row is null || row.HomeRunnerId != observation.RunnerId || row.HomePath != observation.HomePath)
            return null;
        var current = Read(row.DirectoryObservationJson);
        if (requireAttempt is not null && current?.AttemptId != requireAttempt)
            return null;
        if (requireAttempt is null && current is { Outcome: "unknown", Reason: "result_pending" })
            return null;
        var original = row.DirectoryObservationJson;
        var updated = await db.Workspaces
            .Where(w => w.ProjectId == projectId && w.Name == name
                && w.HomeRunnerId == observation.RunnerId && w.HomePath == observation.HomePath
                && w.DirectoryObservationJson == original)
            .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.DirectoryObservationJson,
                JsonSerializer.Serialize(observation)), ct);
        return updated == 1 ? observation : null;
    }

    public static WorkspaceDirectoryObservation? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<WorkspaceDirectoryObservation>(json); }
        catch (JsonException) { return null; }
    }
}

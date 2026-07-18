using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Orleans;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;
using DomainIssueEvent = Mohist.Server.Issue.Domain.Events.IssueEvent;

namespace Mohist.Server.Infrastructure.Data.Issue;

public interface IIssueStore : IStateStore<DomainIssue>
{
    Task SaveAsync(string key, DomainIssue state, IReadOnlyList<DomainIssueEvent> events, CancellationToken ct = default);
}

public class IssueStore : IIssueStore
{
    private const string SpecVersion = "1.0";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventStore _eventStore;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<IssueStore> _log;

    public IssueStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventStore eventStore,
        IGrainFactory grainFactory,
        ILogger<IssueStore> log)
    {
        _dbFactory = dbFactory;
        _eventStore = eventStore;
        _grainFactory = grainFactory;
        _log = log;
    }

    public async Task<DomainIssue?> LoadAsync(string key)
    {
        var (projectId, issueNumber) = ParseKey(key);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Issues.FindAsync(projectId, issueNumber);
        return row is null ? null : Deserialize(row.State);
    }

    public async Task SaveAsync(string key, DomainIssue state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await StageIssueAsync(db, state, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(string key, DomainIssue state, IReadOnlyList<DomainIssueEvent> events, CancellationToken ct = default)
    {
        var source = IssueEventPersistence.IssueSource(state.ProjectId, state.Number);
        var subject = state.Number.ToString();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageIssueAsync(db, state, ct);
            var extensions = IssueLineage.BuildExtensions(state);
            foreach (var evt in events)
            {
                if (evt is null) continue;
                var envelope = ToCloudEvent(evt, source, subject, extensions);
                await _eventStore.AppendAsync(db, envelope, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }

        PokeDispatcherBestEffort();
    }

    private void PokeDispatcherBestEffort() =>
        EventDispatcherPoke.PokeAfterCommit(_grainFactory, _log, nameof(IssueStore));

    public Task DeleteAsync(string key) => throw new NotImplementedException();

    public Task<IReadOnlyList<DomainIssue>> ListAsync() => throw new NotImplementedException();

    private static async Task StageIssueAsync(
        MohistDbContext db,
        DomainIssue state,
        CancellationToken ct)
    {
        var row = await db.Issues.FindAsync(new object[] { state.ProjectId, state.Number }, ct);
        if (row is null)
        {
            db.Issues.Add(new IssueRow
            {
                ProjectId = state.ProjectId,
                Number = state.Number,
                State = Serialize(state),
                Risk = state.Risk,
                EpicNumber = state.EpicNumber,
                ParentIssueNumber = state.ParentIssueNumber,
            });
        }
        else
        {
            row.State = Serialize(state);
            row.Risk = state.Risk;
            row.EpicNumber = state.EpicNumber;
            row.ParentIssueNumber = state.ParentIssueNumber;
        }
    }

    private static CloudEvent ToCloudEvent(DomainIssueEvent evt, string source, string subject, IReadOnlyDictionary<string, string> extensions)
    {
        var type = IssueEventSerializer.BusType(evt);
        var data = IssueEventSerializer.ToData(evt);
        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: data,
            subject: subject,
            specVersion: SpecVersion,
            extensions: extensions);
    }

    public static DomainIssue? Deserialize(string json) =>
        string.IsNullOrEmpty(json) ? null : JSON.Deserialize<DomainIssue>(json);

    public static string Serialize(DomainIssue issue) =>
        JSON.Serialize(issue);

    private static (string ProjectId, int IssueNumber) ParseKey(string key)
    {
        ScopedGrainKeyCodec.Parse(key, out var projectId, out var issueNumber);
        return (projectId, issueNumber);
    }
}

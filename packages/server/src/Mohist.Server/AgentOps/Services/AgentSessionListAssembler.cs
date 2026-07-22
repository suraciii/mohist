using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.AgentOps.Services;

public sealed class AgentSessionListAssembler : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentSessionQuery _sessionQuery;
    private readonly ILogger<AgentSessionListAssembler> _logger;

    public AgentSessionListAssembler(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentSessionQuery sessionQuery,
        ILogger<AgentSessionListAssembler> logger)
    {
        _dbFactory = dbFactory;
        _sessionQuery = sessionQuery;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgentSessionInfoDto>> ListCurrentAsync(
        string projectId,
        string? status = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedDescending,
            limit,
            status: status,
            ct: ct);
        sessions = await ActiveSessionReconciler.ReconcileAsync(db, sessions, _logger, ct);
        var issueTitles = await IssueTitleLookup.LoadTitlesAsync(db, projectId, sessions.Select(r => r.IssueNumber()), ct);
        var eventSummaries = await TranscriptReductions.LoadEventSummariesAsync(db, sessions.Select(r => r.Session.Id), ct);

        return sessions.Select(record =>
        {
            var session = record.Session;
            var events = eventSummaries.GetValueOrDefault(session.Id);
            var issueNumber = record.IssueNumber();
            return new AgentSessionInfoDto(
                issueNumber,
                IssueTitleLookup.Resolve(issueTitles, issueNumber),
                record.Label(AgentSessionQueryMetadataKeys.Stage) ?? string.Empty,
                session.Id,
                AgentSessionJsonHelper.ActivityName(session),
                session.Settings.Model,
                null,
                session.Status.CreatedAt.ToString("o"),
                null,
                AgentSessionJsonHelper.LastActivityAt(session).ToString("o"),
                AgentSessionDtoMapper.ToEventSummaryDto(events),
                AgentSessionDtoMapper.ToUsageDto(AgentSessionJsonHelper.Usage(session)));
        }).ToList();
    }
}

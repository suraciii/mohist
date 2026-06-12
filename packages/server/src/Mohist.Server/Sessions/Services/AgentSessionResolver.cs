using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

public sealed class AgentSessionResolver
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;

    public AgentSessionResolver(IDbContextFactory<MohistDbContext> dbFactory, IGrainFactory grains)
    {
        _dbFactory = dbFactory;
        _grains = grains;
    }

    public async Task<string?> ResolveAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AgentSessions.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId && s.SessionName == sessionName)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AgentSessionInfo?> GetAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        var sessionId = await ResolveAsync(workflowRunId, sessionName, ct);
        if (sessionId is null) return null;
        return await _grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
    }

    public IAgentSessionGrain GetGrain(string sessionId) =>
        _grains.GetGrain<IAgentSessionGrain>(sessionId);

    public string NewSessionId() => Guid.NewGuid().ToString("N");
}

using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public sealed class FollowupDispatchScheduler : IFollowupDispatchScheduler, ISingletonService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackgroundTaskLauncher _backgroundTasks;
    private readonly ILogger<FollowupDispatchScheduler> _log;

    public FollowupDispatchScheduler(
        IServiceScopeFactory scopeFactory,
        IBackgroundTaskLauncher backgroundTasks,
        ILogger<FollowupDispatchScheduler> log)
    {
        _scopeFactory = scopeFactory;
        _backgroundTasks = backgroundTasks;
        _log = log;
    }

    public void Schedule(string projectId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        _backgroundTasks.Launch(async ct =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<AgentSessionFollowupDispatcher>();
                await dispatcher.DispatchNextAsync(projectId, sessionId, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not dispatch queued follow-up for AgentSession {SessionId}", sessionId);
            }
        });
    }
}

using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Runner.Services.SignalR;

public class RunnerHub : Hub
{
    private readonly RunnerConnectionTracker _tracker;
    private readonly IEventBus _eventBus;
    private readonly ILogger<RunnerHub> _log;

    public RunnerHub(RunnerConnectionTracker tracker, IEventBus eventBus, ILogger<RunnerHub> log)
    {
        _tracker = tracker;
        _eventBus = eventBus;
        _log = log;
    }

    public override Task OnConnectedAsync()
    {
        var runnerId = Context.GetHttpContext()?.Request.Query["runnerId"].ToString();
        if (!string.IsNullOrEmpty(runnerId))
        {
            _tracker.Register(runnerId, Context.ConnectionId);
        }
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var runnerId = Context.GetHttpContext()?.Request.Query["runnerId"].ToString();
        if (!string.IsNullOrEmpty(runnerId))
        {
            _tracker.Unregister(runnerId);

            // Notify subscribers that this runner's SignalR connection dropped.
            // Subscribers (AgentSessionRunnerBridge) mark any sessions tied
            // to this runner as failed promptly, so the workflow's task
            // dispatch can re-queue or surface the failure.
            _eventBus.Emit(CloudEventFactory.Create(
                type: EventCatalog.ReverseDns.RunnerDisconnected,
                source: new Uri($"/mohist/runner/{runnerId}", UriKind.Relative),
                subject: runnerId,
                extraExtensions: new Dictionary<string, object?>
                {
                    ["runnerid"] = runnerId,
                    ["reason"] = exception is null ? "tcp-drop" : $"disconnect:{exception.GetType().Name}",
                }));
        }
        return base.OnDisconnectedAsync(exception);
    }
}

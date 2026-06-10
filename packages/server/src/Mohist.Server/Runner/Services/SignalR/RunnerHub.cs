using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Runner.Services.SignalR;

public class RunnerHub : Hub
{
    private readonly RunnerConnectionTracker _tracker;
    private readonly ILogger<RunnerHub> _log;

    public RunnerHub(RunnerConnectionTracker tracker, ILogger<RunnerHub> log)
    {
        _tracker = tracker;
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
        }
        return base.OnDisconnectedAsync(exception);
    }
}

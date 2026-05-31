using Microsoft.AspNetCore.SignalR;

namespace Mohist.Server.Runner.SignalR;

public class RunnerHub : Hub
{
    private readonly RunnerConnectionTracker _tracker;

    public RunnerHub(RunnerConnectionTracker tracker)
    {
        _tracker = tracker;
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

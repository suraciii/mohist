using Microsoft.AspNetCore.SignalR;

namespace Mohist.Server.Events.Hub;

public interface IEventsClient
{
    Task OnEvent(string eventName, object? data);
}

public sealed class MohistHub : Hub<IEventsClient>
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "project:global");
        var projectId = Context.GetHttpContext()?.Request.Query["projectId"].ToString();
        if (!string.IsNullOrEmpty(projectId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"project:{projectId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "project:global");
        var projectId = Context.GetHttpContext()?.Request.Query["projectId"].ToString();
        if (!string.IsNullOrEmpty(projectId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project:{projectId}");
        await base.OnDisconnectedAsync(exception);
    }
}

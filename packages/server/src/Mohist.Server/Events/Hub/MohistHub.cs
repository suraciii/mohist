using Microsoft.AspNetCore.SignalR;

namespace Mohist.Server.Events.Hub;

public interface IEventsClient
{
    /// <summary>
    /// Receive an event from the bus. <paramref name="eventName"/> is the
    /// CloudEvents <c>type</c> for back-compat; <paramref name="data"/>
    /// carries a <see cref="CloudEventEnvelope"/> with the full CloudEvents
    /// 1.0.2 attributes (id, source, type, subject, time, extensions, data).
    /// New Web code should read from <c>envelope</c> in <paramref name="data"/>.
    /// </summary>
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

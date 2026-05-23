using System.Text.Json;
using System.Threading.Channels;
using Mohist.Server.Events;

namespace Mohist.Server.Api;

public static class EventRoutes
{
    private static readonly string[] AllEventTypes =
    [
        "stage_changed",
        "comment_added",
        "agent_started",
        "agent_completed",
        "agent_paused",
        "agent_error",
        "approval_requested",
        "tool_call",
        "question_asked",
        "question_answered",
        "explore_crystallized",
        "agent_text_chunk",
        "main_tool_call",
        "coder_text_chunk",
        "coder_thought_chunk",
        "coder_tool_call",
        "ralph_task_update",
        "ralph_loop_progress",
        "plan_round_start",
        "plan_session_update",
        "merge_queued",
        "merge_started",
        "merge_completed",
        "merge_failed",
        "merge_blocked",
        "agent_conflict_resolution_started",
        "agent_conflict_resolution_completed",
        "agent_conflict_resolution_failed",
        "coder_recovery_status",
        "coder_session_started",
        "coder_session_completed",
        "rebase_started",
        "rebase_progress",
        "rebase_completed",
        "rebase_conflict",
        "schedule_triggered",
        "schedule_completed",
        "schedule_failed",
        "stage_task_update",
        "integration_started",
        "integration_step_updated",
        "integration_completed",
        "integration_failed",
        "integration_preflight_refreshed",
    ];

    public static WebApplication MapEventRoutes(this WebApplication app)
    {
        app.MapGet("/api/events", async (
            HttpContext ctx,
            IEventBus eventBus,
            CancellationToken ct,
            string? projectId = null) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            await ctx.Response.Body.FlushAsync(ct);

            var channel = Channel.CreateUnbounded<MohistEvent>();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var handlers = new List<Action<object>>();
            foreach (var eventType in AllEventTypes)
            {
                Action<object> handler = data =>
                {
                    if (projectId is not null)
                    {
                        try
                        {
                            var json = JsonSerializer.Serialize(data);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("projectId", out var pid)
                                && pid.GetString() != projectId)
                                return;
                        }
                        catch { }
                    }
                    channel.Writer.TryWrite(new MohistEvent(eventType, data));
                };
                eventBus.On(eventType, handler);
                handlers.Add(handler);
            }

            var heartbeatCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), heartbeatCts.Token);
                        channel.Writer.TryWrite(new MohistEvent("heartbeat", ""));
                    }
                    catch { break; }
                }
            }, heartbeatCts.Token);

            try
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(cts.Token))
                {
                    if (evt.EventName == "heartbeat")
                    {
                        await ctx.Response.WriteAsync(": heartbeat\n\n", cts.Token);
                    }
                    else
                    {
                        var json = JsonSerializer.Serialize(evt.Data);
                        await ctx.Response.WriteAsync($"event: {evt.EventName}\n", cts.Token);
                        await ctx.Response.WriteAsync($"data: {json}\n\n", cts.Token);
                    }
                    await ctx.Response.Body.FlushAsync(cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                heartbeatCts.Cancel();
                for (int i = 0; i < AllEventTypes.Length; i++)
                    eventBus.Off(AllEventTypes[i], handlers[i]);
                channel.Writer.Complete();
            }

            return Results.Empty;
        });

        return app;
    }
}

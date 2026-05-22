using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static class RunnerRoutes
{
    public static WebApplication MapRunnerRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/{runnerId}");

        group.MapPost("/register", async (string runnerId, RunnerRegisterRequest req, IGrainFactory grains, IRunnerRegistry registry) =>
        {
            registry.Register(runnerId, req.Capabilities);

            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.RegisterAsync(new RunnerInfo(runnerId, req.Capabilities, req.Hostname ?? Environment.MachineName));
            return Results.Ok();
        });

        group.MapPost("/unregister", async (string runnerId, IGrainFactory grains, IRunnerRegistry registry) =>
        {
            registry.Unregister(runnerId);

            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.UnregisterAsync();
            return Results.Ok();
        });

        group.MapPost("/heartbeat", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.HeartbeatAsync();
            return Results.Ok();
        });

        group.MapPost("/poll", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var work = await runner.PollAsync();
            return work is not null ? Results.Ok(work) : Results.NoContent();
        });

        group.MapPost("/report", async (string runnerId, RunnerReportRequest req, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.ReportAsync(req.WorkId, new WorkDispatchResult(
                req.Status, req.Message, req.Output, req.ExitCode));
            return Results.Ok();
        });

        return app;
    }
}

public record RunnerRegisterRequest(string[] Capabilities, string? Hostname = null);
public record RunnerReportRequest(string WorkId, string Status, string? Message = null, JsonElement? Output = null, int? ExitCode = null);

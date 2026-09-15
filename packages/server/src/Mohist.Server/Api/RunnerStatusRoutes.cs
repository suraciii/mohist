using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

public static class RunnerStatusRoutes
{
    public static WebApplication MapRunnerStatusRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runners");

        group.MapGet("/", async (
            RunnerStatusService projection,
            CancellationToken ct) =>
        {
            var snapshot = await projection.GetGlobalRunnersAsync(ct);
            var inventory = snapshot.Runners.Count == 0
                ? new RunnerInventoryStatusView(
                    "first-install",
                    [new RunnerNextActionView(
                        "install-runner",
                        "Install and start the first Runner.",
                        "mo install runner --repo-root <path>")])
                : new RunnerInventoryStatusView("ready", []);
            return ApiResults.Ok(new RunnerStatusListResponse(
                snapshot.ObservedAt,
                inventory,
                snapshot.Runners));
        });

        group.MapGet("/{runnerId}", async (
            string runnerId,
            RunnerStatusService projection,
            CancellationToken ct) =>
        {
            var snapshot = await projection.GetGlobalRunnerAsync(runnerId, ct);
            if (snapshot is null)
                return ApiResults.Fail($"Runner '{runnerId}' not found", 404, "runner_not_found");

            return ApiResults.Ok(new RunnerStatusDetailResponse(
                snapshot.ObservedAt,
                snapshot.Runner));
        });

        return app;
    }
}

public sealed record RunnerInventoryStatusView(
    string State,
    IReadOnlyList<RunnerNextActionView> NextActions);

public sealed record RunnerStatusListResponse(
    DateTimeOffset ObservedAt,
    RunnerInventoryStatusView Inventory,
    IReadOnlyList<RunnerStatusEntry> Runners);

public sealed record RunnerStatusDetailResponse(
    DateTimeOffset ObservedAt,
    RunnerStatusEntry Runner);

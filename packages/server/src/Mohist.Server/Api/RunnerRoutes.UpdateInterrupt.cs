using Microsoft.AspNetCore.Builder;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapUpdateInterruptRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/update-interrupt/{updateInterruptId}/cancel", async (
            string runnerId,
            string updateInterruptId,
            IGrainFactory grains) =>
        {
            if (!Guid.TryParse(updateInterruptId, out _))
                return ApiResults.BadRequest("updateInterruptId must be a UUID");

            var result = await grains.GetGrain<IRunnerGrain>(runnerId)
                .CancelUpdateInterruptAsync(updateInterruptId);
            var status = result.Status switch
            {
                RunnerUpdateInterruptCancelStatus.Cancelled => "cancelled",
                RunnerUpdateInterruptCancelStatus.AlreadyCancelled => "already-cancelled",
                _ => "superseded",
            };
            return ApiResults.Ok(new RunnerUpdateInterruptCancelResponse(
                runnerId,
                result.UpdateInterruptId,
                status));
        });
    }
}


public record RunnerUpdateInterruptRequest(string? UpdateInterruptId = null);

public record RunnerUpdateInterruptCancelResponse(
    string RunnerId,
    string UpdateInterruptId,
    string Status);

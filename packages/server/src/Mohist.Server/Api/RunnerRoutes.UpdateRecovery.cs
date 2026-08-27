using Microsoft.AspNetCore.Builder;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapRunnerUpdateRecoveryRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/update-interrupt", async (
            string runnerId,
            RunnerUpdateInterruptRequest? request,
            IGrainFactory grains) =>
        {
            RunnerRuntimeState? runtime;
            try
            {
                runtime = await grains.GetGrain<IRunnerGrain>(runnerId)
                    .BeginUpdateInterruptAsync(request?.UpdateInterruptId);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }

            if (runtime is null)
                return ApiResults.NotFound($"Runner '{runnerId}' not found");

            var workIds = runtime.ActiveWorks
                .Select(work => work.WorkId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return ApiResults.Ok(new RunnerUpdateInterruptResponse(
                runnerId,
                "interrupted",
                runtime.UpdateInterruptId,
                workIds,
                workIds.Length));
        });
    }
}

public record RunnerUpdateInterruptResponse(
    string RunnerId,
    string Status,
    string? UpdateInterruptId,
    IReadOnlyList<string> InterruptedWorkIds,
    int InterruptedWorkCount);

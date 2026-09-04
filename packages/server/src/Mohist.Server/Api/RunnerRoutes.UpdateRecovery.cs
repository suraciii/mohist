using Microsoft.AspNetCore.Builder;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
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
            RunnerUpdateInterruptBeginResult? result;
            try
            {
                result = await grains.GetGrain<IRunnerGrain>(runnerId)
                    .BeginUpdateInterruptAsync(request?.UpdateInterruptId ?? string.Empty);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }

            if (result is null)
                return ApiResults.NotFound($"Runner '{runnerId}' not found");

            var workIds = result.Runtime.ActiveWorks
                .Select(work => work.WorkId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return ApiResults.Ok(new RunnerUpdateInterruptResponse(
                runnerId,
                BeginStatusValue(result.Status),
                result.UpdateInterruptId,
                workIds,
                workIds.Length));
        }).RequireScopes(Scope.Operator, Scope.Runner);
    }

    private static string BeginStatusValue(RunnerUpdateInterruptBeginStatus status) => status switch
    {
        RunnerUpdateInterruptBeginStatus.Draining => "draining",
        RunnerUpdateInterruptBeginStatus.Superseded => "superseded",
        RunnerUpdateInterruptBeginStatus.AlreadyCancelled => "already-cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

public record RunnerUpdateInterruptResponse(
    string RunnerId,
    string Status,
    string UpdateInterruptId,
    IReadOnlyList<string> ActiveWorkIds,
    int ActiveWorkCount);

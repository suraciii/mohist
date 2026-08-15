using Microsoft.AspNetCore.Builder;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapUpdateInterruptRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/update-interrupt", async (
            string runnerId,
            HttpRequest request,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            RunnerUpdateInterruptRequest? body = null;
            if (request.ContentLength is > 0)
            {
                try
                {
                    body = await request.ReadFromJsonAsync<RunnerUpdateInterruptRequest>(cancellationToken: ct);
                }
                catch
                {
                    return ApiResults.BadRequest("updateInterruptId must be a UUID");
                }
            }
            if (body?.UpdateInterruptId is { Length: > 0 }
                && !Guid.TryParse(body.UpdateInterruptId, out _))
            {
                return ApiResults.BadRequest("updateInterruptId must be a UUID");
            }

            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var runtime = await runner.BeginUpdateInterruptAsync(body?.UpdateInterruptId);
            if (runtime is null)
                return ApiResults.NotFound($"Runner '{runnerId}' not found");
            if (!runtime.Draining || string.IsNullOrWhiteSpace(runtime.UpdateInterruptId))
            {
                return ApiResults.Conflict(
                    "update interrupt was already cancelled",
                    "update_interrupt_cancelled",
                    new { runnerId, updateInterruptId = body?.UpdateInterruptId });
            }

            var interruptedWorkIds = runtime.ActiveWorks
                .Select(work => work.WorkId)
                .Where(workId => !string.IsNullOrWhiteSpace(workId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return ApiResults.Ok(new RunnerUpdateInterruptResponse(
                runnerId,
                "interrupted",
                runtime.UpdateInterruptId!,
                interruptedWorkIds,
                interruptedWorkIds.Length));
        });

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

public record RunnerUpdateInterruptResponse(
    string RunnerId,
    string Status,
    string UpdateInterruptId,
    IReadOnlyList<string> InterruptedWorkIds,
    int InterruptedWorkCount);

public record RunnerUpdateInterruptRequest(string? UpdateInterruptId = null);

public record RunnerUpdateInterruptCancelResponse(
    string RunnerId,
    string UpdateInterruptId,
    string Status);

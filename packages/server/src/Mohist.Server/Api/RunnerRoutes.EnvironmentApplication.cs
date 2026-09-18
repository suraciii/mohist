using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapEnvironmentApplicationRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/environment/application", async (
            string runnerId,
            RunnerEnvironmentApplicationBeginRequest request,
            IGrainFactory grains) =>
        {
            if (request is null)
                return ApiResults.BadRequest("request body is required");

            try
            {
                var result = await grains.GetGrain<IRunnerGrain>(runnerId)
                    .BeginEnvironmentApplicationAsync(
                        request.UpdateId,
                        request.TargetVersion,
                        request.ProcessGeneration,
                        request.ConnectionGeneration);
                if (result is null)
                    return ApiResults.NotFound($"Runner '{runnerId}' is not online");

                var response = new RunnerEnvironmentApplicationResponse(
                    runnerId,
                    result.UpdateId,
                    BeginStatusValue(result.Status),
                    result.Application);
                return result.Status == RunnerEnvironmentApplicationBeginStatus.Conflict
                    ? ApiResults.Conflict("Another Runner environment application or update is active", "environment_application_conflict", response)
                    : ApiResults.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapGet("/environment/application/{updateId}", async (
            string runnerId,
            string updateId,
            IGrainFactory grains) =>
        {
            RunnerEnvironmentApplicationCommandResult result;
            try
            {
                result = await grains.GetGrain<IRunnerGrain>(runnerId)
                    .GetEnvironmentApplicationAsync(updateId);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }

            return result.Status == RunnerEnvironmentApplicationCommandStatus.NotFound
                ? ApiResults.NotFound($"Environment application '{updateId}' was not found")
                : ApiResults.Ok(ProjectEnvironmentApplication(runnerId, result));
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapPost("/environment/application/{updateId}/apply", async (
            string runnerId,
            string updateId,
            RunnerEnvironmentApplicationApplyRequest request,
            IGrainFactory grains) =>
        {
            if (request is null)
                return ApiResults.BadRequest("request body is required");
            return await RunEnvironmentCommandAsync(
                runnerId,
                updateId,
                grains,
                grain => grain.BeginEnvironmentApplyAsync(updateId, request.ProcessGeneration));
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapPost("/environment/application/{updateId}/confirm", async (
            string runnerId,
            string updateId,
            RunnerEnvironmentApplicationConfirmRequest request,
            IGrainFactory grains) =>
        {
            if (request is null)
                return ApiResults.BadRequest("request body is required");
            return await RunEnvironmentCommandAsync(
                runnerId,
                updateId,
                grains,
                grain => grain.ConfirmEnvironmentApplicationAsync(
                    updateId,
                    request.ProcessGeneration,
                    request.EnvironmentVersion));
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapPost("/environment/application/{updateId}/rollback-confirm", async (
            string runnerId,
            string updateId,
            RunnerEnvironmentApplicationConfirmRequest request,
            IGrainFactory grains) =>
        {
            if (request is null)
                return ApiResults.BadRequest("request body is required");
            return await RunEnvironmentCommandAsync(
                runnerId,
                updateId,
                grains,
                grain => grain.ConfirmEnvironmentRollbackAsync(
                    updateId,
                    request.ProcessGeneration,
                    request.EnvironmentVersion));
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapPost("/environment/application/{updateId}/fail", async (
            string runnerId,
            string updateId,
            RunnerEnvironmentApplicationFailureRequest request,
            IGrainFactory grains) =>
        {
            if (request is null)
                return ApiResults.BadRequest("request body is required");
            return await RunEnvironmentCommandAsync(
                runnerId,
                updateId,
                grains,
                grain => grain.FailEnvironmentApplicationAsync(updateId, request.FailureCode));
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapPost("/environment/application/{updateId}/unconfirmed", async (
            string runnerId,
            string updateId,
            RunnerEnvironmentApplicationFailureRequest request,
            IGrainFactory grains) =>
        {
            if (request is null)
                return ApiResults.BadRequest("request body is required");
            return await RunEnvironmentCommandAsync(
                runnerId,
                updateId,
                grains,
                grain => grain.MarkEnvironmentApplicationUnconfirmedAsync(updateId, request.FailureCode));
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapPost("/environment/application/{updateId}/cancel", async (
            string runnerId,
            string updateId,
            IGrainFactory grains) =>
            await RunEnvironmentCommandAsync(
                runnerId,
                updateId,
                grains,
                grain => grain.CancelEnvironmentApplicationAsync(updateId)))
            .RequireScopes(Scope.Operator, Scope.Runner);
    }

    private static async Task<IResult> RunEnvironmentCommandAsync(
        string runnerId,
        string updateId,
        IGrainFactory grains,
        Func<IRunnerGrain, Task<RunnerEnvironmentApplicationCommandResult>> command)
    {
        try
        {
            var result = await command(grains.GetGrain<IRunnerGrain>(runnerId));
            var response = ProjectEnvironmentApplication(runnerId, result);
            return result.Status switch
            {
                RunnerEnvironmentApplicationCommandStatus.NotFound =>
                    ApiResults.NotFound($"Environment application '{updateId}' was not found"),
                RunnerEnvironmentApplicationCommandStatus.Conflict =>
                    ApiResults.Conflict("Environment application state does not allow this operation", "environment_application_conflict", response),
                RunnerEnvironmentApplicationCommandStatus.NotSettled =>
                    ApiResults.Conflict("Runner work has not settled for the current process generation", "environment_application_not_settled", response),
                RunnerEnvironmentApplicationCommandStatus.Stale =>
                    ApiResults.Conflict("Runner process generation is stale", "environment_application_stale", response),
                RunnerEnvironmentApplicationCommandStatus.VersionMismatch =>
                    ApiResults.Conflict("Runner environment version does not match the application", "environment_version_mismatch", response),
                _ => ApiResults.Ok(response),
            };
        }
        catch (ArgumentException ex)
        {
            return ApiResults.BadRequest(ex.Message);
        }
    }

    private static RunnerEnvironmentApplicationResponse ProjectEnvironmentApplication(
        string runnerId,
        RunnerEnvironmentApplicationCommandResult result) =>
        new(runnerId, result.UpdateId, CommandStatusValue(result.Status), result.Application);

    private static string BeginStatusValue(RunnerEnvironmentApplicationBeginStatus status) => status switch
    {
        RunnerEnvironmentApplicationBeginStatus.Waiting => "waiting",
        RunnerEnvironmentApplicationBeginStatus.AlreadyPending => "already-pending",
        RunnerEnvironmentApplicationBeginStatus.AlreadyCompleted => "already-completed",
        RunnerEnvironmentApplicationBeginStatus.Conflict => "conflict",
        RunnerEnvironmentApplicationBeginStatus.NotReady => "not-ready",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string CommandStatusValue(RunnerEnvironmentApplicationCommandStatus status) => status switch
    {
        RunnerEnvironmentApplicationCommandStatus.Accepted => "accepted",
        RunnerEnvironmentApplicationCommandStatus.AlreadyApplied => "already-applied",
        RunnerEnvironmentApplicationCommandStatus.AlreadyRolledBack => "already-rolled-back",
        RunnerEnvironmentApplicationCommandStatus.Cancelled => "cancelled",
        RunnerEnvironmentApplicationCommandStatus.AlreadyCancelled => "already-cancelled",
        RunnerEnvironmentApplicationCommandStatus.Failed => "failed",
        RunnerEnvironmentApplicationCommandStatus.Unconfirmed => "unconfirmed",
        RunnerEnvironmentApplicationCommandStatus.NotFound => "not-found",
        RunnerEnvironmentApplicationCommandStatus.Conflict => "conflict",
        RunnerEnvironmentApplicationCommandStatus.NotSettled => "not-settled",
        RunnerEnvironmentApplicationCommandStatus.Stale => "stale",
        RunnerEnvironmentApplicationCommandStatus.VersionMismatch => "version-mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

public sealed record RunnerEnvironmentApplicationBeginRequest(
    string UpdateId,
    string TargetVersion,
    string ProcessGeneration,
    string ConnectionGeneration);

public sealed record RunnerEnvironmentApplicationApplyRequest(string ProcessGeneration);

public sealed record RunnerEnvironmentApplicationConfirmRequest(
    string ProcessGeneration,
    string EnvironmentVersion);

public sealed record RunnerEnvironmentApplicationFailureRequest(string FailureCode);

public sealed record RunnerEnvironmentApplicationResponse(
    string RunnerId,
    string UpdateId,
    string Status,
    RunnerEnvironmentApplicationSnapshot? Application);

using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapEnvironmentObservationRoutes(RouteGroupBuilder group)
    {
        group.MapGet("/environment/observation", async (
            string runnerId,
            RunnerStatusService status) =>
        {
            var snapshot = await status.GetGlobalRunnerAsync(runnerId);
            var observation = snapshot?.Runner.Environment?.Observation;
            return observation is null
                ? ApiResults.NotFound($"Runner environment observation for '{runnerId}' was not found")
                : ApiResults.Ok(new RunnerEnvironmentObservationResponse(runnerId, "accepted", observation));
        }).RequireScopes(Scope.Operator, Scope.Runner);

        group.MapPost("/environment/observation", async (
            string runnerId,
            RunnerEnvironmentObservationReportRequest request,
            IGrainFactory grains) =>
        {
            if (request is null)
                return ApiResults.BadRequest("request body is required");

            try
            {
                var result = await grains.GetGrain<IRunnerGrain>(runnerId)
                    .RecordEnvironmentObservationAsync(ToDomain(request));
                var response = new RunnerEnvironmentObservationResponse(
                    runnerId,
                    ObservationStatusValue(result.Status),
                    RunnerEnvironmentObservationMapper.From(result.Observation));
                return result.Status == RunnerEnvironmentObservationStatus.Stale
                    ? ApiResults.Conflict(
                        "Runner environment observation belongs to a stale process generation",
                        "environment_observation_stale",
                        response)
                    : ApiResults.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        }).RequireScopes(Scope.Operator, Scope.Runner);
    }

    private static RunnerEnvironmentObservation ToDomain(
        RunnerEnvironmentObservationReportRequest request) =>
        new()
        {
            ProcessGeneration = request.ProcessGeneration,
            EnvironmentVersion = request.EnvironmentVersion,
            EnvironmentLoadedAt = request.EnvironmentLoadedAt,
            CandidateSource = request.Candidate?.Source,
            CandidateUser = request.Candidate?.User,
            CandidateVersion = request.Candidate?.Version,
            CandidateVariables = request.Candidate?.Variables ?? [],
            CandidateCapturedAt = request.Candidate?.CapturedAt,
            CandidateAddedVariables = request.Candidate?.AddedVariables ?? [],
            CandidateRemovedVariables = request.Candidate?.RemovedVariables ?? [],
            CandidateChangedVariables = request.Candidate?.ChangedVariables ?? [],
            ToolChecks = (request.ToolChecks ?? [])
                .Select(check => new RunnerEnvironmentToolCheck
                {
                    Executable = check.Executable,
                    ResolvedPath = check.ResolvedPath,
                    SnapshotKind = check.SnapshotKind,
                    SnapshotVersion = check.SnapshotVersion,
                    Outcome = check.Outcome,
                    ExitCode = check.ExitCode,
                    DurationMilliseconds = check.DurationMilliseconds,
                    CheckedAt = check.CheckedAt,
                })
                .ToArray(),
        };

    private static string ObservationStatusValue(RunnerEnvironmentObservationStatus status) => status switch
    {
        RunnerEnvironmentObservationStatus.Accepted => "accepted",
        RunnerEnvironmentObservationStatus.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

public sealed record RunnerEnvironmentObservationReportRequest(
    string? ProcessGeneration = null,
    string? EnvironmentVersion = null,
    DateTimeOffset? EnvironmentLoadedAt = null,
    RunnerEnvironmentCandidateObservationRequest? Candidate = null,
    IReadOnlyList<RunnerEnvironmentToolCheckRequest>? ToolChecks = null);

public sealed record RunnerEnvironmentCandidateObservationRequest(
    string? Source = null,
    string? User = null,
    string? Version = null,
    string[]? Variables = null,
    DateTimeOffset? CapturedAt = null,
    string[]? AddedVariables = null,
    string[]? RemovedVariables = null,
    string[]? ChangedVariables = null);

public sealed record RunnerEnvironmentToolCheckRequest(
    string Executable,
    string? ResolvedPath,
    string SnapshotKind,
    string? SnapshotVersion,
    string Outcome,
    int? ExitCode,
    long DurationMilliseconds,
    DateTimeOffset CheckedAt);

public sealed record RunnerEnvironmentObservationResponse(
    string RunnerId,
    string Status,
    RunnerEnvironmentObservationView? Observation);

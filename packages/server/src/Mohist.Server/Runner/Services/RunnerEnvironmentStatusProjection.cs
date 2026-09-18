using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// The status projection's bounded view of an environment application. Raw
/// snapshot values never enter this record.
/// </summary>
public sealed record RunnerEnvironmentApplicationObservation(
    string UpdateId,
    string TargetVersion,
    string? PreviousVersion,
    string Phase,
    string? FailureCode,
    string? BaseProcessGeneration,
    string? BaseConnectionGeneration,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt);

public static class RunnerEnvironmentApplicationObservationMapper
{
    public static RunnerEnvironmentApplicationObservation? From(
        RunnerEnvironmentApplication? application)
    {
        if (application is null)
            return null;

        return Sanitize(new(
            BoundedRequired(application.UpdateId),
            BoundedRequired(application.TargetVersion),
            Bounded(application.PreviousVersion),
            PhaseValue(application.Phase),
            BoundedFailureCode(application.FailureCode),
            Bounded(application.BaseProcessGeneration),
            Bounded(application.BaseConnectionGeneration),
            application.RequestedAt,
            application.CompletedAt));
    }

    public static RunnerEnvironmentApplicationObservation Sanitize(
        RunnerEnvironmentApplicationObservation observation) =>
        new(
            BoundedRequired(observation.UpdateId),
            BoundedRequired(observation.TargetVersion),
            Bounded(observation.PreviousVersion),
            BoundedPhase(observation.Phase),
            BoundedFailureCode(observation.FailureCode),
            Bounded(observation.BaseProcessGeneration),
            Bounded(observation.BaseConnectionGeneration),
            observation.RequestedAt,
            observation.CompletedAt);

    private static string PhaseValue(RunnerEnvironmentApplicationPhase phase) => phase switch
    {
        RunnerEnvironmentApplicationPhase.Waiting => "waiting",
        RunnerEnvironmentApplicationPhase.Applying => "applying",
        RunnerEnvironmentApplicationPhase.Active => "active",
        RunnerEnvironmentApplicationPhase.Failed => "failed",
        RunnerEnvironmentApplicationPhase.Cancelled => "cancelled",
        RunnerEnvironmentApplicationPhase.Unconfirmed => "unconfirmed",
        _ => "unknown",
    };

    private static string BoundedPhase(string value) => value switch
    {
        "waiting" or "applying" or "active" or "failed" or "cancelled" or "unconfirmed" => value,
        _ => "unknown",
    };

    private static string BoundedRequired(string value) => Bounded(value) ?? "invalid";

    private static string? BoundedFailureCode(string? value)
    {
        var normalized = Bounded(value);
        return normalized is not null
            && normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            ? normalized
            : null;
    }

    private static string? Bounded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 128 && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }
}

/// <summary>
/// Bounded read projection for the explicit local environment observation.
/// The projection contains names and execution facts only; it never contains
/// snapshot values, command arguments, or command output.
/// </summary>
public sealed record RunnerEnvironmentObservationView(
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? ProcessGeneration,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? EnvironmentVersion,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] DateTimeOffset? EnvironmentLoadedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? CandidateSource,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? CandidateUser,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? CandidateVersion,
    IReadOnlyList<string> CandidateVariables,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] DateTimeOffset? CandidateCapturedAt,
    IReadOnlyList<string> CandidateAddedVariables,
    IReadOnlyList<string> CandidateRemovedVariables,
    IReadOnlyList<string> CandidateChangedVariables,
    IReadOnlyList<RunnerEnvironmentToolCheckView> ToolChecks,
    DateTimeOffset ReportedAt);

public sealed record RunnerEnvironmentToolCheckView(
    string Executable,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? ResolvedPath,
    string SnapshotKind,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? SnapshotVersion,
    string Outcome,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] int? ExitCode,
    long DurationMilliseconds,
    DateTimeOffset CheckedAt);

public static class RunnerEnvironmentObservationMapper
{
    private const int MaxTextLength = 256;
    private const int MaxPathLength = 512;
    private const int MaxListLength = 32;
    private const int MaxToolChecks = 8;

    public static RunnerEnvironmentObservationView? From(RunnerEnvironmentObservation? observation)
    {
        if (observation is null)
            return null;

        return new(
            Bounded(observation.ProcessGeneration),
            Bounded(observation.EnvironmentVersion),
            observation.EnvironmentLoadedAt,
            Bounded(observation.CandidateSource),
            Bounded(observation.CandidateUser),
            Bounded(observation.CandidateVersion),
            Names(observation.CandidateVariables),
            observation.CandidateCapturedAt,
            Names(observation.CandidateAddedVariables),
            Names(observation.CandidateRemovedVariables),
            Names(observation.CandidateChangedVariables),
            (observation.ToolChecks ?? [])
                .TakeLast(MaxToolChecks)
                .Select(ToolCheck)
                .ToArray(),
            observation.ReportedAt);
    }

    private static RunnerEnvironmentToolCheckView ToolCheck(RunnerEnvironmentToolCheck value) =>
        new(
            BoundedRequired(value.Executable),
            BoundedPath(value.ResolvedPath),
            value.SnapshotKind is "active" or "candidate" ? value.SnapshotKind : "unknown",
            Bounded(value.SnapshotVersion),
            value.Outcome is "passed" or "failed" or "not-found" or "timed-out" or "error"
                ? value.Outcome
                : "error",
            value.ExitCode is >= 0 and <= 255 ? value.ExitCode : null,
            Math.Clamp(value.DurationMilliseconds, 0, 10_000),
            value.CheckedAt);

    private static IReadOnlyList<string> Names(string[]? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length <= 128 && !value.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaxListLength)
            .ToArray();

    private static string BoundedRequired(string value) => Bounded(value) ?? "unknown";

    private static string? BoundedPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= MaxPathLength
            && !normalized.Any(char.IsControl)
            && Path.IsPathFullyQualified(normalized)
            ? normalized
            : null;
    }

    private static string? Bounded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= MaxTextLength && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }
}

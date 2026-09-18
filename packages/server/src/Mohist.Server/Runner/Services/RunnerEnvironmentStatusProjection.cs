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

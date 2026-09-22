using Orleans;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Read-only observation vocabulary of the <c>session.probe</c> JSON-RPC
/// method. The observation travels as a validated string so the wire values
/// stay byte-identical between the Server and the Runner implementation; the
/// absence of a wire enum keeps an unanswered or malformed reply from ever
/// looking like evidence.
/// </summary>
public static class RunnerSessionActivityObservations
{
    public const string Executing = "executing";
    public const string Idle = "idle";
    public const string UnknownToRunner = "unknown-to-runner";

    public static bool IsKnown(string? observation) =>
        string.Equals(observation, Executing, StringComparison.Ordinal)
        || string.Equals(observation, Idle, StringComparison.Ordinal)
        || string.Equals(observation, UnknownToRunner, StringComparison.Ordinal);

    public static string RequireKnown(string? observation) =>
        IsKnown(observation)
            ? observation!
            : throw new ArgumentException(
                $"Unknown session activity observation '{observation}'.", nameof(observation));
}

/// <summary>
/// Read-only activity-probe request for one logical Session. The record is
/// the complete binding tuple the Runner must examine: an answer is only
/// valid for exactly this target, so any resolved-binding mismatch fails
/// closed. The request is a read — the observation identity is not
/// permission to rerun effects.
/// </summary>
[GenerateSerializer]
public sealed record RunnerSessionActivityProbeRequest(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string ObservationId,
    [property: Id(2)] string RunnerId,
    [property: Id(3)] string Runtime,
    [property: Id(4)] string RuntimeSessionId,
    [property: Id(5)] string WorkDir,
    [property: Id(6)] long BindingEpoch,
    [property: Id(7)] long ContextGeneration)
{
    /// <summary>
    /// True when every field the fence compares is present and well-formed.
    /// An incomplete target can never be answered, so the caller discards it
    /// before any state transition.
    /// </summary>
    public bool HasCompleteTarget() =>
        !string.IsNullOrWhiteSpace(SessionId)
        && !string.IsNullOrWhiteSpace(ObservationId)
        && !string.IsNullOrWhiteSpace(RunnerId)
        && !string.IsNullOrWhiteSpace(Runtime)
        && !string.IsNullOrWhiteSpace(RuntimeSessionId)
        && !string.IsNullOrWhiteSpace(WorkDir)
        && BindingEpoch >= 0
        && ContextGeneration >= 1;
}

/// <summary>
/// The Runner's answer for one probe. <see cref="Probe"/> echoes the complete
/// target actually examined; <see cref="Observation"/> is one validated
/// <see cref="RunnerSessionActivityObservations"/> value.
/// </summary>
[GenerateSerializer]
public sealed record RunnerSessionActivityProbeResult(
    [property: Id(0)] RunnerSessionActivityProbeRequest Probe,
    [property: Id(1)] string Observation)
{
    public bool HasKnownObservation() => RunnerSessionActivityObservations.IsKnown(Observation);
}

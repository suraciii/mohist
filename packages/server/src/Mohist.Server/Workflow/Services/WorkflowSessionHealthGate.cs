using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Evaluates the context-window health of an agent session against the
/// thresholds used by the workflow retry and task dispatch paths.
///
/// <para>
/// Three tiers apply:
/// <list type="bullet">
///   <item><description><b>Healthy</b> &lt; <see cref="WarnThresholdPercent"/>: retry/dispatch proceeds without comment.</description></item>
///   <item><description><b>Warn</b> <see cref="WarnThresholdPercent"/> – &lt; <see cref="BlockThresholdPercent"/>: retry/dispatch proceeds with a warning log entry.</description></item>
///   <item><description><b>Block</b> ≥ <see cref="BlockThresholdPercent"/>: retry is rejected with a structured error; dispatch refuses to run and the stage records a blocking reason.</description></item>
/// </list>
/// </para>
///
/// <para>
/// The 10-point gap between the warn and block bands gives operators a
/// graduated degradation path: they get a warning before they are
/// completely blocked. The thresholds are also defined here so the
/// gateway is a single source of truth shared by the grain, the HTTP
/// layer, and the unit tests.
/// </para>
/// </summary>
public static class WorkflowSessionHealthGate
{
    public const double WarnThresholdPercent = 80d;
    public const double BlockThresholdPercent = 90d;

    public const string RecoveryActionCompact = "compact";
    public const string RecoveryActionReset = "reset";

    public static readonly IReadOnlyList<string> RecoveryActions = new[]
    {
        RecoveryActionCompact,
        RecoveryActionReset,
    };

    public static HealthVerdict Evaluate(double? contextUsagePercent)
    {
        if (contextUsagePercent is null)
        {
            return HealthVerdict.Healthy;
        }

        if (contextUsagePercent.Value >= BlockThresholdPercent)
        {
            return HealthVerdict.Block;
        }

        if (contextUsagePercent.Value >= WarnThresholdPercent)
        {
            return HealthVerdict.Warn;
        }

        return HealthVerdict.Healthy;
    }

    public static HealthVerdict Evaluate(long? contextWindowUsed, long? contextWindowSize) =>
        Evaluate(AgentSessionJsonHelper.ContextUsagePercent(contextWindowUsed, contextWindowSize));

    public static string BuildBlockingMessage(double? contextUsagePercent)
    {
        if (contextUsagePercent is null)
        {
            return "Session context is near capacity. Compact or reset the session before retrying.";
        }

        return $"Session context is near capacity ({contextUsagePercent.Value:0.##}%). Compact or reset the session before retrying.";
    }
}

public enum HealthVerdict
{
    Healthy,
    Warn,
    Block,
}

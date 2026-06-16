namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Helper for converting context window usage percentage into a
/// traffic-light <c>HealthStatus</c> string and for detecting when a
/// session's health status has crossed a colour threshold boundary.
///
/// <para>
/// Thresholds (in percent):
/// <list type="bullet">
///   <item><description><b>green</b> &lt; 60%</description></item>
///   <item><description><b>yellow</b> 60% – 79.99%</description></item>
///   <item><description><b>red</b> ≥ 80%</description></item>
/// </list>
/// </para>
/// </summary>
public static class ContextHealthClassifier
{
    public const string GreenStatus = "green";
    public const string YellowStatus = "yellow";
    public const string RedStatus = "red";

    public const double GreenToYellowPercent = 60d;
    public const double YellowToRedPercent = 80d;

    /// <summary>
    /// Minimum change in percentage points required to emit a health
    /// update event when no colour threshold is crossed. Aligns with
    /// the spec requirement that &lt;10pp changes without a boundary
    /// crossing do not trigger events.
    /// </summary>
    public const double SignificantDeltaPercent = 10d;

    public static string? Classify(double? usagePercent)
    {
        if (usagePercent is null) return null;
        if (usagePercent.Value >= YellowToRedPercent) return RedStatus;
        if (usagePercent.Value >= GreenToYellowPercent) return YellowStatus;
        return GreenStatus;
    }

    /// <summary>
    /// Returns <c>true</c> when the new usage crosses a colour
    /// threshold relative to the previous health status, or when the
    /// absolute change in percentage points meets the
    /// <see cref="SignificantDeltaPercent"/> floor.
    /// </summary>
    public static bool ShouldEmitUpdate(string? previousStatus, double? previousPercent, double? nextPercent)
    {
        if (nextPercent is null) return false;
        var nextStatus = Classify(nextPercent);
        if (nextStatus is null) return false;

        if (!string.Equals(previousStatus, nextStatus, StringComparison.Ordinal))
            return true;

        if (previousPercent is null) return true;
        return Math.Abs(nextPercent.Value - previousPercent.Value) >= SignificantDeltaPercent;
    }
}

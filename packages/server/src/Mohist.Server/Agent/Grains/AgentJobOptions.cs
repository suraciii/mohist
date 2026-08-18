namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Single configuration knob for the agent-job execution engine. All dispatch
/// retry/backoff behavior and the grain-level job timeout are sourced from here
/// so operational bounds can be tuned without code changes.
/// </summary>
/// <remarks>
/// Documented defaults: backoff 1s → 60s cap, total retry bound 10 min,
/// job timeout 10 min, runner-loss recovery 15 min, and update-interruption
/// settlement 5 min. Bind from
/// <c>Mohist:AgentJob</c> in <c>~/.mohist/config.jsonc</c>.
/// </remarks>
public sealed class AgentJobOptions
{
    public const string SectionName = "Mohist:AgentJob";

    public TimeSpan DispatchBackoffInitial { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan DispatchBackoffCap { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan DispatchRetryBound { get; set; } = TimeSpan.FromMinutes(10);
    public double DispatchBackoffMultiplier { get; set; } = 2.0;

    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long an AgentJob may remain Unknown and recoverable after its
    /// runner loses presence. This must exceed the two-minute presence
    /// timeout so closeout records the interruption first.
    /// </summary>
    public TimeSpan RunnerLossRecoveryTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// Bounded arbitration window for an update-interrupted job whose
    /// Runner never delivers a confirmed receipt. Expiry is explicit
    /// Interrupted terminal state, never a synthesized task verdict.
    /// </summary>
    public TimeSpan UpdateInterruptionTimeout { get; set; } = TimeSpan.FromMinutes(5);


    public AgentJobBackoffSchedule ResolveBackoffSchedule()
    {
        return new AgentJobBackoffSchedule(
            Initial: DispatchBackoffInitial < TimeSpan.Zero ? TimeSpan.Zero : DispatchBackoffInitial,
            Cap: DispatchBackoffCap < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : DispatchBackoffCap,
            Multiplier: DispatchBackoffMultiplier <= 1.0 ? 2.0 : DispatchBackoffMultiplier,
            TotalBound: DispatchRetryBound < TimeSpan.Zero ? TimeSpan.Zero : DispatchRetryBound);
    }
}

public sealed record AgentJobBackoffSchedule(
    TimeSpan Initial,
    TimeSpan Cap,
    double Multiplier,
    TimeSpan TotalBound)
{
    public TimeSpan NextDelay(TimeSpan previous)
    {
        if (previous <= TimeSpan.Zero)
            return Initial;

        var scaledTicks = (long)(previous.Ticks * Multiplier);
        var next = new TimeSpan(scaledTicks);
        return next > Cap ? Cap : next;
    }
}
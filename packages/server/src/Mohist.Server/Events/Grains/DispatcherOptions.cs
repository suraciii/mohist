namespace Mohist.Server.Events.Grains;

/// <summary>
/// Configuration knobs for the cluster-singleton dispatcher grain
/// (<see cref="DispatcherGrain"/>) and the underlying
/// <see cref="Mohist.Server.Infrastructure.Events.EventDispatcherService"/>.
/// </summary>
/// <remarks>
/// Bind from <c>Mohist:Dispatcher</c> in <c>~/.mohist/config.jsonc</c>.
/// Default cadence is approximately one second, the project's design
/// target (<c>openspec/changes/issue-362/design.md</c> D2). Reminder
/// periods in Orleans carry a runtime-enforced lower bound configured
/// separately via <see cref="Orleans.Hosting.ReminderOptions.MinimumReminderPeriod"/>;
/// <see cref="ReminderPeriod"/> is the cadence this grain asks for, and
/// the silo-level option is the floor below which the runtime refuses
/// to register. Production registration lowers the floor to ~1s to
/// match this default (see <c>MohistSiloRegistration</c>).
/// </remarks>
public sealed class DispatcherOptions
{
    public const string SectionName = "Mohist:Dispatcher";

    /// <summary>
    /// Initial delay before the first reminder fires after activation.
    /// Defaults to ~1s so the first tick lines up with one reminder
    /// period of activation.
    /// </summary>
    public TimeSpan ReminderDueTime { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cadence between reminder ticks. Defaults to ~1s. Must be &gt;= the
    /// silo-level <see cref="Orleans.Hosting.ReminderOptions.MinimumReminderPeriod"/>.
    /// </summary>
    public TimeSpan ReminderPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Per-tick cap on undelivered rows pulled from the event store.
    /// Defaults to 100; tune up for high-volume systems, down if a
    /// single tick is taking too long.
    /// </summary>
    public int BatchLimit { get; set; } = 100;

    /// <summary>
    /// Maximum number of times a single subscription handler is retried
    /// before its event is dead-lettered. Fixed cap (no backoff/jitter
    /// in v1 — see <c>design.md</c> D6).
    /// </summary>
    public int HandlerMaxAttempts { get; set; } = 3;
}
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Events;
using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.Events.Grains;

/// <summary>
/// Cluster-singleton dispatcher grain. Its reminder drives the dispatch
/// loop through <see cref="EventDispatcherService.DispatchAsync"/>, and the
/// same grain also exposes the operator redelivery entry point used by the
/// dead-letter HTTP route.
/// </summary>
public sealed class EventDispatcherGrain : Grain, IEventDispatcherGrain
{
    public const string Global = "__global__";
    public const string ReminderName = "event-dispatcher-tick";

    private readonly EventDispatcherService _dispatcher;
    private readonly EventDispatcherOptions _options;
    private readonly ILogger<EventDispatcherGrain> _log;

    public EventDispatcherGrain(
        EventDispatcherService dispatcher,
        IOptions<EventDispatcherOptions> options,
        ILogger<EventDispatcherGrain> log)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!IsGlobalKey(this.GetPrimaryKeyString()))
        {
            // Silently no-op rather than throw: the rogue-grain path
            // exercises a non-fixed key in tests, and a transient race
            // during cluster membership churn can momentarily activate
            // the grain on a transitioning silo under a stale key. Either
            // way, no dispatch work must run — the fixed key is the
            // invariant.
            _log.LogWarning(
                "Event dispatcher grain activated under non-fixed key '{Key}'; ignoring (expected '{Expected}')",
                this.GetPrimaryKeyString(),
                Global);
            return;
        }

        await this.RegisterOrUpdateReminder(
            ReminderName,
            _options.ReminderPeriod,
            _options.ReminderPeriod);
        _log.LogInformation(
            "Event dispatcher grain activated (key={Key}); reminder cadence {Period}",
            this.GetPrimaryKeyString(),
            _options.ReminderPeriod);
    }

    public Task DispatchNowAsync(CancellationToken ct = default)
    {
        if (!IsFixedKey) return Task.CompletedTask;
        return _dispatcher.DispatchAsync(ct);
    }

    public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default)
    {
        if (!IsFixedKey)
            return Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "Non-fixed key"));
        return _dispatcher.RedeliverAsync(deadLetterId, ct);
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, ReminderName, StringComparison.Ordinal))
            return Task.CompletedTask;

        return _dispatcher.DispatchAsync(CancellationToken.None);
    }

    internal static bool IsGlobalKey(string key) =>
        string.Equals(key, Global, StringComparison.Ordinal);

    private bool IsFixedKey => IsGlobalKey(this.GetPrimaryKeyString());
}

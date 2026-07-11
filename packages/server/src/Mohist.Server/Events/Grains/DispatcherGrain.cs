using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Events;
using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.Events.Grains;

/// <summary>
/// Cluster-singleton dispatcher grain. Self-wakes on a persisted reminder
/// (<see cref="ReminderName"/>) and delegates every tick — whether
/// reminder-driven or <see cref="PulseAsync"/>-driven — to
/// <see cref="EventDispatcherService.DispatchAsync"/>. The grain body is
/// a thin shell; all pull–fan-out–mark–retry–dead-letter logic lives in
/// the service so it is unit-testable with fakes + an injected
/// <see cref="TimeProvider"/>.
/// </summary>
public sealed class DispatcherGrain : Grain, IDispatcherGrain, IRemindable
{
    public const string FixedKey = "dispatcher";
    public const string ReminderName = "dispatcher-tick";

    private readonly EventDispatcherService _dispatcher;
    private readonly DispatcherOptions _options;
    private readonly ILogger<DispatcherGrain> _log;

    public DispatcherGrain(
        EventDispatcherService dispatcher,
        IOptions<DispatcherOptions> options,
        ILogger<DispatcherGrain> log)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await this.RegisterOrUpdateReminder(
            ReminderName,
            _options.ReminderDueTime,
            _options.ReminderPeriod);
        _log.LogInformation(
            "Dispatcher grain activated (key={Key}); reminder cadence ~{Period}",
            this.GetPrimaryKeyString(), _options.ReminderPeriod);
    }

    public Task EnsureStartedAsync() => Task.CompletedTask;

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
        {
            _log.LogDebug(
                "Dispatcher grain ignoring foreign reminder {Name}", reminderName);
            return Task.CompletedTask;
        }
        return _dispatcher.DispatchAsync(CancellationToken.None);
    }

    public Task PulseAsync(CancellationToken ct = default) =>
        _dispatcher.DispatchAsync(ct);

    public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
        _dispatcher.RedeliverAsync(deadLetterId, ct);
}

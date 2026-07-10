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
    internal const string FixedKey = "dispatcher";
    internal const string ReminderName = "dispatcher-tick";

    private readonly EventDispatcherService _dispatcher;
    private readonly TimeProvider _time;
    private readonly DispatcherOptions _options;
    private readonly ILogger<DispatcherGrain> _log;
    private IGrainReminder? _reminder;

    public DispatcherGrain(
        EventDispatcherService dispatcher,
        TimeProvider time,
        IOptions<DispatcherOptions> options,
        ILogger<DispatcherGrain> log)
    {
        _dispatcher = dispatcher;
        _time = time;
        _options = options.Value;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await EnsureReminderAsync(ct);
        _log.LogInformation(
            "Dispatcher grain activated (key={Key}); reminder cadence ~{Period}",
            this.GetPrimaryKeyString(), _options.ReminderPeriod);
    }

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

    public Task RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
        _dispatcher.RedeliverAsync(deadLetterId, ct);

    private async Task EnsureReminderAsync(CancellationToken ct)
    {
        if (_reminder is not null)
            return;

        var existing = await this.GetReminder(ReminderName);
        if (existing is not null)
        {
            _reminder = existing;
            return;
        }

        _reminder = await this.RegisterOrUpdateReminder(
            ReminderName,
            _options.ReminderDueTime,
            _options.ReminderPeriod);
    }
}
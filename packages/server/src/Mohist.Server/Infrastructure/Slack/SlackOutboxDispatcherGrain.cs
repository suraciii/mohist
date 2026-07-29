using Mohist.Server.Infrastructure.Slack;
using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.Infrastructure.Slack.Grains;

/// <summary>
/// Cluster-singleton dispatcher for the Slack outbound outbox. Mirrors
/// <see cref="Mohist.Server.Events.Grains.IEventDispatcherGrain"/>:
/// activated under a fixed string key
/// (<see cref="SlackOutboxDispatcherGrain.Global"/>) so Orleans'
/// placement guarantees exactly one active instance per cluster, with
/// a persistent reminder driving the dispatch cadence. A rogue
/// activation under any other key is silently ignored — the fixed key
/// is the invariant.
/// </summary>
public interface ISlackOutboxDispatcherGrain : IGrainWithStringKey, IRemindable
{
    /// <summary>
    /// Best-effort immediate dispatch tick. Producers call this after
    /// committing a row to nudge latency down; correctness does NOT
    /// depend on it — the persisted reminder alone guarantees the
    /// retry-budget / claim-timeout / uncertain-timeout sweeps run
    /// within one reminder period.
    /// </summary>
    Task DispatchNowAsync(CancellationToken ct = default);
}

public sealed class SlackOutboxDispatcherGrain : Grain, ISlackOutboxDispatcherGrain
{
    public const string Global = "__slack_outbox_dispatcher_global__";
    public const string ReminderName = "slack-outbox-dispatcher";

    private readonly SlackOutboxDispatcherService _service;
    private readonly Microsoft.Extensions.Options.IOptions<SlackProviderOptions> _options;
    private readonly ILogger<SlackOutboxDispatcherService> _log;

    public SlackOutboxDispatcherGrain(
        SlackOutboxDispatcherService service,
        Microsoft.Extensions.Options.IOptions<SlackProviderOptions> options,
        ILogger<SlackOutboxDispatcherService> log)
    {
        _service = service;
        _options = options;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!string.Equals(this.GetPrimaryKeyString(), Global, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "Slack outbox dispatcher grain activated under non-fixed key '{Key}'; ignoring (expected '{Expected}')",
                this.GetPrimaryKeyString(),
                Global);
            return;
        }

        await this.RegisterOrUpdateReminder(
            ReminderName,
            _options.Value.OutboxReminderPeriod,
            _options.Value.OutboxReminderPeriod);
        _log.LogInformation(
            "Slack outbox dispatcher grain activated (key={Key}); reminder cadence {Period}",
            this.GetPrimaryKeyString(),
            _options.Value.OutboxReminderPeriod);
    }

    public Task DispatchNowAsync(CancellationToken ct = default)
    {
        if (!IsFixedKey) return Task.CompletedTask;
        return _service.DispatchAsync(ct);
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, ReminderName, StringComparison.Ordinal))
            return Task.CompletedTask;

        return _service.DispatchAsync(CancellationToken.None);
    }

    private bool IsFixedKey =>
        string.Equals(this.GetPrimaryKeyString(), Global, StringComparison.Ordinal);
}

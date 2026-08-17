using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.Infrastructure.Slack.Grains;

public interface ISlackActionRecoveryGrain : IGrainWithStringKey, IRemindable
{
    Task RecoverNowAsync(CancellationToken ct = default);
}

public sealed class SlackActionRecoveryGrain : Grain, ISlackActionRecoveryGrain
{
    public const string Global = "__slack_action_recovery_global__";
    public const string ReminderName = "slack-action-recovery";
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromSeconds(10);
    private readonly SlackActionRecoveryService _service;
    private readonly ILogger<SlackActionRecoveryGrain> _log;

    public SlackActionRecoveryGrain(
        SlackActionRecoveryService service,
        ILogger<SlackActionRecoveryGrain> log)
    {
        _service = service;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!string.Equals(this.GetPrimaryKeyString(), Global, StringComparison.Ordinal))
            return;
        await this.RegisterOrUpdateReminder(ReminderName, ReminderPeriod, ReminderPeriod);
        _log.LogInformation("Slack action recovery activated under fixed key {Key}", Global);
    }

    public Task RecoverNowAsync(CancellationToken ct = default) =>
        IsFixedKey ? _service.RecoverAsync(ct) : Task.CompletedTask;

    public Task ReceiveReminder(string reminderName, TickStatus status) =>
        string.Equals(reminderName, ReminderName, StringComparison.Ordinal)
            ? _service.RecoverAsync(CancellationToken.None)
            : Task.CompletedTask;

    private bool IsFixedKey =>
        string.Equals(this.GetPrimaryKeyString(), Global, StringComparison.Ordinal);
}

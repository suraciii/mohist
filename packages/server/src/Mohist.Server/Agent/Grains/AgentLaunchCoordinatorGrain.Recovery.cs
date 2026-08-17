using System.Text.Json;
using Mohist.Server.Agent.Services;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentLaunchCoordinatorGrain
{
    private async Task SaveStateAsync()
    {
        await _state.WriteStateAsync();
    }

    private async Task EnsureRecoveryReminderAsync()
    {
        await this.RegisterOrUpdateReminder(
            RecoveryReminderName,
            RecoveryReminderDue,
            RecoveryReminderPeriod);
    }

    private static void ThrowRejection(AgentLaunchCoordinatorPlan plan)
    {
        if (plan.PostPlanRejected)
            throw new AgentSpawnPostPlanRejectedException(plan.RejectionReason!);
        throw new AgentSpawnPreplanRejectedException(plan.RejectionReason!);
    }

    private async Task UnregisterReminderAsync()
    {
        try
        {
            var reminder = await this.GetReminder(RecoveryReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentLaunchCoordinatorGrain {Key} could not unregister orphan reminder",
                PrimaryKeyString());
        }
    }

    private string PrimaryKeyString() => this.GetPrimaryKeyString();

    private static JsonElement? DeserializeAgentConfig(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonDocument.Parse(json).RootElement.Clone();
}

using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Resolves whether a preserved Slack Bot author belongs to the current
/// workspace's active Mohist enrollment. The receiving App is intentionally
/// absent from this decision.
/// </summary>
public sealed class SlackManagedBotAdmissionService : IScopedService
{
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly ManagedSlackAgentAppStore _agentApps;

    public SlackManagedBotAdmissionService(
        SlackWorkspaceEnrollmentStore enrollments,
        ManagedSlackAgentAppStore agentApps)
    {
        _enrollments = enrollments;
        _agentApps = agentApps;
    }

    public async Task<SlackManagedBotAdmissionResult> EvaluateAsync(
        string workspaceTeamId,
        string? senderKind,
        SlackBotAuthorMetadata? authorBot,
        CancellationToken ct = default)
    {
        if (!string.Equals(senderKind?.Trim(), "bot", StringComparison.OrdinalIgnoreCase)
            || authorBot is null
            || authorBot.IdentityConflict)
            return SlackManagedBotAdmissionResult.NotManaged;

        var authorAppId = Normalize(authorBot.AppId);
        var authorBotUserId = Normalize(authorBot.BotUserId);
        if (authorAppId is null && authorBotUserId is null)
            return SlackManagedBotAdmissionResult.NotManaged;

        var enrollment = await _enrollments.GetActiveByTeamAsync(workspaceTeamId, ct);
        if (enrollment is null)
            return SlackManagedBotAdmissionResult.NotManaged;

        if (Matches(
                enrollment.ManagerAppId,
                enrollment.ManagerBotUserId,
                authorAppId,
                authorBotUserId))
            return new SlackManagedBotAdmissionResult(true, enrollment);

        var agentApps = await _agentApps.ListByEnrollmentAsync(enrollment.Id, ct);
        if (agentApps.Any(agentApp =>
                agentApp.DeletedAt is null
                && !string.Equals(agentApp.AppLifecycle, SlackAppLifecycle.Deleted, StringComparison.Ordinal)
                && string.Equals(agentApp.WorkspaceTeamId, workspaceTeamId, StringComparison.Ordinal)
                && Matches(agentApp.AppId, agentApp.BotUserId, authorAppId, authorBotUserId)))
            return new SlackManagedBotAdmissionResult(true, enrollment);

        return new SlackManagedBotAdmissionResult(false, enrollment);
    }

    private static bool Matches(
        string? registeredAppId,
        string? registeredBotUserId,
        string? authorAppId,
        string? authorBotUserId)
    {
        var appId = Normalize(registeredAppId);
        var botUserId = Normalize(registeredBotUserId);
        if (appId is null || botUserId is null)
            return false;

        return (authorAppId is null || string.Equals(appId, authorAppId, StringComparison.Ordinal))
            && (authorBotUserId is null || string.Equals(botUserId, authorBotUserId, StringComparison.Ordinal));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SlackManagedBotAdmissionResult(
    bool IsManaged,
    SlackWorkspaceEnrollment? ActiveEnrollment = null)
{
    public static SlackManagedBotAdmissionResult NotManaged { get; } = new(false);
}

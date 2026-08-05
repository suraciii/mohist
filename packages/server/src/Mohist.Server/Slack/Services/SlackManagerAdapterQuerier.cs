using System.Text;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManagerAdapterQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly ISecretStore _secrets;

    public SlackManagerAdapterQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        SlackWorkspaceEnrollmentStore enrollments,
        ISecretStore secrets)
    {
        _dbFactory = dbFactory;
        _enrollments = enrollments;
        _secrets = secrets;
    }

    public async Task<IReadOnlyList<SlackManagerAdapterTarget>> ListReadyTargetsAsync(
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.SlackWorkspaceEnrollments.AsNoTracking()
            .Where(enrollment => enrollment.DeletedAt == null
                && enrollment.Lifecycle == SlackEnrollmentLifecycle.Active
                && enrollment.ManagerCapability == SlackManagerCapability.Available
                && enrollment.ManagerReadiness == SlackManagerReadiness.Ready
                && enrollment.ManagerAppId != string.Empty
                && enrollment.ManagerBotUserId != string.Empty
                && enrollment.ManagerCredentialRef != string.Empty)
            .OrderBy(enrollment => enrollment.Id)
            .Select(enrollment => new
            {
                enrollment.Id,
                enrollment.WorkspaceTeamId,
                enrollment.ManagerCredentialRef,
            })
            .ToListAsync(ct);

        var targets = new List<SlackManagerAdapterTarget>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var credential = await LoadManagerCredentialAsync(candidate.Id, ct);
            if (credential is not null)
                targets.Add(new SlackManagerAdapterTarget(candidate.Id, candidate.WorkspaceTeamId));
        }

        return targets;
    }

    public async Task<SlackManagerAdapterSessionResult> GetSessionAsync(
        string enrollmentId,
        CancellationToken ct = default)
    {
        var enrollment = await _enrollments.GetAsync(enrollmentId, ct);
        if (enrollment is null)
            return SlackManagerAdapterSessionResult.NotFound;
        if (enrollment.Lifecycle != SlackEnrollmentLifecycle.Active)
            return SlackManagerAdapterSessionResult.Inactive;
        if (enrollment.ManagerReadiness != SlackManagerReadiness.Ready
            || string.IsNullOrWhiteSpace(enrollment.ManagerCredentialRef))
            return SlackManagerAdapterSessionResult.NotReady;

        var botToken = await LoadManagerCredentialAsync(enrollment.Id, ct);
        if (botToken is null)
            return SlackManagerAdapterSessionResult.NotReady;

        return new SlackManagerAdapterSessionResult(
            SlackManagerAdapterSessionStates.Ready,
            enrollment.WorkspaceTeamId,
            Encoding.UTF8.GetString(botToken));
    }

    private async Task<byte[]?> LoadManagerCredentialAsync(
        string enrollmentId,
        CancellationToken ct)
    {
        var secret = await _secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken),
            ct);
        return secret is { Length: > 0 } ? secret : null;
    }
}

public sealed record SlackManagerAdapterTarget(string EnrollmentId, string WorkspaceTeamId);

public sealed record SlackManagerAdapterSessionResult(
    string State,
    string? WorkspaceTeamId = null,
    string? BotToken = null)
{
    public static readonly SlackManagerAdapterSessionResult NotFound = new(SlackManagerAdapterSessionStates.NotFound);
    public static readonly SlackManagerAdapterSessionResult Inactive = new(SlackManagerAdapterSessionStates.Inactive);
    public static readonly SlackManagerAdapterSessionResult NotReady = new(SlackManagerAdapterSessionStates.NotReady);
}

public static class SlackManagerAdapterSessionStates
{
    public const string Ready = "ready";
    public const string NotFound = "not_found";
    public const string Inactive = "inactive";
    public const string NotReady = "not_ready";
}

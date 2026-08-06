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
    private readonly ISecretStore _secrets;

    public SlackManagerAdapterQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretStore secrets)
    {
        _dbFactory = dbFactory;
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

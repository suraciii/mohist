using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Backs Socket lease targets with <see cref="SlackWorkspaceEnrollment"/>
/// state for the workspace Mohist App (Manager). Connection-backed targets
/// belong to a later slice; this provider covers the Manager target the
/// setup flow produces. Secret <em>addresses</em> are derived from the
/// enrollment owner; plaintext is resolved by the lease core at issuance.
/// </summary>
public sealed class EnrollmentSlackLeaseTargetProvider(
    SlackWorkspaceEnrollmentStore enrollments,
    IDbContextFactory<MohistDbContext> dbFactory) : ISlackLeaseTargetProvider, IScopedService
{
    public async Task<IReadOnlyList<SlackLeaseTarget>> GetTargetsAsync(string operatorId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackWorkspaceEnrollments.AsNoTracking()
            .Where(row => row.Lifecycle == SlackEnrollmentLifecycle.Active
                && row.ManagerAppId != "")
            .ToListAsync(ct);
        return rows.Select(ToTarget).WhereNotNull().ToList();
    }

    public async Task<SlackLeaseTarget?> GetTargetAsync(
        string operatorId, SlackLeaseTargetRef targetRef, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        if (targetRef is not SlackLeaseTargetRef.Manager manager)
            return null;
        var enrollment = await enrollments.GetAsync(manager.EnrollmentId, ct);
        return enrollment is null || enrollment.Lifecycle != SlackEnrollmentLifecycle.Active
            ? null
            : ToTarget(enrollment);
    }

    public async Task MarkVerifiedAsync(
        string operatorId,
        SlackLeaseTargetRef targetRef,
        string appId,
        DateTimeOffset verifiedAt,
        CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (targetRef is not SlackLeaseTargetRef.Manager manager)
            return;
        await enrollments.CompleteSocketVerificationAsync(manager.EnrollmentId, ct);
    }

    private static SlackLeaseTarget? ToTarget(SlackWorkspaceEnrollmentRow row) => ToTarget(
        row.Id, row.WorkspaceTeamId, row.Lifecycle, row.ManagerAppId, row.RuntimeCredentialValidationState);

    private static SlackLeaseTarget? ToTarget(SlackWorkspaceEnrollment enrollment) => ToTarget(
        enrollment.Id, enrollment.WorkspaceTeamId, enrollment.Lifecycle, enrollment.ManagerAppId, enrollment.RuntimeCredentialValidationState);

    private static SlackLeaseTarget? ToTarget(
        string enrollmentId,
        string workspaceTeamId,
        string lifecycle,
        string managerAppId,
        string runtimeCredentialState)
    {
        if (string.IsNullOrWhiteSpace(managerAppId))
            return null;
        var @ref = new SlackLeaseTargetRef.Manager(enrollmentId, workspaceTeamId);
        var tokenProvisioned = runtimeCredentialState
            is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket
            or SlackRuntimeCredentialValidationState.Verified;
        return new SlackLeaseTarget(
            @ref,
            managerAppId,
            Active: lifecycle == SlackEnrollmentLifecycle.Active,
            AppLevelTokenProvisioned: tokenProvisioned,
            BotTokenProvisioned: tokenProvisioned,
            CredentialVerified: runtimeCredentialState == SlackRuntimeCredentialValidationState.Verified,
            AppLevelTokenAddress: SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken),
            BotTokenAddress: SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken));
    }

    private static void RequireOperator(string operatorId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
}

internal static class LeaseTargetEnumerable
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source) where T : class
    {
        foreach (var item in source)
            if (item is not null)
                yield return item;
    }
}

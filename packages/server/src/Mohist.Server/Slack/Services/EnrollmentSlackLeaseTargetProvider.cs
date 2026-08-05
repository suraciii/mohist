using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Backs Socket lease targets with durable control-plane state:
/// the workspace Mohist App (Manager, via <see cref="SlackWorkspaceEnrollment"/>)
/// and each managed Agent App (Connection, via <see cref="ManagedSlackAgentApp"/>
/// joined to its <see cref="Agent.Domain.AgentConnection"/>). Secret
/// <em>addresses</em> are derived from the owning aggregate; plaintext is
/// resolved by the lease core at issuance and never appears in discovery.
/// </summary>
public sealed class EnrollmentSlackLeaseTargetProvider(
    SlackWorkspaceEnrollmentStore enrollments,
    ManagedSlackAgentAppStore agentApps,
    SlackAgentAppBindingService binding,
    IDbContextFactory<MohistDbContext> dbFactory,
    ISecretStore secrets) : ISlackLeaseTargetProvider, IScopedService
{
    public async Task<IReadOnlyList<SlackLeaseTarget>> GetTargetsAsync(string operatorId, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        var targets = new List<SlackLeaseTarget>();
        targets.AddRange(await DiscoverManagerTargetsAsync(ct));
        targets.AddRange(await DiscoverConnectionTargetsAsync(ct));
        return targets;
    }

    public async Task<SlackLeaseTarget?> GetTargetAsync(
        string operatorId, SlackLeaseTargetRef targetRef, CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        return targetRef switch
        {
            SlackLeaseTargetRef.Manager manager => await GetManagerTargetAsync(manager, ct),
            SlackLeaseTargetRef.Connection connection => await GetConnectionTargetAsync(connection, ct),
            _ => null,
        };
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
        switch (targetRef)
        {
            case SlackLeaseTargetRef.Manager manager:
                // The confirmed hello promotes the candidate pair to the runtime
                // addresses; the candidate slot and the previous pair parked by a
                // rotation are no longer needed. Promote before marking Verified
                // so a crash between the two can never serve a stale pair from a
                // Verified state.
                await PromoteEnrollmentCandidateAsync(manager.EnrollmentId, ct);
                await DeleteEnrollmentCandidateSecretsAsync(manager.EnrollmentId, ct);
                await secrets.DeleteAsync(
                    SecretStoreAddress.ForSlackWorkspaceEnrollment(manager.EnrollmentId, SecretKind.PreviousBotToken), ct);
                await secrets.DeleteAsync(
                    SecretStoreAddress.ForSlackWorkspaceEnrollment(manager.EnrollmentId, SecretKind.PreviousAppToken), ct);
                await enrollments.CompleteSocketVerificationAsync(manager.EnrollmentId, ct);
                return;
            case SlackLeaseTargetRef.Connection connection:
                await VerifyConnectionTargetAsync(connection, appId, ct);
                return;
        }
    }

    public async Task RejectAsync(
        string operatorId,
        SlackLeaseTargetRef targetRef,
        DateTimeOffset rejectedAt,
        CancellationToken ct = default)
    {
        RequireOperator(operatorId);
        switch (targetRef)
        {
            case SlackLeaseTargetRef.Manager manager:
                await RejectManagerTargetAsync(manager, ct);
                return;
            case SlackLeaseTargetRef.Connection connection:
                await RejectConnectionTargetAsync(connection, ct);
                return;
        }
    }

    private async Task<IReadOnlyList<SlackLeaseTarget>> DiscoverManagerTargetsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackWorkspaceEnrollments.AsNoTracking()
            .Where(row => row.Lifecycle == SlackEnrollmentLifecycle.Active
                && row.ManagerAppId != "")
            .ToListAsync(ct);
        return rows.Select(ToManagerTarget).WhereNotNull().ToList();
    }

    private async Task<SlackLeaseTarget?> GetManagerTargetAsync(
        SlackLeaseTargetRef.Manager manager, CancellationToken ct)
    {
        var enrollment = await enrollments.GetAsync(manager.EnrollmentId, ct);
        return enrollment is null || enrollment.Lifecycle != SlackEnrollmentLifecycle.Active
            ? null
            : ToManagerTarget(enrollment);
    }

    private static SlackLeaseTarget? ToManagerTarget(SlackWorkspaceEnrollmentRow row) => ToManagerTarget(
        row.Id, row.WorkspaceTeamId, row.Lifecycle, row.ManagerAppId, row.RuntimeCredentialValidationState);

    private static SlackLeaseTarget? ToManagerTarget(SlackWorkspaceEnrollment enrollment) => ToManagerTarget(
        enrollment.Id, enrollment.WorkspaceTeamId, enrollment.Lifecycle, enrollment.ManagerAppId, enrollment.RuntimeCredentialValidationState);

    private static SlackLeaseTarget? ToManagerTarget(
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
        var candidateAppLevelTokenAddress = runtimeCredentialState
            is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket
            ? SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken)
            : (SecretStoreAddress?)null;
        return new SlackLeaseTarget(
            @ref,
            managerAppId,
            Active: lifecycle == SlackEnrollmentLifecycle.Active,
            AppLevelTokenProvisioned: tokenProvisioned,
            BotTokenProvisioned: tokenProvisioned,
            CredentialVerified: runtimeCredentialState == SlackRuntimeCredentialValidationState.Verified,
            AppLevelTokenAddress: SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken),
            BotTokenAddress: SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken),
            CandidateAppLevelTokenAddress: candidateAppLevelTokenAddress);
    }

    private async Task<IReadOnlyList<SlackLeaseTarget>> DiscoverConnectionTargetsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await LeaseableConnectionJoins(db).ToListAsync(ct);
        return rows.Select(ToConnectionTarget).WhereNotNull().ToList();
    }

    private async Task<SlackLeaseTarget?> GetConnectionTargetAsync(
        SlackLeaseTargetRef.Connection connection, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await (
            from agentApp in db.ManagedSlackAgentApps.AsNoTracking()
            where agentApp.DeletedAt == null
                && agentApp.AppLifecycle == SlackAppLifecycle.Created
                && agentApp.AppId != ""
                && agentApp.AgentConnectionId == connection.ConnectionId
            join conn in db.AgentConnections.AsNoTracking()
                on agentApp.AgentConnectionId equals conn.Id
            where conn.DeletedAt == null
                && conn.DesiredState == DesiredStateKind.Enabled
                && conn.ProjectId == connection.ProjectId
                && conn.Id == connection.ConnectionId
            select new ConnectionLeaseJoin(
                agentApp.Id,
                agentApp.AppId,
                agentApp.WorkspaceTeamId,
                agentApp.RuntimeCredentialValidationState,
                conn.ProjectId,
                conn.Id,
                conn.AppId,
                conn.BotUserId)
        ).FirstOrDefaultAsync(ct);
        return row is null ? null : ToConnectionTarget(row);
    }

    private static IQueryable<ConnectionLeaseJoin> LeaseableConnectionJoins(MohistDbContext db) =>
        from agentApp in db.ManagedSlackAgentApps.AsNoTracking()
        where agentApp.DeletedAt == null
            && agentApp.AppLifecycle == SlackAppLifecycle.Created
            && agentApp.AppId != ""
        join connection in db.AgentConnections.AsNoTracking()
            on agentApp.AgentConnectionId equals connection.Id
        where connection.DeletedAt == null
            && connection.DesiredState == DesiredStateKind.Enabled
            && (agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Candidate
                || agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.AwaitingSocket
                || agentApp.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified)
        select new ConnectionLeaseJoin(
            agentApp.Id,
            agentApp.AppId,
            agentApp.WorkspaceTeamId,
            agentApp.RuntimeCredentialValidationState,
            connection.ProjectId,
            connection.Id,
            connection.AppId,
            connection.BotUserId);

    private static SlackLeaseTarget ToConnectionTarget(ConnectionLeaseJoin join)
    {
        var @ref = new SlackLeaseTargetRef.Connection(join.ConnectionProjectId, join.ConnectionId);
        var tokenProvisioned = join.RuntimeCredentialValidationState
            is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket
            or SlackRuntimeCredentialValidationState.Verified;
        var bound = !string.IsNullOrWhiteSpace(join.ConnectionAppId)
            && !string.IsNullOrWhiteSpace(join.ConnectionBotUserId);
        var candidateAppLevelTokenAddress = join.RuntimeCredentialValidationState
            is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket
            ? SecretStoreAddress.ForManagedSlackAgentApp(join.AgentAppId, SecretKind.CandidateAppToken)
            : (SecretStoreAddress?)null;
        return new SlackLeaseTarget(
            @ref,
            join.AppId,
            Active: bound,
            AppLevelTokenProvisioned: tokenProvisioned,
            BotTokenProvisioned: tokenProvisioned,
            CredentialVerified: join.RuntimeCredentialValidationState == SlackRuntimeCredentialValidationState.Verified,
            AppLevelTokenAddress: SecretStoreAddress.ForManagedSlackAgentApp(join.AgentAppId, SecretKind.AppToken),
            BotTokenAddress: SecretStoreAddress.ForManagedSlackAgentApp(join.AgentAppId, SecretKind.BotToken),
            CandidateAppLevelTokenAddress: candidateAppLevelTokenAddress);
    }

    private async Task VerifyConnectionTargetAsync(
        SlackLeaseTargetRef.Connection connection, string appId, CancellationToken ct)
    {
        var agentApp = await agentApps.GetByConnectionAsync(connection.ConnectionId, ct);
        if (agentApp is null
            || agentApp.DeletedAt is not null
            || agentApp.AppLifecycle != SlackAppLifecycle.Created
            || !string.Equals(agentApp.AppId, appId, StringComparison.Ordinal))
            return;

        var state = agentApp.RuntimeCredentialValidationState;
        if (state == SlackRuntimeCredentialValidationState.Candidate)
        {
            await agentApps.ApplyCredentialValidationAsync(
                agentApp.Id, SlackRuntimeCredentialValidationState.AwaitingSocket, ct);
            await agentApps.ApplyCredentialValidationAsync(
                agentApp.Id, SlackRuntimeCredentialValidationState.Verified, ct);
        }
        else if (state == SlackRuntimeCredentialValidationState.AwaitingSocket)
        {
            await agentApps.ApplyCredentialValidationAsync(
                agentApp.Id, SlackRuntimeCredentialValidationState.Verified, ct);
        }
        else if (state != SlackRuntimeCredentialValidationState.Verified)
        {
            return;
        }

        // The confirmed hello promotes the candidate pair to the runtime
        // addresses; the candidate slot and the previous pair parked by a
        // rotation are no longer needed. Promote before the state leaves
        // AwaitingSocket so a crash between the two can never serve a stale
        // pair from a Verified state.
        await PromoteAgentAppCandidateAsync(agentApp.Id, ct);
        await DeleteAgentAppCandidateSecretsAsync(agentApp.Id, ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentApp.Id, SecretKind.PreviousBotToken), ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentApp.Id, SecretKind.PreviousAppToken), ct);
        await binding.ReconcileAsync(agentApp.Id, ct);
    }

    private async Task RejectManagerTargetAsync(SlackLeaseTargetRef.Manager manager, CancellationToken ct)
    {
        var enrollment = await enrollments.GetAsync(manager.EnrollmentId, ct);
        if (enrollment is null)
            return;

        var state = enrollment.RuntimeCredentialValidationState;
        if (state is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket)
            await DeleteEnrollmentCandidateSecretsAsync(manager.EnrollmentId, ct);

        if (await secrets.LoadAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(manager.EnrollmentId, SecretKind.PreviousBotToken), ct) is not null)
        {
            // A rotation was in flight: restore the parked previous verified
            // pair to the runtime addresses so the Mohist App keeps serving it.
            await RestoreEnrollmentRuntimeFromPreviousAsync(manager.EnrollmentId, ct);
            if (state != SlackRuntimeCredentialValidationState.Verified
                && enrollment.Lifecycle == SlackEnrollmentLifecycle.Active)
                await enrollments.CompleteSocketVerificationAsync(manager.EnrollmentId, ct);
            return;
        }

        // First-provision candidate: its validation failed. Verified/
        // Failed/NotProvided targets are left untouched (idempotent).
        if (state is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket)
            await enrollments.ApplySocketValidationAsync(
                manager.EnrollmentId, SlackRuntimeCredentialValidationState.Failed, ct);
    }

    private async Task RejectConnectionTargetAsync(SlackLeaseTargetRef.Connection connection, CancellationToken ct)
    {
        var agentApp = await agentApps.GetByConnectionAsync(connection.ConnectionId, ct);
        if (agentApp is null
            || agentApp.DeletedAt is not null
            || agentApp.AppLifecycle != SlackAppLifecycle.Created)
            return;

        var state = agentApp.RuntimeCredentialValidationState;
        if (state is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket)
            await DeleteAgentAppCandidateSecretsAsync(agentApp.Id, ct);

        if (await secrets.LoadAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentApp.Id, SecretKind.PreviousBotToken), ct) is not null)
        {
            // A rotation was in flight: restore the previous verified pair. The
            // Connection stays bound to the same App from its prior verification.
            await RestoreAgentAppRuntimeFromPreviousAsync(agentApp.Id, ct);
            if (state != SlackRuntimeCredentialValidationState.Verified)
                await agentApps.ApplyCredentialValidationAsync(
                    agentApp.Id, SlackRuntimeCredentialValidationState.Verified, ct);
            return;
        }

        // First-provision candidate: its validation failed. The Connection is
        // never bound here; binding only follows a verified hello.
        if (state is SlackRuntimeCredentialValidationState.Candidate
            or SlackRuntimeCredentialValidationState.AwaitingSocket)
            await agentApps.ApplyCredentialValidationAsync(
                agentApp.Id, SlackRuntimeCredentialValidationState.Failed, ct);
    }

    private async Task PromoteEnrollmentCandidateAsync(string enrollmentId, CancellationToken ct)
    {
        var bot = await secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken), ct);
        var app = await secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken), ct);
        if (bot is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken), bot, ct);
        if (app is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken), app, ct);
    }

    private async Task DeleteEnrollmentCandidateSecretsAsync(string enrollmentId, CancellationToken ct)
    {
        await secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateAppToken), ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.CandidateBotToken), ct);
    }

    private async Task PromoteAgentAppCandidateAsync(string agentAppId, CancellationToken ct)
    {
        var bot = await secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateBotToken), ct);
        var app = await secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateAppToken), ct);
        if (bot is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), bot, ct);
        if (app is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), app, ct);
    }

    private async Task DeleteAgentAppCandidateSecretsAsync(string agentAppId, CancellationToken ct)
    {
        await secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateAppToken), ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.CandidateBotToken), ct);
    }

    private async Task RestoreEnrollmentRuntimeFromPreviousAsync(string enrollmentId, CancellationToken ct)
    {
        var bot = await secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken), ct);
        var app = await secrets.LoadAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousAppToken), ct);
        if (bot is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken), bot, ct);
        if (app is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken), app, ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousBotToken), ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.PreviousAppToken), ct);
    }

    private async Task RestoreAgentAppRuntimeFromPreviousAsync(string agentAppId, CancellationToken ct)
    {
        var bot = await secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken), ct);
        var app = await secrets.LoadAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousAppToken), ct);
        if (bot is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), bot, ct);
        if (app is not null)
            await secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), app, ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousBotToken), ct);
        await secrets.DeleteAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.PreviousAppToken), ct);
    }

    private static void RequireOperator(string operatorId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
}

internal sealed record ConnectionLeaseJoin(
    string AgentAppId,
    string AppId,
    string WorkspaceTeamId,
    string RuntimeCredentialValidationState,
    string ConnectionProjectId,
    string ConnectionId,
    string ConnectionAppId,
    string ConnectionBotUserId);

internal static class LeaseTargetEnumerable
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source) where T : class
    {
        foreach (var item in source)
            if (item is not null)
                yield return item;
    }
}

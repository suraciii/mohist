using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Shared helper for specs that exercise the adapter-facing routes now gated
/// on a runtime Socket lease. Acquires a real production runtime lease over
/// HTTP (never a test bypass) for a fully provisioned, verified target.
/// </summary>
public static class SlackRuntimeLeaseTestSupport
{
    public const string AdapterId = "adapter-spec";

    /// <summary>
    /// Puts a setup-created manager enrollment into the verified, provisioned
    /// state the runtime lease requires (the production hello path would have
    /// done this): credentials stored at the enrollment addresses and the
    /// validation state promoted to Verified. Returns the enrollment id.
    /// </summary>
    public static async Task<string> ProvisionVerifiedManagerAsync(
        MohistIntegrationFixture fixture, string teamId, string appToken, string botToken)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var enrollment = await db.SlackWorkspaceEnrollments.SingleAsync(row => row.WorkspaceTeamId == teamId);
        enrollment.RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified;
        await db.SaveChangesAsync();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollment.Id, SecretKind.AppToken),
            Encoding.UTF8.GetBytes(appToken));
        await secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollment.Id, SecretKind.BotToken),
            Encoding.UTF8.GetBytes(botToken));
        return enrollment.Id;
    }

    /// <summary>
    /// One active enrollment per team (the team unique index forbids a
    /// second one): created once and reused by every seed in the collection.
    /// </summary>
    public static async Task<string> EnsureEnrollmentAsync(MohistIntegrationFixture fixture, string teamId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var existing = await db.SlackWorkspaceEnrollments.FirstOrDefaultAsync(row => row.WorkspaceTeamId == teamId);
        if (existing is not null)
            return existing.Id;
        var enrollmentId = $"enrollment-{teamId}";
        var now = fixture.TimeProvider.GetUtcNow();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = enrollmentId,
            WorkspaceTeamId = teamId,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            PlanCode = "unknown",
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return enrollmentId;
    }

    public static async Task<string> AcquireConnectionLeaseAsync(
        MohistIntegrationFixture fixture, string projectId, string connectionId)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/slack-adapter/leases/acquire", new
        {
            kind = Mohist.Server.Slack.Domain.SlackLeaseKind.Runtime,
            target = new
            {
                kind = Mohist.Server.Slack.Domain.SlackLeaseTargetKind.Connection,
                projectId,
                connectionId,
            },
            adapterId = AdapterId,
        });
        response.EnsureSuccessStatusCode();
        return await ReadLeaseIdAsync(response);
    }

    public static async Task<string> AcquireManagerLeaseAsync(
        MohistIntegrationFixture fixture, string enrollmentId, string workspaceTeamId) =>
        await AcquireManagerLeaseAsync(fixture, enrollmentId, workspaceTeamId, AdapterId);

    public static async Task<string> AcquireManagerLeaseAsync(
        MohistIntegrationFixture fixture, string enrollmentId, string workspaceTeamId, string adapterId)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/slack-adapter/leases/acquire", new
        {
            kind = Mohist.Server.Slack.Domain.SlackLeaseKind.Runtime,
            target = new
            {
                kind = Mohist.Server.Slack.Domain.SlackLeaseTargetKind.Manager,
                enrollmentId,
                workspaceTeamId,
            },
            adapterId,
        });
        response.EnsureSuccessStatusCode();
        return await ReadLeaseIdAsync(response);
    }

    private static async Task<string> ReadLeaseIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("leaseId").GetString()!;
    }
}

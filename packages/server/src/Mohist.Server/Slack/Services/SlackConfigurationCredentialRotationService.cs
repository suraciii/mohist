using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public interface ISlackConfigurationCredentialStore
{
    Task<SlackConfigurationCredentialPersistence> StoreVerifiedRotationAsync(
        string enrollmentId,
        string workspaceTeamId,
        int expectedGeneration,
        SlackConfigurationCredentialPair credentials,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        CancellationToken ct = default);
}

public sealed record SlackConfigurationCredentialPersistence(bool Stored, string? ErrorClass = null)
{
    public static SlackConfigurationCredentialPersistence NotFound { get; } = new(false, "enrollment_not_found");
}

public sealed class ProtectedSlackConfigurationCredentialStore(
    IDbContextFactory<MohistDbContext> dbFactory,
    AesGcmSecretStore secrets) : ISlackConfigurationCredentialStore, IScopedService
{
    public async Task<SlackConfigurationCredentialPersistence> StoreVerifiedRotationAsync(
        string enrollmentId,
        string workspaceTeamId,
        int expectedGeneration,
        SlackConfigurationCredentialPair credentials,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        credentials.Validate();
        if (expectedGeneration < 0 || expiresAt <= now)
            return new(false, "invalid_rotation_result");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.SlackWorkspaceEnrollments
            .Where(item => item.Id == enrollmentId
                && item.WorkspaceTeamId == workspaceTeamId
                && item.ConfigurationCredentialGeneration == expectedGeneration)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ConfigurationCredentialRef, enrollmentId)
                .SetProperty(item => item.ConfigurationCredentialGeneration, expectedGeneration + 1)
                .SetProperty(item => item.ConfigurationCredentialExpiresAt, expiresAt)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (updated != 1)
        {
            var current = await db.SlackWorkspaceEnrollments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == enrollmentId, ct);
            if (current is null)
                return SlackConfigurationCredentialPersistence.NotFound;
            if (!string.Equals(current.WorkspaceTeamId, workspaceTeamId, StringComparison.Ordinal))
                return new(false, "workspace_mismatch");
            return new(false, "configuration_credential_generation_conflict");
        }
        await secrets.StoreAtomicallyAsync(db,
        [
            new(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.ConfigurationAccessToken), System.Text.Encoding.UTF8.GetBytes(credentials.AccessToken)),
            new(SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.ConfigurationRefreshToken), System.Text.Encoding.UTF8.GetBytes(credentials.RefreshToken)),
        ], ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(true);
    }
}

public sealed class SlackConfigurationCredentialRotationService : IScopedService
{
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly ISlackConfigurationCredentialPort _port;
    private readonly ISlackConfigurationCredentialStore _credentials;
    private readonly TimeProvider _timeProvider;

    public SlackConfigurationCredentialRotationService(SlackWorkspaceEnrollmentStore enrollments, ISlackConfigurationCredentialPort port, ISlackConfigurationCredentialStore credentials, TimeProvider timeProvider)
    {
        _enrollments = enrollments;
        _port = port;
        _credentials = credentials;
        _timeProvider = timeProvider;
    }

    public async Task<SlackConfigurationCredentialRotation> RotateAsync(string enrollmentId, SlackConfigurationCredentialPair currentCredentials, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        currentCredentials.Validate();
        var enrollment = await _enrollments.GetAsync(enrollmentId, ct);
        if (enrollment is null)
            return SlackConfigurationCredentialRotation.NotFound;

        var result = await _port.RotateAsync(currentCredentials, ct);
        if (result.Outcome != SlackConfigurationCredentialRotationOutcome.Succeeded)
            return new(result.Outcome, result.ErrorClass);
        if (result.Credentials is null || string.IsNullOrWhiteSpace(result.WorkspaceTeamId) || result.ExpiresAt is null)
            return new(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, "invalid_rotation_result");

        var persisted = await _credentials.StoreVerifiedRotationAsync(
            enrollment.Id,
            result.WorkspaceTeamId,
            enrollment.ConfigurationCredentialGeneration,
            result.Credentials,
            result.ExpiresAt.Value,
            _timeProvider.GetUtcNow(),
            ct);
        return persisted.Stored
            ? new(SlackConfigurationCredentialRotationOutcome.Succeeded)
            : new(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, persisted.ErrorClass);
    }
}

public sealed record SlackConfigurationCredentialRotation(SlackConfigurationCredentialRotationOutcome Outcome, string? ErrorClass = null)
{
    public static SlackConfigurationCredentialRotation NotFound { get; } = new(SlackConfigurationCredentialRotationOutcome.DefiniteFailure, "enrollment_not_found");
}

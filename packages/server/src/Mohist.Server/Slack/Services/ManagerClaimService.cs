using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class ManagerClaimService : IScopedService
{
    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(10);
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly TimeProvider _timeProvider;

    public ManagerClaimService(
        SlackWorkspaceEnrollmentStore enrollments,
        TimeProvider timeProvider)
    {
        _enrollments = enrollments;
        _timeProvider = timeProvider;
    }

    public async Task<SlackManagerClaimIssued> IssueAsync(
        string enrollmentId,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now + ClaimLifetime;
        var code = CreateCode();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        var issuance = await _enrollments.IssueManagerClaimAsync(
            enrollmentId, hash, now, expiresAt, ct);
        if (issuance.Enrollment is null)
            throw new InvalidOperationException("The workspace enrollment disappeared while issuing a claim.");
        return issuance.Issued
            ? new(code, issuance.Enrollment.ManagerClaimExpiresAt ?? expiresAt)
            : SlackManagerClaimIssued.None;
    }

    public async Task<SlackManagerClaimConsumption> ConsumeAsync(
        string workspaceTeamId,
        string slackUserId,
        string code,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new(SlackManagerClaimOutcome.Invalid);

        var enrollment = await _enrollments.GetByTeamAsync(workspaceTeamId, ct);
        if (enrollment is null)
            return new(SlackManagerClaimOutcome.Rejected);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));
        return await _enrollments.ConsumeManagerClaimAsync(
            enrollment.Id,
            workspaceTeamId,
            slackUserId,
            hash,
            _timeProvider.GetUtcNow(),
            ct);
    }

    private static string CreateCode() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

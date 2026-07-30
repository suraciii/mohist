using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Data.Secrets;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Slack;

public sealed record SlackOwnerClaimCode(string Value, DateTimeOffset ExpiresAt);

public sealed record SlackInboundDm(string SenderSlackUserId, string Text);

public sealed record SlackInboundDecision(string Kind, string? Reason)
{
    public bool IsRejected => string.Equals(Kind, SlackInboundDecisionKind.Rejected, StringComparison.Ordinal);
}

public static class SlackInboundDecisionKind
{
    public const string Claimed = "claimed";
    public const string Transferred = "transferred";
    public const string AcceptedOwnerTask = "accepted_owner_task";
    public const string Rejected = "rejected";
}

public sealed class SlackOwnerClaimService : IScopedService, IAgentConnectionProviderCleanup
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISecretStore _secrets;
    private readonly ISlackApiClient _slack;
    private readonly TimeProvider _time;

    public SlackOwnerClaimService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretStore secrets,
        ISlackApiClient slack,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _slack = slack;
        _time = time;
    }

    public async Task<SlackOwnerClaimCode> GenerateAsync(
        string projectId,
        string connectionId,
        TimeSpan? lifetime = null,
        CancellationToken ct = default) =>
        await GenerateAsync(projectId, connectionId, SlackOwnerClaimCodeKinds.Initial, lifetime, ct);

    public async Task<SlackOwnerClaimCode> GenerateAsync(
        string projectId,
        string connectionId,
        string kind,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var connection = await db.AgentConnections.FirstOrDefaultAsync(
            row => row.ProjectId == projectId && row.Id == connectionId && row.DeletedAt == null, ct);
        if (connection is null)
            throw new InvalidOperationException("Connection was not found.");

        if (string.Equals(kind, SlackOwnerClaimCodeKinds.Initial, StringComparison.Ordinal))
        {
            if (connection.OwnerSlackUserId is not null || connection.SetupProgress == SetupProgressKind.Complete)
                throw new InvalidOperationException("The Connection already has an owner.");
            if (connection.SetupProgress != SetupProgressKind.ClaimOwner)
                throw new InvalidOperationException("Slack setup must be verified before generating an owner claim code.");
        }
        else if (string.Equals(kind, SlackOwnerClaimCodeKinds.Transfer, StringComparison.Ordinal))
        {
            if (connection.OwnerSlackUserId is null)
                throw new InvalidOperationException("The Connection has no current owner. Use claim-owner instead.");
            if (connection.SetupProgress != SetupProgressKind.Complete)
                throw new InvalidOperationException("Slack setup must be complete before transferring ownership.");
        }
        else
        {
            throw new ArgumentException($"Unknown owner claim code kind '{kind}'.", nameof(kind));
        }

        var now = _time.GetUtcNow();
        var expiresAt = now.Add(lifetime ?? DefaultLifetime);
        var id = $"claim_{Guid.NewGuid():N}";
        var value = CreateCode();

        var previous = await db.SlackOwnerClaimCodes
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId && row.Kind == kind && row.UsedAt == null && row.SupersededBy == null)
            .ToListAsync(ct);
        foreach (var row in previous)
            row.SupersededBy = id;

        db.SlackOwnerClaimCodes.Add(new SlackOwnerClaimCodeRow
        {
            Id = id,
            ProjectId = projectId,
            ConnectionId = connectionId,
            CodeHash = Hash(value),
            Kind = kind,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return new(value, expiresAt);
    }

    public async Task<SlackInboundDecision> HandleInboundDmAsync(
        string projectId,
        string connectionId,
        SlackInboundDm inbound,
        CancellationToken ct = default)
    {
        var text = inbound.Text.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var connection = await db.AgentConnections.AsNoTracking().FirstOrDefaultAsync(
            row => row.ProjectId == projectId && row.Id == connectionId && row.DeletedAt == null, ct);
        if (connection is null)
            return Reject("Connection was not found.");

        var code = await db.SlackOwnerClaimCodes.FirstOrDefaultAsync(
            row => row.ProjectId == projectId && row.ConnectionId == connectionId && row.CodeHash == Hash(text), ct);
        if (code is not null)
        {
            if (code.UsedAt is not null || code.SupersededBy is not null)
                return Reject("This owner claim code is no longer valid. Generate a new code.");
            if (_time.GetUtcNow() >= code.ExpiresAt)
                return Reject("This owner claim code has expired. Generate a new code.");
            if (string.Equals(code.Kind, SlackOwnerClaimCodeKinds.Transfer, StringComparison.Ordinal))
            {
                if (connection.OwnerSlackUserId is null)
                    return Reject("This Connection has no current owner to transfer. Use claim-owner instead.");
                return await TryTransferAsync(db, connection, code, inbound.SenderSlackUserId, ct);
            }
            return await TryClaimAsync(db, connection, code, inbound.SenderSlackUserId, ct);
        }

        if (connection.SetupProgress != SetupProgressKind.Complete)
            return Reject("Owner setup is not complete. Send a current owner claim code first.");
        if (!string.Equals(connection.OwnerSlackUserId, inbound.SenderSlackUserId, StringComparison.Ordinal))
            return Reject("This Slack Connection is available only to its owner.");
        return new(SlackInboundDecisionKind.AcceptedOwnerTask, null);
    }

    public async Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackOwnerClaimCodes
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.SlackOwnerClaimCodes.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<SlackInboundDecision> TryClaimAsync(
        MohistDbContext db,
        AgentConnectionRow connection,
        SlackOwnerClaimCodeRow code,
        string senderSlackUserId,
        CancellationToken ct)
    {
        var token = await _secrets.LoadAsync(
            new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken), ct);
        if (token is null || token.Length == 0)
            return Reject("Slack setup is incomplete: configure the Bot token before claiming ownership.");

        var userResponse = await _slack.UsersInfoAsync(
            senderSlackUserId, Encoding.UTF8.GetString(token), ct);
        if (!IsEligibleMember(userResponse, connection.WorkspaceTeamId, senderSlackUserId))
            return Reject("Only a current regular member of this Slack workspace can claim ownership.");

        var now = _time.GetUtcNow();
        var changed = await db.AgentConnections
            .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id && row.DeletedAt == null && row.OwnerSlackUserId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.OwnerSlackUserId, senderSlackUserId)
                .SetProperty(row => row.SetupProgress, SetupProgressKind.Complete)
                .SetProperty(row => row.UpdatedAt, now), ct);
        if (changed == 0)
            return Reject("This Slack Connection already has an owner.");

        code.UsedAt = now;
        await db.SaveChangesAsync(ct);
        return new(SlackInboundDecisionKind.Claimed, null);
    }

    private async Task<SlackInboundDecision> TryTransferAsync(
        MohistDbContext db,
        AgentConnectionRow connection,
        SlackOwnerClaimCodeRow code,
        string senderSlackUserId,
        CancellationToken ct)
    {
        var token = await _secrets.LoadAsync(
            new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken), ct);
        if (token is null || token.Length == 0)
            return Reject("Slack setup is incomplete: configure the Bot token before transferring ownership.");

        var userResponse = await _slack.UsersInfoAsync(
            senderSlackUserId, Encoding.UTF8.GetString(token), ct);
        if (!IsEligibleMember(userResponse, connection.WorkspaceTeamId, senderSlackUserId))
            return Reject("Only a current regular member of this Slack workspace can take over ownership.");

        var currentOwner = connection.OwnerSlackUserId;
        if (currentOwner is null)
            return Reject("This Connection no longer has a current owner. Use claim-owner instead.");
        if (string.Equals(currentOwner, senderSlackUserId, StringComparison.Ordinal))
            return Reject("You are already the owner of this Connection.");

        var now = _time.GetUtcNow();
        var changed = await db.AgentConnections
            .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id && row.DeletedAt == null && row.OwnerSlackUserId == currentOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.OwnerSlackUserId, senderSlackUserId)
                .SetProperty(row => row.UpdatedAt, now), ct);
        if (changed == 0)
            return Reject("Ownership has already been transferred. Generate a new transfer code if you still need to take ownership.");

        code.UsedAt = now;
        await db.SaveChangesAsync(ct);
        return new(SlackInboundDecisionKind.Transferred, null);
    }

    private static bool IsEligibleMember(
        SlackUserInfoResponse response,
        string workspaceTeamId,
        string senderSlackUserId)
    {
        var user = response.User;
        if (!response.Ok || user is null || !string.Equals(user.Id, senderSlackUserId, StringComparison.Ordinal)) return false;
        if (!string.Equals(user.TeamId, workspaceTeamId, StringComparison.Ordinal)) return false;
        if (user.TeamIds is { Count: > 0 } && user.TeamIds.Any(teamId => !string.Equals(teamId, workspaceTeamId, StringComparison.Ordinal))) return false;
        return !user.IsBot && !user.Deleted && !user.IsGuest && !user.IsRestricted && !user.IsUltraRestricted;
    }

    private static SlackInboundDecision Reject(string reason) => new(SlackInboundDecisionKind.Rejected, reason);

    private static string CreateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
}

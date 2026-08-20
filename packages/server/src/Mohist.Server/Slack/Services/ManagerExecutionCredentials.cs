using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Slack.Services;

public enum ManagerExecutionLeaseKind
{
    Management,
    Reply,
}

public static class ManagerExecutionRuntimeCapabilities
{
    public const string GrantV1 = "manager-execution-grant-v1";
    public const string EpochV1 = "manager-deployment-epoch-v1";
    public const string PrivateBrokerV1 = "manager-private-broker-v1";
    public const string PiScopedExecutorV1 = "manager-pi-scoped-executor-v1";
    public const string IsolatedOpenCodeV1 = "manager-opencode-isolated-v1";
    public const string RedactionV1 = "manager-redaction-v1";

    public static IReadOnlySet<string> Required { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        GrantV1,
        EpochV1,
        PrivateBrokerV1,
        PiScopedExecutorV1,
        IsolatedOpenCodeV1,
        RedactionV1,
    };

    public static bool Supports(RunnerInfo info) =>
        Required.All(required => info.Capabilities.Contains(required, StringComparer.Ordinal));
}

/// <summary>
/// The non-secret facts that make a Manager execution lease useful only for
/// its original Slack turn. ConnectionId is the Enrollment identity for
/// Manager executions.
/// </summary>
public sealed record ManagerExecutionOrigin(
    string WorkspaceId,
    string ConversationId,
    string ThreadRootMessageId,
    string TriggeringMessageId,
    string ActorId,
    string EnrollmentId,
    string SessionId,
    string DispatchRef);

public sealed record ManagerExecutionLeaseMetadata(
    string LeaseId,
    string CredentialHash,
    ManagerExecutionLeaseKind Kind,
    string ExecutionId,
    ManagerExecutionOrigin Origin,
    string DeploymentEpoch,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlySet<string> Capabilities,
    bool Active = true);

/// <summary>
/// Plaintext values exist only in this poll response DTO and the Runner's
/// in-memory wrapper. This type must never be added to a durable dispatch,
/// Session, AgentJob, inbox, outbox, or event record.
/// </summary>
public sealed record ManagerExecutionGrant(
    [property: JsonPropertyName("managementCredential")] string ManagementCredential,
    [property: JsonPropertyName("replyCredential")] string ReplyCredential,
    [property: JsonPropertyName("executionId")] string ExecutionId,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("deploymentEpoch")] string DeploymentEpoch);

public sealed record ManagerExecutionIssueRequest(
    string ExecutionId,
    ManagerExecutionOrigin Origin,
    DateTimeOffset Now,
    TimeSpan Lifetime,
    IReadOnlySet<string>? ManagementCapabilities = null);

public sealed record ManagerExecutionCredentialContext(
    ManagerExecutionLeaseMetadata Lease,
    ManagerExecutionLeaseKind Kind)
{
    public const string HttpContextItemKey = "mohist.manager.execution-credential";
}

public sealed record ManagerExecutionValidationResult(
    bool Allowed,
    string Code,
    string Message,
    ManagerExecutionLeaseMetadata? Lease = null)
{
    public static ManagerExecutionValidationResult Denied(string code, string message) =>
        new(false, code, message);

    public static ManagerExecutionValidationResult Allow(ManagerExecutionLeaseMetadata lease) =>
        new(true, "authorized", "authorized", lease);
}

public interface IManagerDeploymentEpoch
{
    string Current { get; }
    bool Available { get; }
    string Advance();
    void Invalidate();
}

/// <summary>
/// Process-local implementation of the deployment-epoch seam. Production
/// topologies can replace this with their shared atomic epoch provider. A
/// missing or invalid epoch is deliberately unavailable rather than a
/// permissive fallback.
/// </summary>
public sealed class ManagerDeploymentEpoch : IManagerDeploymentEpoch, IHostedService
{
    private readonly object _gate = new();
    private readonly IManagerExecutionLeaseStore? _store;
    private string? _current = NewEpoch();
    private bool _available = true;

    public ManagerDeploymentEpoch(IManagerExecutionLeaseStore? store = null) => _store = store;

    public string Current
    {
        get
        {
            lock (_gate)
                return _current ?? string.Empty;
        }
    }

    public bool Available
    {
        get
        {
            lock (_gate)
                return _available && !string.IsNullOrWhiteSpace(_current);
        }
    }

    public string Advance()
    {
        lock (_gate)
        {
            _store?.RevokeAll();
            _current = NewEpoch();
            _available = true;
            return _current;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _available = false;
            _current = null;
            _store?.RevokeAll();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Invalidate();
        return Task.CompletedTask;
    }

    private static string NewEpoch() => $"mepoch_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
}

public interface IManagerExecutionLeaseStore
{
    bool Available { get; }
    void Put(ManagerExecutionLeaseMetadata lease);
    ManagerExecutionLeaseMetadata? Find(string credentialHash);
    int RevokeExecution(string executionId);
    int RevokeExecutionPrefix(string executionPrefix);
    int RevokeAll();
    int RemoveExpired(DateTimeOffset now);
    int Count { get; }
}

/// <summary>
/// Short-lived runtime store. The dictionary value contains only a hash and
/// non-secret binding metadata. It is intentionally not an EF store and has
/// no serialization or recovery path.
/// </summary>
public sealed class ManagerExecutionLeaseStore : IManagerExecutionLeaseStore, IDisposable
{
    private readonly ConcurrentDictionary<string, ManagerExecutionLeaseMetadata> _leases = new(StringComparer.Ordinal);
    private int _available = 1;

    public bool Available => Volatile.Read(ref _available) == 1;
    public int Count => _leases.Count;

    public void Put(ManagerExecutionLeaseMetadata lease)
    {
        if (!Available)
            throw new InvalidOperationException("Manager lease store is unavailable.");
        ArgumentNullException.ThrowIfNull(lease);
        if (string.IsNullOrWhiteSpace(lease.CredentialHash))
            throw new ArgumentException("A lease hash is required.", nameof(lease));
        _leases[lease.CredentialHash] = lease;
    }

    public ManagerExecutionLeaseMetadata? Find(string credentialHash)
    {
        if (!Available || string.IsNullOrWhiteSpace(credentialHash))
            return null;
        return _leases.TryGetValue(credentialHash, out var lease) && lease.Active ? lease : null;
    }

    public int RevokeExecution(string executionId)
    {
        if (!Available || string.IsNullOrWhiteSpace(executionId))
            return 0;
        var revoked = 0;
        foreach (var pair in _leases)
        {
            if (!string.Equals(pair.Value.ExecutionId, executionId, StringComparison.Ordinal))
                continue;
            if (_leases.TryUpdate(pair.Key, pair.Value with { Active = false }, pair.Value))
                revoked++;
        }
        return revoked;
    }

    public int RevokeExecutionPrefix(string executionPrefix)
    {
        if (!Available || string.IsNullOrWhiteSpace(executionPrefix))
            return 0;
        var revoked = 0;
        foreach (var pair in _leases)
        {
            if (!pair.Value.ExecutionId.StartsWith(executionPrefix, StringComparison.Ordinal))
                continue;
            if (_leases.TryUpdate(pair.Key, pair.Value with { Active = false }, pair.Value))
                revoked++;
        }
        return revoked;
    }

    public int RevokeAll()
    {
        if (!Available)
            return 0;
        var revoked = 0;
        foreach (var pair in _leases)
        {
            if (_leases.TryUpdate(pair.Key, pair.Value with { Active = false }, pair.Value))
                revoked++;
        }
        return revoked;
    }

    public int RemoveExpired(DateTimeOffset now)
    {
        if (!Available)
            return 0;
        var removed = 0;
        foreach (var pair in _leases)
        {
            if (pair.Value.ExpiresAt > now)
                continue;
            if (_leases.TryRemove(pair.Key, out _))
                removed++;
        }
        return removed;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _available, 0);
        _leases.Clear();
    }
}

public sealed class ManagerExecutionCapabilityIssuer
{
    public const int CredentialBytes = 32;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly IManagerExecutionLeaseStore _store;
    private readonly IManagerDeploymentEpoch _epoch;

    public ManagerExecutionCapabilityIssuer(
        IManagerExecutionLeaseStore store,
        IManagerDeploymentEpoch epoch)
    {
        _store = store;
        _epoch = epoch;
    }

    public ManagerExecutionGrant Issue(ManagerExecutionIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExecutionId))
            throw new ArgumentException("Execution identity is required.", nameof(request));
        ValidateOrigin(request.Origin);
        if (!_epoch.Available || string.IsNullOrWhiteSpace(_epoch.Current) || !_store.Available)
            throw new InvalidOperationException("Manager execution authorization is unavailable.");

        // A redelivery or replacement with the same execution identity
        // fences the interrupted lease before issuing its fresh values.
        _store.RevokeExecution(request.ExecutionId);
        var issuedAt = request.Now;
        var expiresAt = issuedAt + (request.Lifetime <= TimeSpan.Zero ? DefaultLifetime : request.Lifetime);
        var management = NewCredential();
        var reply = NewCredential();
        var capabilities = request.ManagementCapabilities ?? ManagerCapabilityCatalog.ManagementCapabilities;
        var managementLease = NewMetadata(
            ManagerExecutionLeaseKind.Management,
            management,
            request,
            issuedAt,
            expiresAt,
            capabilities);
        var replyLease = NewMetadata(
            ManagerExecutionLeaseKind.Reply,
            reply,
            request,
            issuedAt,
            expiresAt,
            new HashSet<string>(StringComparer.Ordinal) { "manager.reply" });

        try
        {
            _store.Put(managementLease);
            _store.Put(replyLease);
        }
        catch
        {
            _store.RevokeExecution(request.ExecutionId);
            throw;
        }

        return new ManagerExecutionGrant(management, reply, request.ExecutionId, expiresAt, _epoch.Current);
    }

    public ManagerExecutionGrant? IssueFor(
        WorkDispatch work,
        DateTimeOffset? now = null,
        TimeSpan? lifetime = null)
    {
        if (!ManagerExecutionBinding.TryRead(work, out var binding))
            return null;
        return Issue(new ManagerExecutionIssueRequest(
            binding.ExecutionId,
            binding.Origin,
            now ?? DateTimeOffset.UtcNow,
            lifetime ?? DefaultLifetime));
    }

    public ManagerExecutionValidationResult ValidatePresented(
        string? credential,
        ManagerExecutionLeaseKind kind,
        string capability,
        DateTimeOffset now)
    {
        if (!_epoch.Available || !_store.Available)
            return ManagerExecutionValidationResult.Denied(
                "manager_authorization_unavailable",
                "Manager authorization is unavailable; inspect the current Manager status and retry explicitly.");
        if (string.IsNullOrWhiteSpace(credential) || credential.Length < 32)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is invalid or unavailable; request a fresh turn.");
        var lease = _store.Find(Hash(credential));
        if (lease is null || lease.Kind != kind)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is unknown, revoked, or already consumed; request a fresh turn.");
        if (!string.Equals(lease.DeploymentEpoch, _epoch.Current, StringComparison.Ordinal))
            return ManagerExecutionValidationResult.Denied(
                "manager_epoch_changed",
                "The Manager Server restarted; request a fresh turn before retrying.");
        if (now >= lease.ExpiresAt)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_expired",
                "The Manager execution expired; inspect the current state and start a fresh turn.");
        if (!lease.Capabilities.Contains(capability, StringComparer.Ordinal))
            return ManagerExecutionValidationResult.Denied(
                "manager_capability_not_available",
                "This Manager operation is outside the execution capability allowlist.");
        return ManagerExecutionValidationResult.Allow(lease);
    }

    public ManagerExecutionValidationResult Validate(
        string? credential,
        ManagerExecutionLeaseKind kind,
        string capability,
        string executionId,
        ManagerExecutionOrigin origin,
        DateTimeOffset now)
    {
        if (!_epoch.Available || !_store.Available)
            return ManagerExecutionValidationResult.Denied(
                "manager_authorization_unavailable",
                "Manager authorization is unavailable; inspect the current Manager status and retry explicitly.");
        if (string.IsNullOrWhiteSpace(credential) || credential.Length < 32)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is invalid or unavailable; request a fresh turn.");
        if (string.IsNullOrWhiteSpace(capability))
            return ManagerExecutionValidationResult.Denied(
                "manager_capability_invalid",
                "The requested Manager capability is invalid; choose an allowlisted operation.");
        var hash = Hash(credential);
        var lease = _store.Find(hash);
        if (lease is null)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is unknown, revoked, or already consumed; request a fresh turn.");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(lease.CredentialHash),
                Encoding.UTF8.GetBytes(hash)))
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is invalid; request a fresh turn.");
        if (lease.Kind != kind)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_scope_mismatch",
                "This credential is not valid for the requested Manager route.");
        if (!string.Equals(lease.DeploymentEpoch, _epoch.Current, StringComparison.Ordinal))
            return ManagerExecutionValidationResult.Denied(
                "manager_epoch_changed",
                "The Manager Server restarted; request a fresh turn before retrying.");
        if (!string.Equals(lease.ExecutionId, executionId, StringComparison.Ordinal)
            || !Equals(lease.Origin, origin))
            return ManagerExecutionValidationResult.Denied(
                "manager_origin_mismatch",
                "The Manager request does not match its current execution origin.");
        if (now >= lease.ExpiresAt)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_expired",
                "The Manager execution expired; inspect the current state and start a fresh turn.");
        if (!lease.Capabilities.Contains(capability))
            return ManagerExecutionValidationResult.Denied(
                "manager_capability_not_available",
                "This Manager operation is outside the execution capability allowlist.");
        return ManagerExecutionValidationResult.Allow(lease);
    }

    public int RevokeExecution(string executionId) => _store.RevokeExecution(executionId);

    public int RevokeWork(string agentJobId, string workId) =>
        _store.RevokeExecutionPrefix($"manager:{agentJobId}:{workId}:");

    private ManagerExecutionLeaseMetadata NewMetadata(
        ManagerExecutionLeaseKind kind,
        string credential,
        ManagerExecutionIssueRequest request,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        IReadOnlySet<string> capabilities) =>
        new(
            $"mlease_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
            Hash(credential),
            kind,
            request.ExecutionId,
            request.Origin,
            _epoch.Current,
            issuedAt,
            expiresAt,
            new HashSet<string>(capabilities, StringComparer.Ordinal));

    private static string NewCredential() => Base64Url(RandomNumberGenerator.GetBytes(CredentialBytes));

    private static string Hash(string credential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))).ToLowerInvariant();

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateOrigin(ManagerExecutionOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(origin.WorkspaceId)
            || string.IsNullOrWhiteSpace(origin.ConversationId)
            || string.IsNullOrWhiteSpace(origin.ThreadRootMessageId)
            || string.IsNullOrWhiteSpace(origin.TriggeringMessageId)
            || string.IsNullOrWhiteSpace(origin.ActorId)
            || string.IsNullOrWhiteSpace(origin.EnrollmentId)
            || string.IsNullOrWhiteSpace(origin.SessionId)
            || string.IsNullOrWhiteSpace(origin.DispatchRef))
            throw new ArgumentException("A complete Manager Slack origin is required.", nameof(origin));
    }
}

public sealed record ManagerExecutionBinding(string ExecutionId, ManagerExecutionOrigin Origin)
{
    public static bool TryRead(WorkDispatch work, out ManagerExecutionBinding binding)
    {
        binding = null!;
        if (!string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
            || !string.Equals(work.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(work.AgentJobId)
            || string.IsNullOrWhiteSpace(work.With))
            return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(work.With);
            if (!document.RootElement.TryGetProperty("slackExecutionContext", out var contextElement))
                return false;
            var context = System.Text.Json.JsonSerializer.Deserialize<AgentSlackExecutionContext>(
                contextElement.GetRawText(), JSON.Options);
            var anchor = context?.ReplyAnchor;
            if (anchor is null
                || !string.Equals(anchor.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                || !string.Equals(anchor.OwnerKind, SlackDeliveryOwnerKinds.Manager, StringComparison.Ordinal))
                return false;
            var origin = new ManagerExecutionOrigin(
                anchor.WorkspaceId,
                anchor.ConversationId,
                anchor.ThreadRootMessageId,
                anchor.TriggeringMessageId,
                anchor.InitiatingMemberId,
                anchor.ConnectionId,
                anchor.SessionId,
                anchor.DispatchRef);
            binding = new ManagerExecutionBinding(
                $"manager:{work.AgentJobId}:{work.WorkId}:{work.RecoveryGeneration}",
                origin);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

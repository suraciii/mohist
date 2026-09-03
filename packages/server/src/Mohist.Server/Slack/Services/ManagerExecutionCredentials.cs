using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
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
public sealed class ManagerDeploymentEpoch : IManagerDeploymentEpoch, IHostedLifecycleService
{
    private readonly object _gate = new();
    private readonly IManagerExecutionLeaseStore? _store;
    private string? _current;
    private bool _available;

    public ManagerDeploymentEpoch(IManagerExecutionLeaseStore? store = null)
    {
        _store = store;
        try
        {
            _current = store?.IsShared == true
                ? store.ReadDeploymentEpoch()
                : NewEpoch();
            _available = !string.IsNullOrWhiteSpace(_current) && (store is null || store.Available);
        }
        catch
        {
            _current = null;
            _available = false;
        }
    }

    public string Current
    {
        get
        {
            lock (_gate)
            {
                RefreshSharedEpoch();
                return _current ?? string.Empty;
            }
        }
    }

    public bool Available
    {
        get
        {
            lock (_gate)
            {
                RefreshSharedEpoch();
                return _available && !string.IsNullOrWhiteSpace(_current);
            }
        }
    }

    public string Advance()
    {
        lock (_gate)
        {
            try
            {
                _current = _store?.IsShared == true
                    ? _store.AdvanceDeploymentEpoch()
                    : NewEpoch();
                if (string.IsNullOrWhiteSpace(_current))
                    throw new InvalidOperationException("Manager deployment epoch is unavailable.");
                _available = _store is null || _store.Available;
                if (!_available)
                    throw new InvalidOperationException("Manager lease store is unavailable.");
                return _current;
            }
            catch
            {
                _current = null;
                _available = false;
                throw;
            }
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

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance();
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        Invalidate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RefreshSharedEpoch()
    {
        if (_store?.IsShared != true || !_available)
            return;

        try
        {
            var current = _store.ReadDeploymentEpoch();
            if (string.IsNullOrWhiteSpace(current))
            {
                _current = null;
                _available = false;
                return;
            }

            _current = current;
            _available = _store.Available;
        }
        catch
        {
            _current = null;
            _available = false;
        }
    }

    private static string NewEpoch() => $"mepoch_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
}

public interface IManagerExecutionLeaseStore
{
    bool Available { get; }
    bool IsShared { get; }
    string? ReadDeploymentEpoch();
    string? AdvanceDeploymentEpoch();
    void Put(ManagerExecutionLeaseMetadata lease);
    ManagerExecutionLeaseMetadata? Find(string credentialHash);
    ManagerExecutionLeaseMetadata? FindIncludingRevoked(string credentialHash);
    int RevokeExecution(string executionId);
    int RevokeExecutionPrefix(string executionPrefix);
    int RevokeAll();
    int RemoveExpired(DateTimeOffset now);
    int Count { get; }
}

/// <summary>
/// Stores only lease hashes and non-secret binding metadata. Production uses
/// the configured SQLite database so every Server instance sees the same
/// leases and deployment epoch; the parameterless constructor remains an
/// in-memory test seam.
/// </summary>
public sealed class ManagerExecutionLeaseStore : IManagerExecutionLeaseStore, IDisposable
{
    private const string EpochRowId = "manager";
    private readonly ConcurrentDictionary<string, ManagerExecutionLeaseMetadata> _leases = new(StringComparer.Ordinal);
    private readonly IDbContextFactory<MohistDbContext>? _dbFactory;
    private readonly object _databaseGate = new();
    private int _available = 1;
    private bool _schemaReady;

    public ManagerExecutionLeaseStore(IDbContextFactory<MohistDbContext>? dbFactory = null) => _dbFactory = dbFactory;

    public bool Available => Volatile.Read(ref _available) == 1;
    public bool IsShared => _dbFactory is not null;
    public int Count => IsShared ? WithDatabase(connection => ScalarInt(connection, "SELECT COUNT(*) FROM ManagerExecutionLeases;")) : _leases.Count;

    public string? ReadDeploymentEpoch()
    {
        if (!IsShared) return null;
        return WithDatabase(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Epoch FROM ManagerDeploymentEpochs WHERE Id = @id;";
            Add(command, "@id", EpochRowId);
            var value = command.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(value)) return value;
            var epoch = NewEpoch();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO ManagerDeploymentEpochs (Id, Epoch) VALUES (@id, @epoch);";
            Add(insert, "@id", EpochRowId);
            Add(insert, "@epoch", epoch);
            insert.ExecuteNonQuery();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT Epoch FROM ManagerDeploymentEpochs WHERE Id = @id;";
            Add(read, "@id", EpochRowId);
            return read.ExecuteScalar() as string;
        });
    }

    public string? AdvanceDeploymentEpoch()
    {
        if (!IsShared) return null;
        return WithDatabase(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var epoch = NewEpoch();
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO ManagerDeploymentEpochs (Id, Epoch) VALUES (@id, @epoch);";
                Add(insert, "@id", EpochRowId);
                Add(insert, "@epoch", epoch);
                insert.ExecuteNonQuery();
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE ManagerDeploymentEpochs SET Epoch = @epoch WHERE Id = @id; UPDATE ManagerExecutionLeases SET Active = 0 WHERE Active = 1;";
                Add(update, "@id", EpochRowId);
                Add(update, "@epoch", epoch);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            return epoch;
        });
    }

    public void Put(ManagerExecutionLeaseMetadata lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (string.IsNullOrWhiteSpace(lease.CredentialHash))
            throw new ArgumentException("A lease hash is required.", nameof(lease));
        if (!IsShared)
        {
            EnsureAvailable();
            _leases[lease.CredentialHash] = lease;
            return;
        }

        WithDatabase(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO ManagerExecutionLeases (CredentialHash, LeaseId, Kind, ExecutionId, WorkspaceId, ConversationId, ThreadRootMessageId, TriggeringMessageId, ActorId, EnrollmentId, SessionId, DispatchRef, DeploymentEpoch, IssuedAt, ExpiresAt, CapabilitiesJson, Active) VALUES (@hash, @lease, @kind, @execution, @workspace, @conversation, @thread, @trigger, @actor, @enrollment, @session, @dispatch, @epoch, @issued, @expires, @capabilities, @active);";
            AddLeaseParameters(command, lease);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public ManagerExecutionLeaseMetadata? Find(string credentialHash)
    {
        var lease = FindIncludingRevoked(credentialHash);
        return lease is { Active: true } ? lease : null;
    }

    public ManagerExecutionLeaseMetadata? FindIncludingRevoked(string credentialHash)
    {
        if (string.IsNullOrWhiteSpace(credentialHash) || !Available)
            return null;
        if (!IsShared)
            return _leases.TryGetValue(credentialHash, out var lease) ? lease : null;
        return WithDatabase(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT LeaseId, CredentialHash, Kind, ExecutionId, WorkspaceId, ConversationId, ThreadRootMessageId, TriggeringMessageId, ActorId, EnrollmentId, SessionId, DispatchRef, DeploymentEpoch, IssuedAt, ExpiresAt, CapabilitiesJson, Active FROM ManagerExecutionLeases WHERE CredentialHash = @hash;";
            Add(command, "@hash", credentialHash);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadLease(reader) : null;
        });
    }

    public int RevokeExecution(string executionId) =>
        RevokeWhere("ExecutionId = @value", executionId);

    public int RevokeExecutionPrefix(string executionPrefix) =>
        RevokeWhere("ExecutionId LIKE @value || '%'", executionPrefix);

    public int RevokeAll()
    {
        if (!Available) return 0;
        if (!IsShared)
        {
            var count = 0;
            foreach (var pair in _leases)
            {
                if (_leases.TryUpdate(pair.Key, pair.Value with { Active = false }, pair.Value)) count++;
            }
            return count;
        }
        return WithDatabase(connection => Execute(connection, "UPDATE ManagerExecutionLeases SET Active = 0 WHERE Active = 1;"));
    }

    public int RemoveExpired(DateTimeOffset now)
    {
        if (!Available) return 0;
        if (!IsShared)
        {
            var removed = 0;
            foreach (var pair in _leases)
            {
                if (pair.Value.ExpiresAt <= now && _leases.TryRemove(pair.Key, out _)) removed++;
            }
            return removed;
        }
        return WithDatabase(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ManagerExecutionLeases WHERE ExpiresAt <= @expires;";
            Add(command, "@expires", now.ToString("O", CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        });
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _available, 0);
        _leases.Clear();
    }

    private int RevokeWhere(string predicate, string value)
    {
        if (!Available || string.IsNullOrWhiteSpace(value)) return 0;
        if (!IsShared)
        {
            var revoked = 0;
            foreach (var pair in _leases)
            {
                var matches = predicate.StartsWith("ExecutionId LIKE", StringComparison.Ordinal)
                    ? pair.Value.ExecutionId.StartsWith(value, StringComparison.Ordinal)
                    : string.Equals(pair.Value.ExecutionId, value, StringComparison.Ordinal);
                if (matches && _leases.TryUpdate(pair.Key, pair.Value with { Active = false }, pair.Value)) revoked++;
            }
            return revoked;
        }
        return WithDatabase(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE ManagerExecutionLeases SET Active = 0 WHERE Active = 1 AND {predicate};";
            Add(command, "@value", value);
            return command.ExecuteNonQuery();
        });
    }

    private T WithDatabase<T>(Func<DbConnection, T> operation)
    {
        if (!Available) throw new InvalidOperationException("Manager lease store is unavailable.");
        try
        {
            lock (_databaseGate)
            {
                using var db = _dbFactory!.CreateDbContext();
                using var connection = db.Database.GetDbConnection();
                connection.Open();
                EnsureSchema(connection);
                return operation(connection);
            }
        }
        catch
        {
            Interlocked.Exchange(ref _available, 0);
            throw;
        }
    }

    private void EnsureSchema(DbConnection connection)
    {
        if (_schemaReady) return;
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS ManagerDeploymentEpochs (Id TEXT NOT NULL PRIMARY KEY, Epoch TEXT NOT NULL); CREATE TABLE IF NOT EXISTS ManagerExecutionLeases (CredentialHash TEXT NOT NULL PRIMARY KEY, LeaseId TEXT NOT NULL, Kind TEXT NOT NULL, ExecutionId TEXT NOT NULL, WorkspaceId TEXT NOT NULL, ConversationId TEXT NOT NULL, ThreadRootMessageId TEXT NOT NULL, TriggeringMessageId TEXT NOT NULL, ActorId TEXT NOT NULL, EnrollmentId TEXT NOT NULL, SessionId TEXT NOT NULL, DispatchRef TEXT NOT NULL, DeploymentEpoch TEXT NOT NULL, IssuedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL, CapabilitiesJson TEXT NOT NULL, Active INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS IX_ManagerExecutionLeases_ExecutionId ON ManagerExecutionLeases (ExecutionId);";
        command.ExecuteNonQuery();
        _schemaReady = true;
    }

    private static void AddLeaseParameters(DbCommand command, ManagerExecutionLeaseMetadata lease)
    {
        Add(command, "@hash", lease.CredentialHash);
        Add(command, "@lease", lease.LeaseId);
        Add(command, "@kind", lease.Kind.ToString());
        Add(command, "@execution", lease.ExecutionId);
        Add(command, "@workspace", lease.Origin.WorkspaceId);
        Add(command, "@conversation", lease.Origin.ConversationId);
        Add(command, "@thread", lease.Origin.ThreadRootMessageId);
        Add(command, "@trigger", lease.Origin.TriggeringMessageId);
        Add(command, "@actor", lease.Origin.ActorId);
        Add(command, "@enrollment", lease.Origin.EnrollmentId);
        Add(command, "@session", lease.Origin.SessionId);
        Add(command, "@dispatch", lease.Origin.DispatchRef);
        Add(command, "@epoch", lease.DeploymentEpoch);
        Add(command, "@issued", lease.IssuedAt.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@expires", lease.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@capabilities", JsonSerializer.Serialize(lease.Capabilities));
        Add(command, "@active", lease.Active ? 1 : 0);
    }

    private static ManagerExecutionLeaseMetadata ReadLease(DbDataReader reader)
    {
        var kind = Enum.Parse<ManagerExecutionLeaseKind>(reader.GetString(2), ignoreCase: true);
        var capabilities = JsonSerializer.Deserialize<HashSet<string>>(reader.GetString(15)) ?? new(StringComparer.Ordinal);
        return new(
            reader.GetString(0),
            reader.GetString(1),
            kind,
            reader.GetString(3),
            new ManagerExecutionOrigin(
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)),
            reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            capabilities,
            reader.GetInt32(16) != 0);
    }

    private static int Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static int ScalarInt(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private void EnsureAvailable()
    {
        if (!Available) throw new InvalidOperationException("Manager lease store is unavailable.");
    }

    private static string NewEpoch() => $"mepoch_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
}

public sealed class ManagerExecutionCapabilityIssuer
{
    public const int CredentialBytes = 32;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly IManagerExecutionLeaseStore _store;
    private readonly IManagerDeploymentEpoch _epoch;
    private readonly TimeProvider _timeProvider;

    public ManagerExecutionCapabilityIssuer(
        IManagerExecutionLeaseStore store,
        IManagerDeploymentEpoch epoch,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _epoch = epoch;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ManagerExecutionGrant Issue(ManagerExecutionIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExecutionId))
            throw new ArgumentException("Execution identity is required.", nameof(request));
        ValidateOrigin(request.Origin);
        var deploymentEpoch = _epoch.Current;
        if (!_epoch.Available || string.IsNullOrWhiteSpace(deploymentEpoch) || !_store.Available)
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
            capabilities,
            deploymentEpoch);
        var replyLease = NewMetadata(
            ManagerExecutionLeaseKind.Reply,
            reply,
            request,
            issuedAt,
            expiresAt,
            new HashSet<string>(StringComparer.Ordinal) { "manager.reply" },
            deploymentEpoch);

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

        if (!string.Equals(_epoch.Current, deploymentEpoch, StringComparison.Ordinal))
        {
            _store.RevokeExecution(request.ExecutionId);
            throw new InvalidOperationException("Manager deployment epoch changed while issuing the execution grant.");
        }

        return new ManagerExecutionGrant(management, reply, request.ExecutionId, expiresAt, deploymentEpoch);
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
            now ?? _timeProvider.GetUtcNow(),
            lifetime ?? DefaultLifetime));
    }

    public ManagerExecutionValidationResult ValidatePresented(
        string? credential,
        ManagerExecutionLeaseKind kind,
        string capability,
        DateTimeOffset now)
    {
        var deploymentEpoch = _epoch.Current;
        if (!_epoch.Available || !_store.Available || string.IsNullOrWhiteSpace(deploymentEpoch))
            return ManagerExecutionValidationResult.Denied(
                "manager_authorization_unavailable",
                "Manager authorization is unavailable; inspect the current Manager status and retry explicitly.");
        if (string.IsNullOrWhiteSpace(credential) || credential.Length < 32)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is invalid or unavailable; request a fresh turn.");
        var lease = _store.FindIncludingRevoked(Hash(credential));
        if (lease is null || lease.Kind != kind)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is unknown, revoked, or already consumed; request a fresh turn.");
        if (!string.Equals(lease.DeploymentEpoch, deploymentEpoch, StringComparison.Ordinal))
            return ManagerExecutionValidationResult.Denied(
                "manager_epoch_changed",
                "The Manager Server restarted; request a fresh turn before retrying.");
        if (!lease.Active)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is unknown, revoked, or already consumed; request a fresh turn.");
        if (now >= lease.ExpiresAt)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_expired",
                "The Manager execution expired; inspect the current state and start a fresh turn.");
        var routeAllowed = string.Equals(capability, ManagerCapabilityCatalog.ManagerManagementRoute, StringComparison.Ordinal)
            ? kind == ManagerExecutionLeaseKind.Management
                && lease.Capabilities.Any(ManagerCapabilityCatalog.IsManagement)
            : lease.Capabilities.Contains(capability, StringComparer.Ordinal);
        if (!routeAllowed)
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
        var deploymentEpoch = _epoch.Current;
        if (!_epoch.Available || !_store.Available || string.IsNullOrWhiteSpace(deploymentEpoch))
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
        var lease = _store.FindIncludingRevoked(hash);
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
        if (!string.Equals(lease.DeploymentEpoch, deploymentEpoch, StringComparison.Ordinal))
            return ManagerExecutionValidationResult.Denied(
                "manager_epoch_changed",
                "The Manager Server restarted; request a fresh turn before retrying.");
        if (!lease.Active)
            return ManagerExecutionValidationResult.Denied(
                "manager_credential_invalid",
                "The Manager execution credential is unknown, revoked, or already consumed; request a fresh turn.");
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
        IReadOnlySet<string> capabilities,
        string deploymentEpoch) =>
        new(
            $"mlease_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
            Hash(credential),
            kind,
            request.ExecutionId,
            request.Origin,
            deploymentEpoch,
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
                $"manager:{work.AgentJobId}:{work.WorkId}",
                origin);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

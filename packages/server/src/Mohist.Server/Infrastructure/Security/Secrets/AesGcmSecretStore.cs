using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Secrets;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// AES-GCM-backed implementation of <see cref="ISecretStore"/>. Each
/// stored blob is laid out as
/// <c>nonce (12) || ciphertext || tag (16)</c> — the nonce is freshly
/// generated per write, and the master key is loaded once per process
/// from <see cref="PhysicalSecretKeyFile"/> and held in memory.
/// <see cref="AesGcm.Encrypt"/> and <see cref="AesGcm.Decrypt"/>
/// authenticate the tag implicitly; a key mismatch (wrong master key,
/// corrupted ciphertext) surfaces as <see cref="CryptographicException"/>
/// which the public methods translate to
/// <see cref="SecretStoreKeyException"/>. Plaintext never enters a log
/// message: the renderer below logs only <c>ProjectId</c>,
/// <c>ConnectionId</c>, <c>Kind</c>, and the byte count.
/// </summary>
public sealed class AesGcmSecretStore : ISecretStore, ISingletonService
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISecretKeyFile _keyFile;
    private readonly string _keyPath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AesGcmSecretStore> _logger;

    private readonly object _masterKeyGate = new();
    private byte[]? _masterKey;

    public AesGcmSecretStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretKeyFile keyFile,
        Microsoft.Extensions.Options.IOptions<SecretStoreOptions> options,
        IEnvironmentVariableProvider environment,
        TimeProvider timeProvider,
        ILogger<AesGcmSecretStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dbFactory = dbFactory;
        _keyFile = keyFile;
        var configured = options.Value.KeyPath;
        _keyPath = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : PhysicalSecretKeyFile.ResolvePath(environment);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
    {
        ValidateAddress(address);
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var blob = Encrypt(address, plaintext, nonce);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.StoredSecrets
            .FirstOrDefaultAsync(
                r => r.OwnerKind == address.OwnerKind
                    && r.OwnerScope == address.OwnerScope
                    && r.OwnerId == address.OwnerId
                    && r.Kind == StoredSecretRow.WireKind(address.Kind),
                ct)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        if (existing is null)
        {
            db.StoredSecrets.Add(new StoredSecretRow
            {
                OwnerKind = address.OwnerKind,
                OwnerScope = address.OwnerScope,
                OwnerId = address.OwnerId,
                Kind = StoredSecretRow.WireKind(address.Kind),
                Blob = blob,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Blob = blob;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default)
    {
        ValidateAddress(address);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.StoredSecrets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.OwnerKind == address.OwnerKind
                    && r.OwnerScope == address.OwnerScope
                    && r.OwnerId == address.OwnerId
                    && r.Kind == StoredSecretRow.WireKind(address.Kind),
                ct)
            .ConfigureAwait(false);
        if (row is null)
            return null;

        try
        {
            return Decrypt(address, row.Blob);
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreKeyException(
                "Stored secret could not be decrypted with the current master key; " +
                "the master key file may have been replaced since this secret was written.",
                ex);
        }
    }

    public async Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default)
    {
        ValidateAddress(address);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.StoredSecrets
            .Where(
                r => r.OwnerKind == address.OwnerKind
                    && r.OwnerScope == address.OwnerScope
                    && r.OwnerId == address.OwnerId
                    && r.Kind == StoredSecretRow.WireKind(address.Kind))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0)
            return false;

        db.StoredSecrets.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
            copy[key] = SecretNameDetector.IsSecretKey(key) ? RedactionMarker : value;
        return copy;
    }

    public const string RedactionMarker = "***";

    private byte[] Encrypt(SecretStoreAddress address, byte[] plaintext, byte[] nonce)
    {
        var key = GetMasterKey();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreException(
                "AES-GCM encryption failed.", ex);
        }

        var blob = new byte[NonceLength + ciphertext.Length + TagLength];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceLength);
        Buffer.BlockCopy(ciphertext, 0, blob, NonceLength, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceLength + ciphertext.Length, TagLength);
        return blob;
    }

    private byte[] Decrypt(SecretStoreAddress address, byte[] blob)
    {
        if (blob.Length < NonceLength + TagLength)
            throw new SecretStoreKeyException("Stored secret blob is too short to be valid.");

        var key = GetMasterKey();
        var nonce = new byte[NonceLength];
        var tag = new byte[TagLength];
        var ciphertextLength = blob.Length - NonceLength - TagLength;
        var ciphertext = new byte[ciphertextLength];

        Buffer.BlockCopy(blob, 0, nonce, 0, NonceLength);
        Buffer.BlockCopy(blob, NonceLength, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(blob, NonceLength + ciphertextLength, tag, 0, TagLength);

        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreKeyException(
                "Stored secret could not be decrypted with the current master key; " +
                "the master key file may have been replaced since this secret was written.",
                ex);
        }
    }

    private byte[] GetMasterKey()
    {
        if (_masterKey is not null)
            return _masterKey;

        lock (_masterKeyGate)
        {
            if (_masterKey is not null)
                return _masterKey;

            var key = LoadOrCreateKey();
            _masterKey = key;
            return key;
        }
    }

    private byte[] LoadOrCreateKey()
    {
        try
        {
            return _keyFile.EnsureKeyAsync(_keyPath).GetAwaiter().GetResult();
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void ValidateAddress(SecretStoreAddress address)
    {
        if (address.Owner is null)
            throw new ArgumentException("Secret owner is required.", nameof(address));
        _ = new SecretStoreAddress(address.Owner, address.Kind);
    }
}

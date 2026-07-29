using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// Single seam the rest of Server uses to read/write encrypted
/// connection-bound secrets. The first secret-storage primitive in the
/// repo; later features (Runner creds, webhook secrets) consume the same
/// shape. Implementations persist the ciphertext in the Mohist SQLite
/// database and decrypt/encrypt with a process-global master key loaded
/// from disk. Plaintext never appears in any log line, command argument,
/// transcript, or Agent configuration produced through this store —
/// callers MUST treat the returned byte array as in-process only.
/// </summary>
public interface ISecretStore
{
    Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default);

    Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default);

    Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default);

    /// <summary>
    /// Redacts a property-bag so any value whose key matches the
    /// secret-name predicate is replaced with <c>"***"</c>. Used by
    /// diagnostic / config-read surfaces to keep a token from leaking
    /// even when an upstream caller forwards an in-memory
    /// <see cref="SecretStoreAddress"/> map.
    /// </summary>
    IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values);
}

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message) : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SecretStoreAccessDeniedException(string message) : Exception(message);

public sealed class SecretStoreKeyException : Exception
{
    public SecretStoreKeyException(string message)
        : base(message)
    {
    }

    public SecretStoreKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

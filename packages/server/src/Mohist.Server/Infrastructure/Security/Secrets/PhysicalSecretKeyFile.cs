using System.Security.Cryptography;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Security.Secrets;

public sealed class PhysicalSecretKeyFile : ISecretKeyFile
{
    public const string PathEnvironmentVariable = "MOHIST_SECRET_KEY_PATH";
    public const string DefaultFileName = "slack-master.key";
    public const int KeyLengthBytes = 32;
    private const int ConcurrentCreateReadRetries = 8;

    private readonly ISecretKeyFileOperations _ops;
    private readonly IEnvironmentVariableProvider _environment;

    public PhysicalSecretKeyFile(
        ISecretKeyFileOperations operations,
        IEnvironmentVariableProvider environment)
    {
        _ops = operations;
        _environment = environment;
    }

    public bool Exists(string path) => _ops.FileExists(path);

    public async Task<byte[]> EnsureKeyAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_ops.FileExists(path))
            return await LoadInternalAsync(path, ct).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            _ops.CreateDirectory(directory);

        // Create-if-absent is exclusive so that concurrent hosts racing to
        // initialize the same key file converge on a single persisted key.
        // Without this, two hosts can each generate a fresh random key and
        // cache it in memory while the on-disk file ends up with only the
        // last writer's bytes, leaving siblings unable to decrypt secrets
        // the first host encrypted (SecretStoreKeyException on load).
        var key = RandomNumberGenerator.GetBytes(KeyLengthBytes);
        if (_ops.TryCreateExclusive(path, key, OwnerOnlyMode()))
            return key;

        // Another host created the key file between our existence check and
        // our create; adopt the persisted key so every host in the process
        // shares the same master key.
        return await LoadInternalAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> TryLoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!_ops.FileExists(path))
            return null;
        return await LoadInternalAsync(path, ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string path, byte[] key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyLengthBytes)
        {
            throw new ArgumentException(
                $"Master-key length must be exactly {KeyLengthBytes} bytes.",
                nameof(key));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            _ops.CreateDirectory(directory);

        await _ops.WriteAllBytesAtomicAsync(path, key, OwnerOnlyMode(), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            _ops.SetUnixFileMode(path, OwnerOnlyMode());
    }

    public static string ResolvePath(
        IEnvironmentVariableProvider environment,
        string? homeOverride = null)
    {
        var configured = environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var home = !string.IsNullOrWhiteSpace(homeOverride)
            ? homeOverride
            : environment.GetEnvironmentVariable(MohistServiceRegistration.HomeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(home, ".mohist", DefaultFileName);
    }

    private async Task<byte[]> LoadInternalAsync(string path, CancellationToken ct)
    {
        // Reading the freshly created key file can transiently hit "file in
        // use" while the host that just won the create-if-absent race is
        // still holding its no-share write handle. Back off briefly so the
        // winner can finish before we read, then re-run the full key-file
        // discipline checks (symlink, permissions, length) on the result.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await LoadInternalCoreAsync(path, ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt + 1 < ConcurrentCreateReadRetries)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> LoadInternalCoreAsync(string path, CancellationToken ct)
    {
        if (_ops.IsReparsePoint(path))
        {
            throw new SecretStoreAccessDeniedException(
                $"Mohist secret master key path '{path}' must not be a symbolic link.");
        }

        EnforcePermissionIfSupported(path);
        var bytes = await _ops.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length != KeyLengthBytes)
        {
            throw new SecretStoreKeyException(
                $"Mohist secret master key at '{path}' has length {bytes.Length}; " +
                $"expected exactly {KeyLengthBytes} bytes.");
        }
        return bytes;
    }

    private void EnforcePermissionIfSupported(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = _ops.GetUnixFileMode(path);
        const UnixFileMode forbidden = UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute;
        if ((mode & forbidden) != 0)
        {
            throw new SecretStoreAccessDeniedException(
                $"Mohist secret master key at '{path}' grants non-owner " +
                "permissions; the file must be readable and writable only by " +
                "the current user (mode 0600).");
        }
    }

    private static UnixFileMode OwnerOnlyMode() =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
}

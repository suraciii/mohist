using System.Security.Cryptography;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// Production implementation of <see cref="ISecretKeyFile"/> backed by
/// the real file system. Honours the master-key file discipline
/// documented at design D2 (issue #514 / T-001): the file is auto-created
/// at <c>~/.mohist/slack-master.key</c> or
/// <c>MOHIST_SECRET_KEY_PATH</c>, permissions are pinned to
/// <c>0600</c> on non-Windows, symlinks are refused, and a file that
/// grants "other" read/write/execute bits is rejected on load to
/// defend against a backup script that broadens permissions silently.
/// All file-system calls go through <see cref="ISecretKeyFileOperations"/>
/// so tests can drive the surface without touching the real file
/// system.
/// </summary>
public sealed class PhysicalSecretKeyFile : ISecretKeyFile
{
    public const string PathEnvironmentVariable = "MOHIST_SECRET_KEY_PATH";
    public const string DefaultFileName = "slack-master.key";
    public const int KeyLengthBytes = 32;

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

        var key = RandomNumberGenerator.GetBytes(KeyLengthBytes);
        await _ops.WriteAllBytesAtomicAsync(path, key, OwnerOnlyMode(), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            _ops.SetUnixFileMode(path, OwnerOnlyMode());
        return key;
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

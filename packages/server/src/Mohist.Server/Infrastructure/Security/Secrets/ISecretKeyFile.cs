namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// File-system surface the secret master-key needs from the host.
/// Mirrors the design constraint that the master-key file:
/// <list type="bullet">
/// <item>is created lazily on first read with mode <c>0600</c> on
/// non-Windows,</item>
/// <item>must be a regular file (symlinks are refused), and</item>
/// <item>must not grant any "other" permission bit on non-Windows.</item>
/// </list>
/// Implementations are split between <see cref="PhysicalSecretKeyFile"/>
/// for production and an in-memory fake used by tests so the test suite
/// never touches the real file system. The interface is intentionally
/// narrow — anything beyond load/save/permission inspection belongs to
/// <see cref="IFileSystem"/>.
/// </summary>
public interface ISecretKeyFile
{
    bool Exists(string path);

    /// <summary>
    /// Returns the 32 random bytes either loaded from
    /// <paramref name="path"/>, or freshly generated and persisted to
    /// <paramref name="path"/> when the file is missing. Implementations
    /// apply the file-mode + symlink discipline documented above; any
    /// violation raises <see cref="SecretStoreAccessDeniedException"/>
    /// or <see cref="SecretStoreException"/> rather than silently
    /// exposing the key.
    /// </summary>
    Task<byte[]> EnsureKeyAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Loads an existing 32-byte key from <paramref name="path"/>. The
    /// implementation refuses a symlink or a file with permissive
    /// "other" bits; missing files yield <c>null</c> so callers can
    /// surface an actionable error.
    /// </summary>
    Task<byte[]?> TryLoadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="key"/> to <paramref name="path"/>
    /// atomically, replacing any existing file. Used by tests and by
    /// the key-rotation flow that ships in a later issue (TBD).
    /// </summary>
    Task WriteAsync(string path, byte[] key, CancellationToken ct = default);
}

/// <summary>
/// Slice of <c>System.IO</c> the <see cref="PhysicalSecretKeyFile"/>
/// needs for its file-discipline checks. Wrapping each call lets tests
/// inject scripted fixtures (a symlinked path, a world-readable file)
/// without ever resolving to the banned
/// <c>System.IO.File</c>/<c>Directory</c>/<c>FileStream</c> types.
/// Production behaviour is provided by
/// <see cref="PhysicalSecretKeyFileOperations"/>.
/// </summary>
public interface ISecretKeyFileOperations
{
    bool FileExists(string path);

    bool IsReparsePoint(string path);

    UnixFileMode GetUnixFileMode(string path);

    void SetUnixFileMode(string path, UnixFileMode mode);

    void CreateDirectory(string path);

    /// <summary>
    /// Atomically creates <paramref name="path"/> with
    /// <paramref name="bytes"/> and <paramref name="ownerOnlyMode"/>,
    /// returning <c>true</c> only when this call created the file. If the
    /// file already exists — because the caller is racing another host that
    /// initialized the same master-key path first — the method leaves the
    /// existing file untouched and returns <c>false</c>. This is the
    /// create-if-absent primitive that lets concurrent hosts converge on a
    /// single persisted key instead of overwriting each other with freshly
    /// generated ones.
    /// </summary>
    bool TryCreateExclusive(
        string path,
        byte[] bytes,
        UnixFileMode ownerOnlyMode);

    Task WriteAllBytesAtomicAsync(
        string path,
        byte[] bytes,
        UnixFileMode ownerOnlyMode,
        CancellationToken ct = default);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default);
}

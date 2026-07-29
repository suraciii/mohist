namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// Default <see cref="ISecretKeyFileOperations"/> backed by
/// <c>System.IO</c>. The intended use is "production path"; tests must
/// not reach this code — they construct a fake
/// <see cref="ISecretKeyFileOperations"/> directly so the banned-symbols
/// rules that forbid <c>System.IO.File</c> in tests are honoured.
/// </summary>
public sealed class PhysicalSecretKeyFileOperations : ISecretKeyFileOperations
{
    public static PhysicalSecretKeyFileOperations Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public UnixFileMode GetUnixFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return File.GetUnixFileMode(path);
    }

    public void SetUnixFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, mode);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public async Task WriteAllBytesAtomicAsync(
        string path,
        byte[] bytes,
        UnixFileMode ownerOnlyMode,
        CancellationToken ct = default)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = ownerOnlyMode;
            }

            await using (var stream = new FileStream(tempPath, options))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
            throw;
        }
    }

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default) =>
        File.ReadAllBytesAsync(path, ct);
}

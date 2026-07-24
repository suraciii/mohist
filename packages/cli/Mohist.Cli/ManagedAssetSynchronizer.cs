namespace Mohist.Cli;

internal sealed record ManagedAssetKind(
    string Label,
    string DisplayName,
    string SourceDirectoryName,
    string SuccessMessageNoun,
    Func<IFileSystem, string, bool> PreparedValidator)
{
    public static readonly ManagedAssetKind Skill = new(
        "skill",
        "skill assets",
        "skill-data",
        "skill assets",
        (fs, dir) => fs.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories).Any());

    public static readonly ManagedAssetKind Preset = new(
        "preset",
        "preset assets",
        "presets",
        "preset assets",
        (fs, dir) => fs.Exists(Path.Combine(dir, "manifest.json")));
}

internal sealed class ManagedAssetSynchronizer
{
    private readonly IFileSystem _fileSystem;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public ManagedAssetSynchronizer(TextWriter output, TextWriter error, IFileSystem? fileSystem = null)
    {
        _out = output;
        _err = error;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
    }

    public async Task<int> SyncAsync(string sourceDir, string managedDir, ManagedAssetKind kind)
    {
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            _err.WriteLine($"Source {kind.SourceDirectoryName} directory is not configured. Aborting managed asset sync.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(managedDir))
        {
            _err.WriteLine($"Managed {kind.SourceDirectoryName} root is not configured. Aborting managed asset sync.");
            return 1;
        }

        if (!_fileSystem.DirectoryExists(sourceDir))
        {
            _err.WriteLine($"Source {kind.SourceDirectoryName} directory '{sourceDir}' is missing. Aborting managed asset sync.");
            return 1;
        }

        var parentDir = Path.GetDirectoryName(managedDir);
        if (!string.IsNullOrWhiteSpace(parentDir))
            _fileSystem.CreateDirectory(parentDir);

        var tempDir = Path.Combine(parentDir ?? string.Empty, $"{kind.SourceDirectoryName}.tmp-{Guid.NewGuid():N}");
        var tempDirCreated = false;
        try
        {
            _fileSystem.CreateDirectory(tempDir);
            tempDirCreated = true;

            await CopyDirectoryAsync(sourceDir, tempDir);

            if (!TryValidatePrepared(tempDir, kind, out var validationError))
            {
                _err.WriteLine($"Prepared {kind.SourceDirectoryName} at '{tempDir}' is invalid: {validationError}");
                return 1;
            }

            if (_fileSystem.DirectoryExists(managedDir))
                _fileSystem.DeleteDirectory(managedDir);

            _fileSystem.Move(tempDir, managedDir);
            tempDirCreated = false;

            _out.WriteLine($"Synchronized managed {kind.SuccessMessageNoun} to {managedDir}");
            return 0;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Managed {kind.SourceDirectoryName} sync failed: {ex.Message}");
            return 1;
        }
        finally
        {
            if (tempDirCreated && _fileSystem.DirectoryExists(tempDir))
            {
                try
                {
                    _fileSystem.DeleteDirectory(tempDir);
                }
                catch
                {
                }
            }
        }
    }

    private async Task CopyDirectoryAsync(string sourceDir, string destDir)
    {
        foreach (var sourceFile in _fileSystem.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
            var destFile = Path.Combine(destDir, relativePath);
            var destSubdir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrWhiteSpace(destSubdir))
                _fileSystem.CreateDirectory(destSubdir);

            await using var sourceStream = _fileSystem.OpenRead(sourceFile);
            await using var destStream = _fileSystem.OpenWrite(destFile);
            await sourceStream.CopyToAsync(destStream);
        }
    }

    private bool TryValidatePrepared(string preparedDir, ManagedAssetKind kind, out string? error)
    {
        error = null;

        try
        {
            if (!kind.PreparedValidator(_fileSystem, preparedDir))
            {
                error = kind.Label == ManagedAssetKind.Skill.Label
                    ? "no '*/SKILL.md' found"
                    : $"manifest.json not found";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

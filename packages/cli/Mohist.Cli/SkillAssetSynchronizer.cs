namespace Mohist.Cli;

internal sealed class SkillAssetSynchronizer
{
    private readonly IFileSystem _fileSystem;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public SkillAssetSynchronizer(TextWriter output, TextWriter error, IFileSystem? fileSystem = null)
    {
        _out = output;
        _err = error;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
    }

    public async Task<int> SyncAsync(string sourceDir, string managedDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            _err.WriteLine($"Source skill-data directory is not configured. Aborting managed asset sync.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(managedDir))
        {
            _err.WriteLine($"Managed skill-data root is not configured. Aborting managed asset sync.");
            return 1;
        }

        if (!_fileSystem.DirectoryExists(sourceDir))
        {
            _err.WriteLine($"Source skill-data directory '{sourceDir}' is missing. Aborting managed asset sync.");
            return 1;
        }

        var parentDir = Path.GetDirectoryName(managedDir);
        if (!string.IsNullOrWhiteSpace(parentDir))
            _fileSystem.CreateDirectory(parentDir);

        var tempDir = Path.Combine(parentDir ?? string.Empty, $"skill-data.tmp-{Guid.NewGuid():N}");
        var tempDirCreated = false;
        try
        {
            _fileSystem.CreateDirectory(tempDir);
            tempDirCreated = true;

            await CopyDirectoryAsync(sourceDir, tempDir);

            if (!TryValidatePreparedSkills(tempDir, out var validationError))
            {
                _err.WriteLine($"Prepared skill-data at '{tempDir}' is invalid: {validationError}");
                return 1;
            }

            if (_fileSystem.DirectoryExists(managedDir))
                _fileSystem.DeleteDirectory(managedDir);

            _fileSystem.Move(tempDir, managedDir);
            tempDirCreated = false;

            _out.WriteLine($"Synchronized managed skill assets to {managedDir}");
            return 0;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Managed skill-data sync failed: {ex.Message}");
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

    private bool TryValidatePreparedSkills(string preparedDir, out string? error)
    {
        error = null;

        try
        {
            var hasSkill = _fileSystem
                .EnumerateFiles(preparedDir, "SKILL.md", SearchOption.AllDirectories)
                .Any();

            if (!hasSkill)
            {
                error = "no '*/SKILL.md' found";
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

namespace Mohist.Server.SystemInfo;

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public long? GetFileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : null;

    public void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, contents);
            if (File.Exists(path))
                File.Replace(tempPath, path, destinationBackupFileName: null);
            else
                File.Move(tempPath, path);
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

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

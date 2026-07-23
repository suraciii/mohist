namespace Mohist.Server.SystemInfo;

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public long? GetFileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : null;
}

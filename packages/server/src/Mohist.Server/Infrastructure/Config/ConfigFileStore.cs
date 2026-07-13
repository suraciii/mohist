namespace Mohist.Server.Infrastructure.Config;

internal interface IConfigFileStore
{
    bool Exists(string path);
    string ReadAllText(string path);
    Task<string> ReadAllTextAsync(string path);
    void EnsureParentDirectory(string path);
    Task WriteAllTextAsync(string path, string contents);
}

internal sealed class PhysicalConfigFileStore : IConfigFileStore
{
    public static readonly PhysicalConfigFileStore Instance = new();

    private PhysicalConfigFileStore()
    {
    }

    public bool Exists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public Task<string> ReadAllTextAsync(string path) => File.ReadAllTextAsync(path);

    public void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public Task WriteAllTextAsync(string path, string contents) => File.WriteAllTextAsync(path, contents);
}

using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Physical file-backed credential store: creates the credential file
/// (0600, no symlink, write-through) when the default path is missing,
/// and refuses to read through symbolic links. The token is a bare
/// base64url 32-byte value with no prefix.
/// </summary>
internal sealed class PhysicalFileCredentialStore : IFileCredentialStore
{
    public static PhysicalFileCredentialStore Instance { get; } = new();

    private PhysicalFileCredentialStore()
    {
    }

    public string LoadOrCreateDefault(string path)
    {
        if (File.Exists(path))
            return ReadAndSecure(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode =
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false));
            writer.Write(token);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return token;
        }
        catch (IOException) when (File.Exists(path))
        {
            return ReadAndSecure(path);
        }
    }

    public string ReadExplicit(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
            when (ex is IOException or
                UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Mohist credential could not be read from '{path}': {ex.Message}",
                ex);
        }
    }

    private static string ReadAndSecure(string path)
    {
        if ((File.GetAttributes(path) &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Mohist credential path '{path}' " +
                "must not be a symbolic link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }
}

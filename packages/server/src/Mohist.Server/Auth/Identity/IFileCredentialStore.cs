namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Backing store for file-backed credentials (admin-token and
/// operator-token). Production uses the physical store; tests inject an
/// in-memory fake so bootstrap never touches a real filesystem.
/// </summary>
public interface IFileCredentialStore
{
    string LoadOrCreateDefault(string path);

    string ReadExplicit(string path);
}

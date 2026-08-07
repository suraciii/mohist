using Mohist.Server.Auth.Identity;

namespace Mohist.Server.TestSupport;

/// <summary>
/// In-memory <see cref="IFileCredentialStore"/> so spec hosts never touch
/// a real credential file (design/testing.md hard-constraint 1). Loaded
/// tokens come from configuration; this store only backstops hosts whose
/// configuration omits a token.
/// </summary>
public sealed class InMemoryFileCredentialStore : IFileCredentialStore
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public int CreateCount { get; private set; }

    public void Set(string path, string token) => _tokens[path] = token;

    public string LoadOrCreateDefault(string path)
    {
        if (_tokens.TryGetValue(path, out var token))
            return token;

        CreateCount++;
        token = $"generated-test-{CreateCount}-token-0123456789abcdef";
        _tokens[path] = token;
        return token;
    }

    public string ReadExplicit(string path)
    {
        if (!_tokens.TryGetValue(path, out var token))
            throw new InvalidOperationException(
                $"Mohist credential could not be read from '{path}': missing.");
        return token;
    }
}

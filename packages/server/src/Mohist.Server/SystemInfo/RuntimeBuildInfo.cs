using System.Reflection;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public interface IRuntimeBuildInfo
{
    string? Version { get; }
    string? GitHash { get; }
    DateTimeOffset StartedAt { get; }
}

public sealed class RuntimeBuildInfo : IRuntimeBuildInfo, ISingletonService
{
    public const string GitHashEnvironmentVariable = "MOHIST_GIT_HASH";

    public string? Version { get; }
    public string? GitHash { get; }
    public DateTimeOffset StartedAt { get; }

    public RuntimeBuildInfo()
        : this(SystemEnvironmentVariableProvider.Instance)
    {
    }

    public RuntimeBuildInfo(IEnvironmentVariableProvider environment)
    {
        StartedAt = DateTimeOffset.UtcNow;
        (Version, GitHash) = ResolveIdentity(environment);
    }

    private static (string? Version, string? GitHash) ResolveIdentity(IEnvironmentVariableProvider environment)
    {
        var assembly = typeof(RuntimeBuildInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var versionFromAssembly = assembly.GetName().Version?.ToString();

        return ResolveIdentity(
            informationalVersion,
            versionFromAssembly,
            () => environment.GetEnvironmentVariable(GitHashEnvironmentVariable),
            TryReadGitHeadFromSource);
    }

    internal static (string? Version, string? GitHash) ResolveIdentity(
        string? informationalVersion,
        string? versionFromAssembly,
        Func<string?> getGitHashFromEnv,
        Func<string?> getGitHead)
    {
        string? version = versionFromAssembly;
        string? gitHash = null;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            if (plusIndex >= 0)
            {
                version = informationalVersion[..plusIndex];
                gitHash = informationalVersion[(plusIndex + 1)..];
            }
            else
            {
                version = informationalVersion;
            }
        }

        if (string.IsNullOrWhiteSpace(gitHash))
            gitHash = getGitHashFromEnv();

        if (string.IsNullOrWhiteSpace(gitHash))
            gitHash = getGitHead();

        return (version, string.IsNullOrWhiteSpace(gitHash) ? null : gitHash);
    }

    private static string? TryReadGitHeadFromSource()
    {
        try
        {
            var assemblyLocation = typeof(RuntimeBuildInfo).Assembly.Location;
            var assemblyDir = Path.GetDirectoryName(assemblyLocation);
            if (string.IsNullOrWhiteSpace(assemblyDir))
                return null;

            var root = assemblyDir;
            while (root != null && !Directory.Exists(Path.Combine(root, ".git")))
            {
                root = Directory.GetParent(root)?.FullName;
            }

            if (root == null)
                return null;

            return TryReadGitHeadFile(root);
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryReadGitHeadFile(string repoRoot)
    {
        try
        {
            var headFile = Path.Combine(repoRoot, ".git", "HEAD");
            if (!File.Exists(headFile))
                return null;

            var head = File.ReadAllText(headFile).Trim();
            if (head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                var refPath = head[5..];
                var refFile = Path.Combine(repoRoot, ".git", refPath);
                if (File.Exists(refFile))
                    return File.ReadAllText(refFile).Trim();
                return null;
            }

            return head;
        }
        catch
        {
            return null;
        }
    }
}

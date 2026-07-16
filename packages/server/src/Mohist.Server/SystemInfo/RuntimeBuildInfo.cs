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

    public RuntimeBuildInfo(
        IEnvironmentVariableProvider environment,
        IRuntimeSourceIdentity sourceIdentity,
        TimeProvider timeProvider)
    {
        StartedAt = timeProvider.GetUtcNow();
        (Version, GitHash) = ResolveIdentity(environment, sourceIdentity);
    }

    private static (string? Version, string? GitHash) ResolveIdentity(
        IEnvironmentVariableProvider environment,
        IRuntimeSourceIdentity sourceIdentity)
    {
        var assembly = typeof(RuntimeBuildInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var versionFromAssembly = assembly.GetName().Version?.ToString();

        return ResolveIdentity(
            informationalVersion,
            versionFromAssembly,
            () => environment.GetEnvironmentVariable(
                GitHashEnvironmentVariable),
            () => sourceIdentity.GitHead);
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

}

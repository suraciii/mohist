using Mohist.Server.SystemInfo;

namespace Mohist.Server.TestSupport;

internal sealed class EmptyFileSystem : IFileSystem
{
    public bool Exists(string path) => false;

    public string ReadAllText(string path) => throw new FileNotFoundException(path);
}

internal sealed class FixedRuntimeBuildInfo(DateTimeOffset startedAt) : IRuntimeBuildInfo
{
    public string? Version => "test";
    public string? GitHash => "test";
    public DateTimeOffset StartedAt => startedAt;
}

internal sealed class FixedProcessStartTimeProvider(DateTimeOffset startedAt) : IProcessStartTimeProvider
{
    public DateTimeOffset GetStartTime() => startedAt;
}

internal sealed class NoopServiceStatusChecker : IServiceStatusChecker
{
    public Task<string?> GetStatusAsync(string? unitName) => Task.FromResult<string?>(null);
}

internal sealed class AvailableManagedAssetInspector : IManagedAssetInspector
{
    public bool HasSkill(string assetRoot) => true;
}

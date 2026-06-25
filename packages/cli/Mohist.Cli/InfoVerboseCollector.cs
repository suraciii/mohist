using System.Runtime.InteropServices;

namespace Mohist.Cli;

internal sealed class InfoVerboseCollector
{
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromSeconds(2);

    private readonly ICommandExecutor _commandExecutor;
    private readonly VerboseSkillInspector _skills;
    private readonly VerboseGitInspector _git;
    private readonly VerboseRuntimeInspector _runtime;
    private readonly VerboseRunnerInspector _runner;
    private readonly VerboseDiskInspector _disk;

    public InfoVerboseCollector(
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        IEnvironmentVariableProvider environment,
        MohistCliApi api,
        SkillAssetService? skillAssetService = null)
    {
        _commandExecutor = commandExecutor;
        _skills = new VerboseSkillInspector(skillAssetService);
        _git = new VerboseGitInspector(fileSystem, commandExecutor);
        _runtime = new VerboseRuntimeInspector(commandExecutor, environment, api);
        _runner = new VerboseRunnerInspector(commandExecutor, environment, api);
        _disk = new VerboseDiskInspector(fileSystem, commandExecutor);
    }

    internal async Task<InfoVerbose> CollectVerboseAsync(
        InfoService server,
        InfoService runner,
        InfoProject? project,
        InfoDataDir dataDir,
        bool systemdAvailable)
    {
        var sourcePath = runner.Source?.Path ?? server.Source?.Path;
        var isGitRepo = (sourcePath is not null) && (server.Source?.CommitShort is not null || runner.Source?.CommitShort is not null);

        using var sharedCts = new CancellationTokenSource(CollectorTimeout);
        var unitEnvTask = InfoCollector.SafeAsync(() => TryGetRunnerUnitEnvironmentAsync(systemdAvailable, sharedCts.Token));

        var skillsTask = InfoCollector.SafeAsync(() => _skills.GetSkillsVerboseAsync());
        var gitRemoteTask = InfoCollector.SafeAsync(() => _git.GetGitRemoteVerboseAsync(sourcePath));
        var opencodeTask = InfoCollector.SafeAsync(() => _runtime.GetOpencodeRuntimeVerboseAsync());
        var osRuntimeTask = InfoCollector.SafeAsync(() => _runtime.GetOsRuntimeVerboseAsync());
        var diskTask = InfoCollector.SafeAsync(() => _disk.GetDiskUsageVerboseAsync(dataDir));

        await Task.WhenAll(skillsTask, gitRemoteTask, opencodeTask, osRuntimeTask, diskTask, unitEnvTask);
        var unitEnv = await unitEnvTask;

        var envVarsTask = InfoCollector.SafeAsync(() => _runner.GetEnvVarsVerboseAsync(runner, systemdAvailable, unitEnv));
        var capacityTask = InfoCollector.SafeAsync(() => _runner.GetCapacityVerboseAsync(runner, project, systemdAvailable, unitEnv));
        await Task.WhenAll(envVarsTask, capacityTask);

        return new InfoVerbose(
            Skills: await skillsTask,
            GitRemote: await gitRemoteTask,
            OpencodeRuntime: await opencodeTask,
            EnvVars: await envVarsTask,
            OsRuntime: await osRuntimeTask,
            Capacity: await capacityTask,
            DiskUsage: await diskTask);
    }

    internal async Task<IReadOnlyDictionary<string, string>?> TryGetRunnerUnitEnvironmentAsync(bool systemdAvailable, CancellationToken ct)
    {
        return await _runner.TryGetRunnerUnitEnvironmentAsync(systemdAvailable, ct);
    }

    internal async Task<InfoVerboseSkills> GetSkillsVerboseAsync()
    {
        return await _skills.GetSkillsVerboseAsync();
    }

    internal async Task<InfoVerboseGitRemote> GetGitRemoteVerboseAsync(string? sourcePath)
    {
        return await _git.GetGitRemoteVerboseAsync(sourcePath);
    }

    internal async Task<InfoVerboseOpencodeRuntime> GetOpencodeRuntimeVerboseAsync()
    {
        return await _runtime.GetOpencodeRuntimeVerboseAsync();
    }

    internal async Task<IReadOnlyList<InfoVerboseEnvVar>> GetEnvVarsVerboseAsync(InfoService runner, bool systemdAvailable, IReadOnlyDictionary<string, string>? unitEnv = null)
    {
        return await _runner.GetEnvVarsVerboseAsync(runner, systemdAvailable, unitEnv);
    }

    internal async Task<InfoVerboseOsRuntime> GetOsRuntimeVerboseAsync()
    {
        return await _runtime.GetOsRuntimeVerboseAsync();
    }

    internal async Task<InfoVerboseCapacity> GetCapacityVerboseAsync(
        InfoService runner,
        InfoProject? project,
        bool systemdAvailable,
        IReadOnlyDictionary<string, string>? unitEnv = null)
    {
        return await _runner.GetCapacityVerboseAsync(runner, project, systemdAvailable, unitEnv);
    }

    internal async Task<InfoVerboseDiskUsage> GetDiskUsageVerboseAsync(InfoDataDir dataDir)
    {
        return await _disk.GetDiskUsageVerboseAsync(dataDir);
    }
}

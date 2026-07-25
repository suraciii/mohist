using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public interface IFileSystem
{
    bool Exists(string path);
    string ReadAllText(string path);
    void CreateDirectory(string path);
    long? GetFileLength(string path);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>
    /// atomically. The implementation MUST ensure that a concurrent
    /// reader either observes the previous contents or the new contents
    /// in full — never a partial write. The default contract used here
    /// is the canonical "write to a sibling temp file, then rename".
    /// </summary>
    void WriteAllText(string path, string contents);

    /// <summary>
    /// Deletes <paramref name="path"/> if it exists. Missing files are
    /// not an error.
    /// </summary>
    void Delete(string path);
}

public sealed record InstallDetectionResult(
    string Mode,
    string? ServiceManager,
    string? ServerUnit,
    string? RunnerUnit,
    string? SourcePath,
    string? Reason);

public sealed class SystemdInstallDetector : ISingletonService
{
    private const string ServerUnitName = "mohist.service";
    private const string RunnerUnitName = "mohist-runner.service";
    private const string SolutionFile = "Mohist.sln";
    private const string ProjectFile = "Mohist.Server.csproj";

    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly string? _unitDir;

    public SystemdInstallDetector(IFileSystem fileSystem, IEnvironmentVariableProvider environment, string? unitDir = null)
    {
        _fileSystem = fileSystem;
        _environment = environment;
        _unitDir = unitDir;
    }

    public InstallDetectionResult Detect()
    {
        var unitDir = ResolveUnitDir();
        if (unitDir is null)
            return Unsupported("systemd user unit directory not found");

        var serverUnitPath = Path.Combine(unitDir, ServerUnitName);
        if (!_fileSystem.Exists(serverUnitPath))
            return Unsupported("mohist.service unit not found");

        SystemdUnitParseResult unit;
        try
        {
            unit = SystemdUnitParser.Parse(_fileSystem.ReadAllText(serverUnitPath));
        }
        catch
        {
            return Unsupported("mohist.service unit could not be parsed");
        }

        if (string.IsNullOrWhiteSpace(unit.WorkingDirectory))
            return Unsupported("mohist.service has no WorkingDirectory");

        if (string.IsNullOrWhiteSpace(unit.ExecStart))
            return Unsupported("mohist.service has no ExecStart");

        var workingDir = unit.WorkingDirectory.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var solutionPath = Path.Combine(workingDir, SolutionFile);
        if (!IsSourceRunShape(unit.ExecStart))
        {
            if (LooksLikeDotnetRun(unit.ExecStart))
                return Unsupported("ExecStart is not a local-source run shape");

            return Binary("ExecStart is not a local-source run shape", unit.WorkingDirectory);
        }

        if (!_fileSystem.Exists(solutionPath))
            return Unsupported("WorkingDirectory does not contain Mohist.sln");

        var runnerUnitPath = Path.Combine(unitDir, RunnerUnitName);
        var hasRunnerUnit = _fileSystem.Exists(runnerUnitPath);

        return new InstallDetectionResult(
            Mode: "local-source",
            ServiceManager: "systemd-user",
            ServerUnit: ServerUnitName,
            RunnerUnit: hasRunnerUnit ? RunnerUnitName : null,
            SourcePath: workingDir,
            Reason: "Detected local-source systemd user install from mohist.service");
    }

    private static bool IsSourceRunShape(string execStart)
    {
        if (!execStart.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!execStart.Contains("run", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!execStart.Contains("--project", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!execStart.Contains(ProjectFile, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool LooksLikeDotnetRun(string execStart)
    {
        return execStart.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
            && execStart.Contains("run", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveUnitDir()
    {
        if (!string.IsNullOrWhiteSpace(_unitDir))
            return _unitDir;

        var home = _environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;

        return Path.Combine(home, ".config", "systemd", "user");
    }

    private static InstallDetectionResult Unsupported(string reason)
    {
        return new InstallDetectionResult(
            Mode: "unknown",
            ServiceManager: "systemd-user",
            ServerUnit: null,
            RunnerUnit: null,
            SourcePath: null,
            Reason: reason);
    }

    private static InstallDetectionResult Binary(string reason, string? sourcePath)
    {
        return new InstallDetectionResult(
            Mode: "binary",
            ServiceManager: "systemd-user",
            ServerUnit: ServerUnitName,
            RunnerUnit: null,
            SourcePath: sourcePath,
            Reason: reason);
    }
}

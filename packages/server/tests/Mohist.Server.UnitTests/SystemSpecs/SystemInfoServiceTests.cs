using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Logging;
using Mohist.Server.SystemInfo;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemInfoServiceTests
{
    [Fact]
    public async Task GetSystemInfo_ReturnsAllSections()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "def456", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.NotNull(info.Running);
        Assert.NotNull(info.Source);
        Assert.NotNull(info.Install);
        Assert.NotNull(info.Update);
        Assert.NotNull(info.Services);
        Assert.NotNull(info.Paths);
        Assert.Equal("Detected local-source systemd user install from mohist.service", info.Install.Reason);
        Assert.NotEqual(default, info.Running.StartedAt);
    }

    [Fact]
    public async Task GetSystemInfo_LocalSourceCleanNewerSource_ReturnsUpdateAvailable()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "def456", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("local-source", info.Install.Mode);
        Assert.Equal("update-available", info.Update.Status);
        Assert.True(info.Update.Available);
        Assert.Equal("A newer source version is available", info.Update.Reason);
    }

    [Fact]
    public async Task GetSystemInfo_LocalSourceCleanNewerSourceDisabled_ReturnsUnsupported()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "def456", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SystemUpdate:Enabled"] = "false"
            })
            .Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("unsupported", info.Update.Status);
        Assert.False(info.Update.Available);
        Assert.Equal("System update is disabled by configuration", info.Update.Reason);
    }

    [Fact]
    public async Task GetSystemInfo_DirtySource_ReturnsDirtySource()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "def456", dirty: true);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.True(info.Source.Dirty);
        Assert.Equal("dirty-source", info.Update.Status);
        Assert.False(info.Update.Available);
        Assert.Equal("Source tree has uncommitted changes", info.Update.Reason);
    }

    [Fact]
    public async Task GetSystemInfo_UpToDate_ReturnsUpToDate()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "abc123", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("up-to-date", info.Update.Status);
        Assert.False(info.Update.Available);
        Assert.Equal("Running server is up to date with source", info.Update.Reason);
    }

    [Fact]
    public async Task GetSystemInfo_UnsupportedInstall_ReturnsUnsupported()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var detector = new SystemdInstallDetector(fs, "/units");
        var git = new FakeGitSourceInspector("/repo", "main", "def456", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("unknown", info.Install.Mode);
        Assert.False(string.IsNullOrWhiteSpace(info.Install.Reason));
        Assert.Equal("unsupported", info.Update.Status);
        Assert.False(info.Update.Available);
        Assert.Equal("Web update is unsupported for the detected deployment", info.Update.Reason);
    }

    [Fact]
    public async Task GetSystemInfo_MissingRunningGitHash_ReturnsUnknown()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", null);
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "def456", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("unknown", info.Update.Status);
        Assert.False(info.Update.Available);
        Assert.Equal("Cannot determine update status: running git hash is unavailable", info.Update.Reason);
    }

    [Fact]
    public async Task GetSystemInfo_MissingSourceHead_ReturnsUnknown()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", null, dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("unknown", info.Update.Status);
        Assert.False(info.Update.Available);
        Assert.Equal("Cannot determine update status: source HEAD is unavailable", info.Update.Reason);
    }

    [Fact]
    public async Task GetSystemInfo_LocalSource_ReturnsServiceStatuses()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");
        fs.Write(Path.Combine(unitDir, "mohist-runner.service"), "[Service]\nExecStart=node runner\n");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "abc123", dirty: false);
        var services = new FakeServiceStatusChecker();
        services.SetStatus("mohist.service", "active");
        services.SetStatus("mohist-runner.service", "inactive");
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("active", info.Services.Server);
        Assert.Equal("inactive", info.Services.Runner);
    }

    [Fact]
    public async Task GetSystemInfo_ReturnsPaths()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var detector = new SystemdInstallDetector(fs, "/units");
        var git = new FakeGitSourceInspector("/repo", "main", "def456", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder().Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.NotNull(info.Paths.Db);
        Assert.NotNull(info.Paths.Config);
        Assert.NotNull(info.Paths.Logs);
        Assert.NotNull(info.Paths.Opencode);
    }

    [Fact]
    public async Task GetSystemInfo_ExplicitlyEnabled_ReturnsUpdateAvailable()
    {
        var runtime = new FakeRuntimeBuildInfo("1.0.0", "abc123");
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var git = new FakeGitSourceInspector(repoDir, "main", "def456", dirty: false);
        var services = new FakeServiceStatusChecker();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SystemUpdate:Enabled"] = "true"
            })
            .Build();

        var svc = new SystemInfoService(
            runtime, detector, git, services, config, new MockEnvironmentVariableProvider(), new LogPathResolver(config, new MockEnvironmentVariableProvider()), NullLogger<SystemInfoService>.Instance);

        var info = await svc.GetSystemInfoAsync();

        Assert.Equal("update-available", info.Update.Status);
        Assert.True(info.Update.Available);
    }

    private sealed class FakeRuntimeBuildInfo : IRuntimeBuildInfo
    {
        public string? Version { get; }
        public string? GitHash { get; }
        public DateTimeOffset StartedAt { get; }

        public FakeRuntimeBuildInfo(string? version, string? gitHash)
        {
            Version = version;
            GitHash = gitHash;
            StartedAt = TestTime.UtcNow;
        }
    }

    private sealed class FakeGitSourceInspector : IGitSourceInspector
    {
        private readonly string _path;
        private readonly string? _branch;
        private readonly string? _head;
        private readonly bool _dirty;

        public FakeGitSourceInspector(string path, string? branch, string? head, bool dirty)
        {
            _path = path;
            _branch = branch;
            _head = head;
            _dirty = dirty;
        }

        public Task<SourceState> InspectAsync(string repoPath)
        {
            return Task.FromResult(new SourceState(repoPath, _branch, _head, _dirty));
        }
    }

    private sealed class FakeServiceStatusChecker : IServiceStatusChecker
    {
        private readonly Dictionary<string, string?> _statuses = new(StringComparer.Ordinal);

        public void SetStatus(string unitName, string? status)
        {
            _statuses[unitName] = status;
        }

        public Task<string?> GetStatusAsync(string? unitName)
        {
            if (unitName is null)
                return Task.FromResult<string?>(null);
            _statuses.TryGetValue(unitName, out var status);
            return Task.FromResult(status);
        }
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Write(string path, string contents)
        {
            _files[Path.GetFullPath(path, "/")] = contents;
        }

        public bool Exists(string path) => _files.ContainsKey(Path.GetFullPath(path, "/"));

        public string ReadAllText(string path) => _files[Path.GetFullPath(path, "/")];
    }
}

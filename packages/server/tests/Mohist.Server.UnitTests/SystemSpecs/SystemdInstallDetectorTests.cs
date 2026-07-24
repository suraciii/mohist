using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemdUnitParserTests
{
    [Fact]
    public void Parse_ExtractsWorkingDirectoryExecStartAndDescription()
    {
        var content = @"[Unit]
Description=Mohist Server
After=network.target

[Service]
Type=simple
WorkingDirectory=/home/surac/repos/mohist
ExecStart=dotnet run --project /home/surac/repos/mohist/packages/server/src/Mohist.Server/Mohist.Server.csproj -- --urls http://127.0.0.1:3456
Restart=on-failure

[Install]
WantedBy=default.target
";

        var result = SystemdUnitParser.Parse(content);

        Assert.Equal("/home/surac/repos/mohist", result.WorkingDirectory);
        Assert.Equal("dotnet run --project /home/surac/repos/mohist/packages/server/src/Mohist.Server/Mohist.Server.csproj -- --urls http://127.0.0.1:3456", result.ExecStart);
        Assert.Equal("Mohist Server", result.Description);
    }

    [Fact]
    public void Parse_WhenKeysMissing_ReturnsNulls()
    {
        var content = @"[Unit]
Description=Minimal
";

        var result = SystemdUnitParser.Parse(content);

        Assert.Null(result.WorkingDirectory);
        Assert.Null(result.ExecStart);
        Assert.Equal("Minimal", result.Description);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndEmptyLines()
    {
        var content = @"[Unit]
# This is a comment
Description=Mohist Server

[Service]
WorkingDirectory=/repo
ExecStart=dotnet run --project Mohist.Server.csproj
";

        var result = SystemdUnitParser.Parse(content);

        Assert.Equal("/repo", result.WorkingDirectory);
        Assert.Equal("dotnet run --project Mohist.Server.csproj", result.ExecStart);
    }

    [Fact]
    public void Parse_HandlesTrailingWhitespace()
    {
        var content = "[Service]\nWorkingDirectory=/repo  \nExecStart=dotnet run  \n";

        var result = SystemdUnitParser.Parse(content);

        Assert.Equal("/repo", result.WorkingDirectory);
        Assert.Equal("dotnet run", result.ExecStart);
    }
}

public class SystemdInstallDetectorTests
{
    [Fact]
    public void Detect_LocalSourceUnitWithSolutionAndSourceRun_ReturnsLocalSource()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/packages/server/src/Mohist.Server/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("local-source", result.Mode);
        Assert.Equal("systemd-user", result.ServiceManager);
        Assert.Equal("mohist.service", result.ServerUnit);
        Assert.Equal(repoDir.Replace('/', Path.DirectorySeparatorChar), result.SourcePath);
        Assert.Equal("Detected local-source systemd user install from mohist.service", result.Reason);
    }

    [Fact]
    public void Detect_LocalSourceWithRunnerUnit_ReturnsLocalSourceAndRunnerUnit()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project {repoDir}/packages/server/src/Mohist.Server/Mohist.Server.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");
        fs.Write(Path.Combine(unitDir, "mohist-runner.service"), "[Service]\nExecStart=node packages/runner/dist/cli.js\n");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("local-source", result.Mode);
        Assert.Equal("mohist-runner.service", result.RunnerUnit);
    }

    [Fact]
    public void Detect_MissingUnit_ReturnsUnknown()
    {
        var fs = new FakeFileSystem();
        var detector = new SystemdInstallDetector(fs, "/units");
        var result = detector.Detect();

        Assert.Equal("unknown", result.Mode);
        Assert.Contains("mohist.service unit not found", result.Reason);
    }

    [Fact]
    public void Detect_MissingWorkingDirectory_ReturnsUnknown()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            "[Service]\nExecStart=dotnet run --project Mohist.Server.csproj\n");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("unknown", result.Mode);
        Assert.Contains("no WorkingDirectory", result.Reason);
    }

    [Fact]
    public void Detect_MissingSolutionFile_ReturnsUnknown()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project Mohist.Server.csproj\n");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("unknown", result.Mode);
        Assert.Contains("does not contain Mohist.sln", result.Reason);
    }

    [Fact]
    public void Detect_BinaryExecStart_ReturnsBinary()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=/usr/bin/mohist-server\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("binary", result.Mode);
        Assert.Equal(repoDir, result.SourcePath);
        Assert.Contains("not a local-source run shape", result.Reason);
    }

    [Fact]
    public void Detect_DotnetRunWithoutProjectFlag_ReturnsUnknown()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("unknown", result.Mode);
        Assert.Contains("not a local-source run shape", result.Reason);
    }

    [Fact]
    public void Detect_DotnetRunWithWrongProject_ReturnsUnknown()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=dotnet run --project SomeOther.csproj\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("unknown", result.Mode);
        Assert.Contains("not a local-source run shape", result.Reason);
    }

    [Fact]
    public void Detect_MalformedUnitFile_ReturnsUnknown()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            "not a valid unit file at all just garbage");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("unknown", result.Mode);
    }

    [Fact]
    public void Detect_EmptyExecStart_ReturnsUnknown()
    {
        var fs = new FakeFileSystem();
        var unitDir = "/units";
        var repoDir = "/repo";

        fs.Write(
            Path.Combine(unitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory={repoDir}\nExecStart=\n");
        fs.Write(Path.Combine(repoDir, "Mohist.sln"), "");

        var detector = new SystemdInstallDetector(fs, unitDir);
        var result = detector.Detect();

        Assert.Equal("unknown", result.Mode);
        Assert.Contains("no ExecStart", result.Reason);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Write(string path, string contents)
        {
            _files[NormalizePath(path)] = contents;
        }

        public bool Exists(string path) => _files.ContainsKey(NormalizePath(path));

        public string ReadAllText(string path) => _files[NormalizePath(path)];

        public void CreateDirectory(string path) { }

        public long? GetFileLength(string path) => _files.TryGetValue(NormalizePath(path), out var content) ? (long?)System.Text.Encoding.UTF8.GetByteCount(content) : null;

        private static string NormalizePath(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }
    }
}

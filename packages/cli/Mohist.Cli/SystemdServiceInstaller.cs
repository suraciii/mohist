using System.Diagnostics;
using System.Text;

namespace Mohist.Cli;

internal sealed class SystemdServiceInstaller : IServiceInstaller
{
    private const string ServerUnit = "mohist.service";
    private const string RunnerUnit = "mohist-runner.service";

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IEnvironmentVariableProvider _environment;

    public SystemdServiceInstaller(
        TextWriter output,
        TextWriter error,
        IFileSystem? fileSystem = null,
        ICommandExecutor? commandExecutor = null,
        IEnvironmentVariableProvider? environment = null)
    {
        _out = output;
        _err = error;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
        _commandExecutor = commandExecutor ?? new SystemCommandExecutor();
        _environment = environment ?? SystemEnvironmentVariableProvider.Instance;
    }

    public async Task<int> InstallServerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var environment = BuildServiceEnvironment();
        // 默认不在 unit 里写死监听地址，让 server 读 ~/.mohist/config.jsonc；
        // 仅当用户显式传 --listen-url 时才追加 --urls。
        var serverArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ListenUrl))
        {
            serverArgs.Add("--urls");
            serverArgs.Add(options.ListenUrl);
        }
        var unit = new SystemdUnit(
            Name: ServerUnit,
            Description: "Mohist Server",
            WorkingDirectory: repoRoot,
            ExecStart: DotnetRun(ResolveExecutable("dotnet"), repoRoot, "packages/server/src/Mohist.Server/Mohist.Server.csproj", serverArgs),
            Environment: environment);

        return await InstallAsync(unit, options);
    }

    public async Task<int> InstallRunnerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var environment = BuildServiceEnvironment();
        environment["SERVER_URL"] = options.ServerUrl ?? "http://127.0.0.1:3456";
        if (!string.IsNullOrWhiteSpace(options.RunnerRoot))
            environment["RUNNER_ROOT"] = options.RunnerRoot;

        var unit = new SystemdUnit(
            Name: RunnerUnit,
            Description: "Mohist Runner",
            WorkingDirectory: repoRoot,
            ExecStart: $"{ResolveExecutable("node")} packages/runner/dist/cli.js",
            Environment: environment);

        return await InstallAsync(unit, options);
    }

    public Task<int> StartServerAsync(ServiceCommandOptions options) => StartAsync(ServerUnit, options);
    public Task<int> StopServerAsync(ServiceCommandOptions options) => StopAsync(ServerUnit, options);
    public Task<int> RestartServerAsync(ServiceCommandOptions options) => RestartAsync(ServerUnit, options);
    public Task<int> StatusServerAsync(ServiceCommandOptions options) => StatusAsync(ServerUnit, options);
    public Task<int> LogsServerAsync(ServiceCommandOptions options) => LogsAsync(ServerUnit, options);
    public Task<int> UninstallServerAsync(ServiceCommandOptions options) => UninstallAsync(ServerUnit, options);

    public Task<int> StartRunnerAsync(ServiceCommandOptions options) => StartAsync(RunnerUnit, options);
    public Task<int> StopRunnerAsync(ServiceCommandOptions options) => StopAsync(RunnerUnit, options);
    public Task<int> RestartRunnerAsync(ServiceCommandOptions options) => RestartAsync(RunnerUnit, options);
    public Task<int> StatusRunnerAsync(ServiceCommandOptions options) => StatusAsync(RunnerUnit, options);
    public Task<int> LogsRunnerAsync(ServiceCommandOptions options) => LogsAsync(RunnerUnit, options);
    public Task<int> UninstallRunnerAsync(ServiceCommandOptions options) => UninstallAsync(RunnerUnit, options);

    public async Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && _commandExecutor is SystemCommandExecutor) return false;
        var (code, stdout, _) = await _commandExecutor.ExecuteAsync(
            "systemctl",
            ["--user", "is-active", RunnerUnit]);
        var trimmed = stdout?.Trim() ?? string.Empty;
        return code == 0 && string.Equals(trimmed, "active", StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> IsRunnerInstalledAsync(string? unitDir = null) => Task.FromResult(IsRunnerUnitInstalled(unitDir));

    private bool IsRunnerUnitInstalled(string? unitDir)
    {
        var unitPath = Path.Combine(ResolveUnitDir(unitDir), RunnerUnit);
        return _fileSystem.Exists(unitPath);
    }

    private async Task<int> InstallAsync(SystemdUnit unit, ServiceInstallOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;

        var unitDir = ResolveUnitDir(options.UnitDir);

        var unitPath = Path.Combine(unitDir, unit.Name);
        await _fileSystem.WriteAllTextAsync(unitPath, unit.Render());
        _out.WriteLine($"Wrote {unitPath}");

        if (options.DryRun)
        {
            _out.WriteLine("Dry run: skipped systemctl enable/start");
            return 0;
        }

        var (daemonReload, _, daemonReloadErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "daemon-reload"]);
        if (daemonReload != 0)
        {
            if (!string.IsNullOrWhiteSpace(daemonReloadErr)) _err.Write(daemonReloadErr);
            return daemonReload;
        }

        var (enable, _, enableErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "enable", unit.Name]);
        if (enable != 0)
        {
            if (!string.IsNullOrWhiteSpace(enableErr)) _err.Write(enableErr);
            return enable;
        }

        var (start, _, startErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "restart", unit.Name]);
        if (start != 0)
        {
            if (!string.IsNullOrWhiteSpace(startErr)) _err.Write(startErr);
            return start;
        }

        _out.WriteLine($"Installed and started {unit.Name}");
        await TryEnableLingerAsync();
        return 0;
    }

    private async Task<int> StartAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "start");
    }

    private async Task<int> StopAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "stop");
    }

    private async Task<int> RestartAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "restart");
    }

    private async Task<int> StatusAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "status", "--no-pager");
    }

    private async Task<int> LogsAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        var args = new List<string> { "--user", "-u", unitName, "--no-pager", "-n", options.Lines.ToString() };
        if (options.Follow)
            args.Add("-f");

        if (options.DryRun)
        {
            _out.WriteLine("Dry run: journalctl " + string.Join(' ', args.Select(ShellQuote)));
            return 0;
        }

        var (code, stdout, stderr) = await _commandExecutor.ExecuteAsync("journalctl", args.ToArray());
        if (!string.IsNullOrWhiteSpace(stdout)) _out.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
        return code;
    }

    private async Task<int> UninstallAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        var unitPath = Path.Combine(ResolveUnitDir(options.UnitDir), unitName);

        if (options.DryRun)
        {
            _out.WriteLine($"Dry run: systemctl --user disable --now {unitName}");
            _out.WriteLine($"Dry run: remove {unitPath}");
            _out.WriteLine("Dry run: systemctl --user daemon-reload");
            return 0;
        }

        var (disable, _, disableErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "disable", "--now", unitName]);
        if (disable != 0)
        {
            if (!string.IsNullOrWhiteSpace(disableErr)) _err.Write(disableErr);
            return disable;
        }

        if (_fileSystem.Exists(unitPath))
        {
            _fileSystem.Delete(unitPath);
            _out.WriteLine($"Removed {unitPath}");
        }
        else
        {
            _out.WriteLine($"Unit file not found: {unitPath}");
        }

        var (reload, _, reloadErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "daemon-reload"]);
        if (reload != 0 && !string.IsNullOrWhiteSpace(reloadErr)) _err.Write(reloadErr);
        return reload;
    }

    private async Task<int> RunSystemctlAsync(string unitName, ServiceCommandOptions options, params string[] command)
    {
        var args = new List<string> { "--user" };
        args.AddRange(command);
        args.Add(unitName);

        if (options.DryRun)
        {
            _out.WriteLine("Dry run: systemctl " + string.Join(' ', args.Select(ShellQuote)));
            return 0;
        }

        var (code, stdout, stderr) = await _commandExecutor.ExecuteAsync("systemctl", args.ToArray());
        if (!string.IsNullOrWhiteSpace(stdout)) _out.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
        return code;
    }

    private bool EnsureSystemdSupported(bool dryRun)
    {
        if (OperatingSystem.IsLinux() || dryRun || _commandExecutor is not SystemCommandExecutor) return true;
        _err.WriteLine("systemd service management is only supported on Linux. Use --dry-run to preview commands.");
        return false;
    }

    private async Task TryEnableLingerAsync()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) return;
        var (code, _, stderr) = await _commandExecutor.ExecuteAsync("loginctl", ["enable-linger", user]);
        if (code != 0)
            _err.WriteLine("Warning: loginctl enable-linger failed; service may stop when the user logs out.");
    }

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot.Replace('\\', '/');

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mohist.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveUnitDir(string? explicitUnitDir) =>
        (explicitUnitDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "systemd",
            "user")).Replace('\\', '/');

    private static string DotnetRun(string dotnetPath, string repoRoot, string projectPath, IReadOnlyList<string> args)
    {
        var combinedPath = (repoRoot + "/" + projectPath).Replace('\\', '/');
        var parts = new List<string>
        {
            dotnetPath,
            "run",
            "--project",
            ShellQuote(combinedPath),
        };
        if (args.Count > 0)
        {
            parts.Add("--");
            parts.AddRange(args.Select(ShellQuote));
        }
        return string.Join(' ', parts);
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');

    private Dictionary<string, string> BuildServiceEnvironment()
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = BuildServicePath(),
        };
        var dotnetRoot = ResolveDotnetRoot();
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
            environment["DOTNET_ROOT_X64"] = dotnetRoot;
        }

        return environment;
    }

    private string? ResolveDotnetRoot()
    {
        var configured = _environment.GetEnvironmentVariable("DOTNET_ROOT_X64")
            ?? _environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var home = _environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;

        var userDotnetRoot = Path.Combine(home, ".dotnet");
        return _fileSystem.Exists(Path.Combine(userDotnetRoot, "dotnet"))
            ? userDotnetRoot
            : null;
    }

    private static string BuildServicePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var entries = new[]
        {
            Path.Combine(home, ".opencode", "bin"),
            Path.Combine(home, ".local", "bin"),
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
        };
        return string.Join(':', entries.Select(NormalizePath));
    }

    private string ResolveExecutable(string name)
    {
        if (Path.IsPathRooted(name))
            return name.Replace('\\', '/');

        var path = _environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return name;

        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim();
            if (dir.Length == 0) continue;
            var candidate = (dir.EndsWith('/') ? dir + name : dir + "/" + name).Replace('\\', '/');
            if (File.Exists(candidate))
                return candidate;
        }

        return name;
    }

    private static string ShellQuote(string value)
    {
        if (value.Length == 0) return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':' or '='))
            return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

internal record SystemdUnit(
    string Name,
    string Description,
    string WorkingDirectory,
    string ExecStart,
    IReadOnlyDictionary<string, string> Environment)
{
    public string Render()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Unit]");
        builder.AppendLine($"Description={Description}");
        builder.AppendLine("After=network.target");
        builder.AppendLine();
        builder.AppendLine("[Service]");
        builder.AppendLine("Type=simple");
        builder.AppendLine($"WorkingDirectory={EscapeValue(NormalizePath(WorkingDirectory))}");
        foreach (var (key, value) in Environment)
            builder.AppendLine($"Environment=\"{EscapeEnvironment(key)}={EscapeEnvironment(NormalizePath(value))}\"");
        builder.AppendLine($"ExecStart={ExecStart}");
        builder.AppendLine("Restart=on-failure");
        builder.AppendLine("RestartSec=5");
        builder.AppendLine("SuccessExitStatus=0 143");
        builder.AppendLine("TimeoutStopSec=30");
        builder.AppendLine("StandardOutput=journal");
        builder.AppendLine("StandardError=journal");
        builder.AppendLine();
        builder.AppendLine("[Install]");
        builder.AppendLine("WantedBy=default.target");
        return builder.ToString().Replace("\r\n", "\n");
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');

    private static string EscapeValue(string value) => RejectControlChars(value);

    private static string EscapeEnvironment(string value) => RejectControlChars(value)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string RejectControlChars(string value)
    {
        if (value.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("systemd unit values cannot contain control characters");
        return value;
    }
}

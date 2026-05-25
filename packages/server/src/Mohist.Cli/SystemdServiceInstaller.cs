using System.Diagnostics;
using System.Text;

internal sealed class SystemdServiceInstaller
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public SystemdServiceInstaller(TextWriter output, TextWriter error)
    {
        _out = output;
        _err = error;
    }

    public async Task<int> InstallServerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var unit = new SystemdUnit(
            Name: "mohist.service",
            Description: "Mohist Server",
            WorkingDirectory: repoRoot,
            ExecStart: DotnetRun(repoRoot, "packages/server/src/Mohist.Server/Mohist.Server.csproj", [
                "--urls",
                options.ListenUrl ?? "http://127.0.0.1:3456",
            ]),
            Environment: new Dictionary<string, string>());

        return await InstallAsync(unit, options);
    }

    public async Task<int> InstallRunnerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var environment = new Dictionary<string, string>
        {
            ["ServerUrl"] = options.ServerUrl ?? "http://127.0.0.1:3456",
        };
        if (!string.IsNullOrWhiteSpace(options.RunnerRoot))
            environment["RunnerRoot"] = Path.GetFullPath(options.RunnerRoot);

        var unit = new SystemdUnit(
            Name: "mohist-runner.service",
            Description: "Mohist Runner",
            WorkingDirectory: repoRoot,
            ExecStart: DotnetRun(repoRoot, "packages/runner/src/Mohist.Runner.Cli/Mohist.Runner.Cli.csproj", []),
            Environment: environment);

        return await InstallAsync(unit, options);
    }

    private async Task<int> InstallAsync(SystemdUnit unit, ServiceInstallOptions options)
    {
        if (!OperatingSystem.IsLinux() && !options.DryRun)
        {
            _err.WriteLine("systemd install is only supported on Linux. Use --dry-run to render the unit file.");
            return 1;
        }

        var unitDir = Path.GetFullPath(options.UnitDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "systemd",
            "user"));
        Directory.CreateDirectory(unitDir);

        var unitPath = Path.Combine(unitDir, unit.Name);
        await File.WriteAllTextAsync(unitPath, unit.Render(), Encoding.UTF8);
        _out.WriteLine($"Wrote {unitPath}");

        if (options.DryRun)
        {
            _out.WriteLine("Dry run: skipped systemctl enable/start");
            return 0;
        }

        var daemonReload = await RunAsync("systemctl", ["--user", "daemon-reload"]);
        if (daemonReload != 0) return daemonReload;

        var enable = await RunAsync("systemctl", ["--user", "enable", unit.Name]);
        if (enable != 0) return enable;

        var start = await RunAsync("systemctl", ["--user", "restart", unit.Name]);
        if (start != 0) return start;

        _out.WriteLine($"Installed and started {unit.Name}");
        await TryEnableLingerAsync();
        return 0;
    }

    private async Task<int> RunAsync(string fileName, string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Failed to run {fileName}: {ex.Message}");
            return 1;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (!string.IsNullOrWhiteSpace(stdout)) _out.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
        return process.ExitCode;
    }

    private async Task TryEnableLingerAsync()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) return;
        var code = await RunAsync("loginctl", ["enable-linger", user]);
        if (code != 0)
            _err.WriteLine("Warning: loginctl enable-linger failed; service may stop when the user logs out.");
    }

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(explicitRoot);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "packages", "server", "Mohist.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string DotnetRun(string repoRoot, string projectPath, IReadOnlyList<string> args)
    {
        var parts = new List<string>
        {
            "dotnet",
            "run",
            "--project",
            ShellQuote(Path.Combine(repoRoot, projectPath)),
        };
        if (args.Count > 0)
        {
            parts.Add("--");
            parts.AddRange(args.Select(ShellQuote));
        }
        return string.Join(' ', parts);
    }

    private static string ShellQuote(string value)
    {
        if (value.Length == 0) return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':' or '='))
            return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

internal sealed record ServiceInstallOptions(
    bool DryRun,
    string? UnitDir,
    string? RepoRoot,
    string? ListenUrl,
    string? ServerUrl,
    string? RunnerRoot)
{
    public static ServiceInstallOptions From(string[] args) => new(
        DryRun: args.Contains("--dry-run"),
        UnitDir: Option(args, "--unit-dir"),
        RepoRoot: Option(args, "--repo-root"),
        ListenUrl: Option(args, "--listen-url"),
        ServerUrl: Option(args, "--server-url"),
        RunnerRoot: Option(args, "--runner-root"));

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i + 1 >= args.Length) throw new ArgumentException($"{name} requires a value");
            return args[i + 1];
        }
        return null;
    }
}

internal sealed record SystemdUnit(
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
        builder.AppendLine($"WorkingDirectory={EscapeValue(WorkingDirectory)}");
        foreach (var (key, value) in Environment)
            builder.AppendLine($"Environment=\"{EscapeEnvironment(key)}={EscapeEnvironment(value)}\"");
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
        return builder.ToString();
    }

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

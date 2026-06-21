using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class ServerCommands
{
    public static Command Build(MohistCliApi api, IServiceProvider provider)
    {
        var server = new Command("server", "Server management");
        var installer = provider.GetRequiredService<IServiceInstaller>();
        var updater = provider.GetRequiredService<SourceCodeUpdater>();

        server.Subcommands.Add(BuildHealth(api));
        server.Subcommands.Add(BuildInstall(installer));
        server.Subcommands.Add(BuildUpdate(updater));
        server.Subcommands.Add(BuildSystemd("start", installer.StartServerAsync, installer));
        server.Subcommands.Add(BuildSystemd("stop", installer.StopServerAsync, installer));
        server.Subcommands.Add(BuildSystemd("restart", installer.RestartServerAsync, installer));
        server.Subcommands.Add(BuildSystemd("status", installer.StatusServerAsync, installer));
        server.Subcommands.Add(BuildLogs(installer));
        server.Subcommands.Add(BuildSystemd("uninstall", installer.UninstallServerAsync, installer));

        return server;
    }

    private static Command BuildHealth(MohistCliApi api)
    {
        var cmd = new Command("health", "Check server health");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/health"));
        return cmd;
    }

    private static Command BuildInstall(IServiceInstaller installer)
    {
        var cmd = new Command("install", "Install server as a managed background service");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var listenUrlOpt = new Option<string?>("--listen-url") { Description = "Server listen URL" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(listenUrlOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var listenUrl = ctx.GetValue(listenUrlOpt);
            return installer.InstallServerAsync(new ServiceInstallOptions(dryRun, unitDir, repoRoot, listenUrl, null, null));
        });
        return cmd;
    }

    private static Command BuildUpdate(SourceCodeUpdater updater)
    {
        var cmd = new Command("update", "Build current source and restart server service");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(async ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            return await updater.UpdateServerAsync(repoRoot, dryRun);
        });
        return cmd;
    }

    private static Command BuildSystemd(string name, Func<ServiceCommandOptions, Task<int>> handler, IServiceInstaller installer)
    {
        var cmd = new Command(name, $"{name} server managed service");
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return handler(new ServiceCommandOptions(dryRun, unitDir, 100, false));
        });
        return cmd;
    }

    private static Command BuildLogs(IServiceInstaller installer)
    {
        var cmd = new Command("logs", "View server service logs");
        var linesOpt = MohistCliCommands.LinesOption();
        var followOpt = MohistCliCommands.FollowOption();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(linesOpt);
        cmd.Options.Add(followOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var lines = ctx.GetValue(linesOpt);
            var follow = ctx.GetValue(followOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return installer.LogsServerAsync(new ServiceCommandOptions(dryRun, unitDir, lines, follow));
        });
        return cmd;
    }
}

internal static class RunnerCommands
{
    public static Command Build(MohistCliApi api, IServiceProvider provider)
    {
        var runner = new Command("runner", "Runner management");
        var installer = provider.GetRequiredService<IServiceInstaller>();
        var environment = provider.GetService<IEnvironmentVariableProvider>() ?? SystemEnvironmentVariableProvider.Instance;

        runner.Subcommands.Add(BuildInstall(installer));
        runner.Subcommands.Add(BuildSystemd("start", installer.StartRunnerAsync, installer));
        runner.Subcommands.Add(BuildSystemd("stop", installer.StopRunnerAsync, installer));
        runner.Subcommands.Add(BuildSystemd("restart", installer.RestartRunnerAsync, installer));
        runner.Subcommands.Add(BuildSystemd("status", installer.StatusRunnerAsync, installer));
        runner.Subcommands.Add(BuildLogs(installer));
        runner.Subcommands.Add(BuildSystemd("uninstall", installer.UninstallRunnerAsync, installer));
        runner.Subcommands.Add(BuildList(api, environment));
        runner.Subcommands.Add(BuildShow(api));

        return runner;
    }

    private static Command BuildList(MohistCliApi api, IEnvironmentVariableProvider environment)
    {
        var cmd = new Command("list", "List runners");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var scopeOpt = new Option<string>("--scope")
        {
            Description = "Filter runners by scope (all, global, project)",
            DefaultValueFactory = _ => "all",
        };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(scopeOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var scopeRaw = ctx.GetValue(scopeOpt) ?? "all";
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

                var scope = ParseScopeFilter(scopeRaw);
                if (scope is null)
                {
                    api.Error.WriteLine($"--scope must be 'all', 'global', or 'project' (got '{scopeRaw}')");
                    return 1;
                }

                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;

                var colorEnabled = !Console.IsOutputRedirected
                    && string.IsNullOrEmpty(environment.GetEnvironmentVariable("NO_COLOR"));

                return await api.PrintRunnerListAsync(resolvedProjectId, scope.Value, mode, colorEnabled);
            }
        });
        return cmd;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show a single runner's full detail (read-only)");
        var runnerIdArg = new Argument<string>("runner-id") { Description = "Runner identifier" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(runnerIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var runnerId = ctx.GetValue(runnerIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                if (string.IsNullOrWhiteSpace(runnerId))
                {
                    api.Error.WriteLine("runner-id is required");
                    return 1;
                }
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
                return await api.PrintRunnerShowAsync(
                    resolvedProjectId,
                    Uri.EscapeDataString(runnerId!),
                    mode);
            }
        });
        return cmd;
    }

    private static MohistCliApi.RunnerScopeFilter? ParseScopeFilter(string raw)
    {
        if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase))
            return MohistCliApi.RunnerScopeFilter.All;
        if (string.Equals(raw, "global", StringComparison.OrdinalIgnoreCase))
            return MohistCliApi.RunnerScopeFilter.Global;
        if (string.Equals(raw, "project", StringComparison.OrdinalIgnoreCase))
            return MohistCliApi.RunnerScopeFilter.Project;
        return null;
    }

    private static Command BuildInstall(IServiceInstaller installer)
    {
        var cmd = new Command("install", "Install runner as a managed background service");
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var serverUrlOpt = new Option<string?>("--server-url") { Description = "Server URL" };
        var runnerRootOpt = new Option<string?>("--runner-root") { Description = "Runner root path" };
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(repoRootOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.Options.Add(serverUrlOpt);
        cmd.Options.Add(runnerRootOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            var repoRoot = ctx.GetValue(repoRootOpt);
            var serverUrl = ctx.GetValue(serverUrlOpt);
            var runnerRoot = ctx.GetValue(runnerRootOpt);
            return installer.InstallRunnerAsync(new ServiceInstallOptions(dryRun, unitDir, repoRoot, null, serverUrl, runnerRoot));
        });
        return cmd;
    }

    private static Command BuildSystemd(string name, Func<ServiceCommandOptions, Task<int>> handler, IServiceInstaller installer)
    {
        var cmd = new Command(name, $"{name} runner managed service");
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return handler(new ServiceCommandOptions(dryRun, unitDir, 100, false));
        });
        return cmd;
    }

    private static Command BuildLogs(IServiceInstaller installer)
    {
        var cmd = new Command("logs", "View runner service logs");
        var linesOpt = MohistCliCommands.LinesOption();
        var followOpt = MohistCliCommands.FollowOption();
        var dryRunOpt = MohistCliCommands.DryRunOption();
        var unitDirOpt = MohistCliCommands.UnitDirOption();
        cmd.Options.Add(linesOpt);
        cmd.Options.Add(followOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(unitDirOpt);
        cmd.SetAction(ctx =>
        {
            var lines = ctx.GetValue(linesOpt);
            var follow = ctx.GetValue(followOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            var unitDir = ctx.GetValue(unitDirOpt);
            return installer.LogsRunnerAsync(new ServiceCommandOptions(dryRun, unitDir, lines, follow));
        });
        return cmd;
    }
}

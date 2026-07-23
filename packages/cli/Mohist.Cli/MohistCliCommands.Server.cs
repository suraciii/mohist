using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class ServerCommands
{
    public static Command Build(MohistCliApi api, IServiceProvider _)
    {
        var server = new Command(
            "server",
            "Connected Mohist Server application. Read-only — reads facts about the running Server (status, health, info, application logs) over the Server API. Local managed-service lifecycle (start, stop, restart, status, logs, uninstall) lives under 'mo service <verb> server'.");

        server.Subcommands.Add(BuildStatus(api));
        server.Subcommands.Add(BuildHealth(api));
        server.Subcommands.Add(BuildInfo(api));
        server.Subcommands.Add(BuildLogs(api));

        return server;
    }

    private static Command BuildStatus(MohistCliApi api)
    {
        var cmd = new Command(
            "status",
            "Show overall Server status (aggregated across all projects). Formerly exposed as 'mo project status'.");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/status?all=true"));
        return cmd;
    }

    private static Command BuildHealth(MohistCliApi api)
    {
        var cmd = new Command("health", "Check server health");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/health"));
        return cmd;
    }

    private static Command BuildInfo(MohistCliApi api)
    {
        var cmd = new Command(
            "info",
            "Show server-side system diagnostics (identity, source, install, update, services, paths). Distinct from 'mo info' (CLI-local environment).");

        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");

        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            return api.PrintSystemInfoAsync(mode);
        });

        return cmd;
    }

    private static Command BuildLogs(MohistCliApi api)
    {
        var cmd = new Command(
            "logs",
            "Show the connected Server's application logs (the Mohist server's own log tail). These are application logs and are not interchangeable with service-manager logs; use 'mo service logs server' for service-manager logs (systemd journal or scheduled-task output).");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/logs/tail"));
        return cmd;
    }
}

internal static class RunnerCommands
{
    public static Command Build(MohistCliApi api, IServiceProvider provider)
    {
        var runner = new Command(
            "runner",
            "Server-registered Runner resources. Read-only — reads presence, capacity, heartbeat, and status from the connected Server.");
        var environment = provider.GetService<IEnvironmentVariableProvider>() ?? SystemEnvironmentVariableProvider.Instance;

        runner.Subcommands.Add(BuildList(api, environment));
        runner.Subcommands.Add(BuildView(api));
        runner.Subcommands.Add(BuildStatus(api));

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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;

                var scope = ParseScopeFilter(scopeRaw);
                if (scope is null)
                {
                    api.Error.WriteLine($"--scope must be 'all', 'global', or 'project' (got '{scopeRaw}')");
                    return 1;
                }

                var (mode, exit) = api.ResolveOutputMode(output);


                if (exit != 0) return exit;


                var colorEnabled = !Console.IsOutputRedirected
                    && string.IsNullOrEmpty(environment.GetEnvironmentVariable("NO_COLOR"));

                return await api.PrintRunnerListAsync(resolvedProjectId, scope.Value, mode, colorEnabled);
            }
        });
        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command("view", "Show a single runner's full detail (read-only)");
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
            return ViewAsync();

            async Task<int> ViewAsync()
            {
                if (string.IsNullOrWhiteSpace(runnerId))
                {
                    api.Error.WriteLine("runner-id is required");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                return await api.PrintRunnerShowAsync(
                    resolvedProjectId,
                    Uri.EscapeDataString(runnerId!),
                    mode);
            }
        });
        return cmd;
    }

    private static Command BuildStatus(MohistCliApi api)
    {
        var cmd = new Command("status", "Show online runner summary (id, heartbeat, idle/busy state)");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return StatusAsync();

            async Task<int> StatusAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;

                var (mode, exit) = api.ResolveOutputMode(output);


                if (exit != 0) return exit;

                return await api.PrintRunnerStatusAsync(resolvedProjectId, mode);
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

}

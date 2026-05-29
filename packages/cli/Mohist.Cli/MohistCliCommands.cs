using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class MohistCliCommands
{
    public static RootCommand Build(MohistCliApi api, IServiceProvider provider)
    {
        var root = new RootCommand("Mohist CLI");

        root.Subcommands.Add(BuildStatusCommand(api));
        root.Subcommands.Add(BuildLogsCommand(api));
        root.Subcommands.Add(ServerCommands.Build(api, provider));
        root.Subcommands.Add(RunnerCommands.Build(api, provider));
        root.Subcommands.Add(InstallCommands.Build(provider));
        root.Subcommands.Add(UpdateCommands.Build(provider));
        root.Subcommands.Add(ProjectCommands.Build(api));
        root.Subcommands.Add(IssueCommands.Build(api));
        root.Subcommands.Add(ConfigProvidersCommands.BuildConfig(api));

        return root;
    }

    internal static Option<bool> DryRunOption() =>
        new("--dry-run") { Description = "Preview commands without executing" };

    internal static Option<string?> UnitDirOption() =>
        new("--unit-dir") { Description = "systemd unit directory" };

    internal static Option<int> LinesOption() =>
        new("--lines", "-n") { Description = "Number of log lines", DefaultValueFactory = _ => 100 };

    internal static Option<bool> FollowOption() =>
        new("--follow", "-f") { Description = "Follow log output" };

    internal static Option<string?> ProjectIdOption() =>
        new("--project-id") { Description = "Project ID" };

    internal static Option<string[]?> LabelOption() =>
        new("--label", "-l") { Description = "Filter by labels", AllowMultipleArgumentsPerToken = true };

    internal static Option<string?> PriorityOption() =>
        new("--priority", "-p") { Description = "Filter by priority" };

    internal static Option<string?> StageOption() =>
        new("--stage", "-s") { Description = "Filter by stage" };

    internal static string ProjectQuery(string? projectId) => Query(ProjectId: projectId);

    internal static string Escape(string value) => Uri.EscapeDataString(value);

    internal static Task<int> RunAsync(HttpClient http, string[] args, TextWriter output, TextWriter error, IFileSystem fileSystem, ICommandExecutor commandExecutor)
    {
        var api = new MohistCliApi(http, output, error, fileSystem, commandExecutor);
        var services = new ServiceCollection();
        services.AddSingleton(api);
        services.AddSingleton(fileSystem);
        services.AddSingleton(commandExecutor);
        services.AddSingleton<SystemdServiceInstaller>();
        services.AddSingleton<SourceCodeUpdater>();
        var provider = services.BuildServiceProvider();
        var root = Build(api, provider);
        return root.Parse(args).InvokeAsync();
    }

    internal static string Query(
        string? ProjectId = null,
        string? Stage = null,
        string? Label = null,
        string? Priority = null,
        bool? Archived = null,
        bool? All = null)
    {
        var parts = new List<string>();
        Add("projectId", ProjectId);
        Add("stage", Stage);
        Add("label", Label);
        Add("priority", Priority);
        Add("archived", Archived?.ToString().ToLowerInvariant());
        Add("all", All?.ToString().ToLowerInvariant());
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }
    }

    private static Command BuildStatusCommand(MohistCliApi api)
    {
        var cmd = new Command("status", "Show server status");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/status?all=true"));
        return cmd;
    }

    private static Command BuildLogsCommand(MohistCliApi api)
    {
        var cmd = new Command("logs", "Show recent logs");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/logs/tail"));
        return cmd;
    }
}

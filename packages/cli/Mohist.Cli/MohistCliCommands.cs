using System.CommandLine;
using System.CommandLine.Invocation;
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
        root.Subcommands.Add(SkillsCommands.Build(provider));
        root.Subcommands.Add(WorkflowCommands.Build(api));
        root.Subcommands.Add(BuildUseCommand(api));
        root.Subcommands.Add(ProjectCommands.Build(api));
        root.Subcommands.Add(RepositoryCommands.Build(api));
        root.Subcommands.Add(IssueCommands.Build(api));
        root.Subcommands.Add(ConfigProvidersCommands.BuildConfig(api));

        return root;
    }

    internal static Option<bool> DryRunOption() =>
        new("--dry-run") { Description = "Preview commands without executing" };

    internal static Option<string?> UnitDirOption() =>
        new("--unit-dir") { Description = "Service unit directory (Linux only)" };

    internal static Option<int> LinesOption() =>
        new("--lines", "-n") { Description = "Number of log lines", DefaultValueFactory = _ => 100 };

    internal static Option<bool> FollowOption() =>
        new("--follow", "-f") { Description = "Follow log output" };

    internal static Option<string?> ProjectIdOption() =>
        new("--project-id") { Description = ProjectRefOptionDescription };

    internal static (Option<string?> Project, Option<string?> ProjectId) ProjectRefOption()
    {
        var project = new Option<string?>("--project") { Description = ProjectRefOptionDescription };
        var projectId = new Option<string?>("--project-id") { Description = ProjectRefOptionDescription };
        return (project, projectId);
    }

    internal static Option<string> OutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format (table, json)",
            DefaultValueFactory = _ => "json",
        };

    internal const string NoActiveProjectMessage =
        "Run 'mo project use <name-or-id>' or pass --project <name-or-id>";

    private const string ProjectRefOptionDescription =
        "Project name or id (canonical: --project; --project-id is a backwards-compatible alias)";

    internal static Option<string[]?> LabelOption() =>
        new("--label", "-l") { Description = "Filter by labels", AllowMultipleArgumentsPerToken = true };

    internal static Option<string?> PriorityOption() =>
        new("--priority", "-p") { Description = "Filter by priority" };

    internal static Option<string?> StageOption() =>
        new("--stage", "-s") { Description = "Filter by stage" };

    internal static string ProjectQuery(string? projectId) => Query(ProjectId: projectId);

    internal static string Escape(string value) => Uri.EscapeDataString(value);

    internal static Task<int> RunAsync(HttpClient http, string[] args, TextWriter output, TextWriter error, IFileSystem fileSystem, ICommandExecutor commandExecutor, IEnvironmentVariableProvider? environment = null, TextReader? standardInput = null)
    {
        environment ??= SystemEnvironmentVariableProvider.Instance;
        var api = new MohistCliApi(http, output, error, fileSystem, commandExecutor, standardInput);
        var services = new ServiceCollection();
        services.AddSingleton(api);
        services.AddSingleton(output);
        services.AddSingleton(error);
        services.AddSingleton<IFileSystem>(fileSystem);
        services.AddSingleton<ICommandExecutor>(commandExecutor);
        services.AddSingleton<IEnvironmentVariableProvider>(environment);
        services.AddSingleton<IServiceInstaller>(sp => OperatingSystem.IsWindows() ? new WindowsScheduledTaskInstaller(output, error, fileSystem, commandExecutor) : new SystemdServiceInstaller(output, error, fileSystem, commandExecutor));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();
        var provider = services.BuildServiceProvider();
        var root = Build(api, provider);
        var config = new InvocationConfiguration { Output = output, Error = error };
        return root.Parse(args).InvokeAsync(config);
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

    private static Command BuildUseCommand(MohistCliApi api)
    {
        var cmd = new Command("use", "Set active project");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        cmd.Arguments.Add(identifierArg);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            return api.UseProjectAsync(identifier!);
        });
        return cmd;
    }
}

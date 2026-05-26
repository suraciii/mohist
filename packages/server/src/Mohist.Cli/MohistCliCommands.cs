using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class MohistCliCommands
{
    public static RootCommand Build(MohistCliApi api)
    {
        var root = new RootCommand("Mohist CLI");

        root.Subcommands.Add(BuildStatusCommand(api));
        root.Subcommands.Add(BuildLogsCommand(api));
        root.Subcommands.Add(ServerCommands.Build(api));
        root.Subcommands.Add(RunnerCommands.Build(api));
        root.Subcommands.Add(ProjectCommands.Build(api));
        root.Subcommands.Add(IssueCommands.Build(api));
        root.Subcommands.Add(ConfigProvidersCommands.BuildConfig(api));
        root.Subcommands.Add(ConfigProvidersCommands.BuildProviders(api));

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

    internal static SystemdServiceInstaller CreateSystemd(MohistCliApi api) =>
        new(api.Output, api.Error);

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

    public static Task<int> RunAsync(string[] args)
    {
        var api = new MohistCliApi();
        var root = Build(api);
        return root.Parse(args).InvokeAsync();
    }

    internal static Task<int> RunAsync(HttpClient http, string[] args, TextWriter output, TextWriter error)
    {
        var api = new MohistCliApi(http, output, error);
        var root = Build(api);
        return root.Parse(args).InvokeAsync();
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
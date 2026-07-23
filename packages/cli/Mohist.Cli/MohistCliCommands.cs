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

        root.Subcommands.Add(InfoCommands.Build(provider));
        root.Subcommands.Add(ServerCommands.Build(api, provider));
        root.Subcommands.Add(RunnerCommands.Build(api, provider));
        root.Subcommands.Add(ServiceCommands.Build(provider));
        root.Subcommands.Add(InstallCommands.Build(provider));
        root.Subcommands.Add(UpdateCommands.Build(provider));
        root.Subcommands.Add(SkillsCommands.Build(provider));
        root.Subcommands.Add(RunCommands.Build(api));
        root.Subcommands.Add(WorkflowCommands.Build(api));
        var environment = provider.GetService<IEnvironmentVariableProvider>()
            ?? SystemEnvironmentVariableProvider.Instance;
        var operatorCredential = provider.GetService<OperatorCredentialProvider>()
            ?? new OperatorCredentialProvider(api.FileSystem, environment);
        root.Subcommands.Add(EventCommands.Build(api, operatorCredential));
        root.Subcommands.Add(ActivityCommands.Build(api));
        root.Subcommands.Add(RoutingCommands.Build(api));
        root.Subcommands.Add(ProjectCommands.Build(api));
        root.Subcommands.Add(RepositoryCommands.Build(api));
        root.Subcommands.Add(IssueCommands.Build(api));
        root.Subcommands.Add(AgentCommands.Build(api));
        root.Subcommands.Add(EpicCommands.Build(api));
        root.Subcommands.Add(LabelCommands.Build(api));
        root.Subcommands.Add(OpencodeCommands.Build(api));
        root.Subcommands.Add(ConfigProvidersCommands.BuildConfig(api));
        root.Subcommands.Add(NotifyCommands.Build(api));
        root.Subcommands.Add(OtelCommands.Build(api, environment, provider.GetService<IOtelQueryExecutor>() ?? new SqliteOtelQueryExecutor()));

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

    internal static Option<string?> ProjectIdOption()
    {
        var option = new Option<string?>("--project-id") { Description = "Legacy project option" };
        option.Hidden = true;
        return option;
    }

    internal static (Option<string?> Project, Option<string?> ProjectId) ProjectRefOption()
    {
        var project = new Option<string?>("--project") { Description = ProjectRefOptionDescription };
        var projectId = ProjectIdOption();
        projectId.Hidden = true;
        return (project, projectId);
    }

    internal static Option<string?> OutputOption(string defaultValue = "table", string formats = "table, json") =>
        CreateOutputOption(defaultValue);

    private static Option<string?> CreateOutputOption(string defaultValue)
    {
        var option = new Option<string?>("--json")
        {
            Description = "Return selected fields, or list available fields when no value is supplied",
            DefaultValueFactory = _ => defaultValue,
            Arity = ArgumentArity.ZeroOrOne,
        };
        option.Validators.Add(result => OutputOptionState.Explicit = !result.Implicit);
        return option;
    }

    internal static Option<string?> JsonSelectionOption() =>
        new("--json")
        {
            Description = "Return selected fields, or list available fields when no value is supplied",
            Arity = ArgumentArity.ZeroOrOne,
        };

    internal const string NoActiveProjectMessage =
        "Run 'mo project use <name-or-id>' or pass --project <name-or-id>";

    internal static class OutputOptionState
    {
        private static readonly AsyncLocal<bool> ExplicitValue = new();

        public static bool Explicit
        {
            get => ExplicitValue.Value;
            set => ExplicitValue.Value = value;
        }
    }

    private const string ProjectRefOptionDescription = "Project name or id";

    internal static Option<string[]?> LabelOption() =>
        new("--label", "-l")
        {
            Description = "Issue label in 'key=value' form (set) or '-key' (remove). Repeatable; e.g. -l stream=frontend -l -bug",
            AllowMultipleArgumentsPerToken = true,
        };

    internal static Option<string[]?> LabelFilterOption() =>
        new("--label", "-l")
        {
            Description = "Filter issues by a label in 'key=value' form (e.g. -l stream=frontend). Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };

    internal static Option<string?> PriorityOption() =>
        new("--priority", "-p") { Description = "Filter by priority" };

    internal static Option<string?> StageOption() =>
        new("--stage", "-s") { Description = "Filter by stage" };

    internal static (Option<bool> Ready, Option<bool> Draft) IsDraftFlags(string action)
    {
        var ready = new Option<bool>("--ready")
        {
            Description = $"Mark the issue as ready (isDraft=false) when {action}ing",
        };
        var draft = new Option<bool>("--draft")
        {
            Description = $"Mark the issue as a draft (isDraft=true) when {action}ing (default for new issues)",
        };
        return (ready, draft);
    }

    internal enum DraftFlagState
    {
        Ready,
        Draft,
        Unspecified,
        Conflicting,
    }

    internal static DraftFlagState ResolveDraftFlagState(bool ready, bool draft)
    {
        if (ready && draft) return DraftFlagState.Conflicting;
        if (ready) return DraftFlagState.Ready;
        if (draft) return DraftFlagState.Draft;
        return DraftFlagState.Unspecified;
    }

    internal static string ProjectQuery(string? projectId) => Query(ProjectId: projectId);

    internal static string Escape(string value) => Uri.EscapeDataString(value);

    internal static async Task<int> RunAsync(HttpClient http, string[] args, TextWriter output, TextWriter error, IFileSystem fileSystem, ICommandExecutor commandExecutor, IEnvironmentVariableProvider? environment = null, TextReader? standardInput = null, IOtelQueryExecutor? queryExecutor = null, IServiceInstaller? installer = null, SourceCodeUpdater? updater = null, Func<string>? getUserHome = null, CancellationToken cancellationToken = default, ICliTerminal? terminalOverride = null, TimeProvider? timeProvider = null)
    {
        OutputOptionState.Explicit = false;
        environment ??= SystemEnvironmentVariableProvider.Instance;
        getUserHome ??= fileSystem is RealFileSystem
            ? null
            : () => "/mohist-tests/user";
        var terminal = terminalOverride ?? new CliTerminal(standardInput is null || standardInput == Console.In
            ? !Console.IsInputRedirected
            : standardInput != TextReader.Null);
        var cliEnvironment = new EnvironmentVariableAdapter(environment);
        var api = new MohistCliApi(
            http,
            output,
            error,
            fileSystem,
            commandExecutor,
            standardInput,
            getUserHome,
            terminal: terminal,
            cliEnvironment: cliEnvironment,
            timeProvider: timeProvider,
            cancellationToken: cancellationToken);
        var services = new ServiceCollection();
        services.AddSingleton(api);
        services.AddSingleton(api.ResponseReader);
        services.AddSingleton(output);
        services.AddSingleton(error);
        services.AddSingleton<IFileSystem>(fileSystem);
        services.AddSingleton<ICommandExecutor>(commandExecutor);
        services.AddSingleton<IEnvironmentVariableProvider>(environment);
        services.AddSingleton<OperatorCredentialProvider>();
        services.AddSingleton(http);
        // Production callers leave queryExecutor null and the default
        // SqliteOtelQueryExecutor is used; tests inject a fake so otel query
        // specs never touch a real SQLite file (design/testing.md constraint 1).
        if (queryExecutor is not null)
            services.AddSingleton(queryExecutor);
        // Production callers leave installer/updater null and the default
        // SystemdServiceInstaller / WindowsScheduledTaskInstaller and a default
        // SourceCodeUpdater are constructed. Tests inject fakes so install/update
        // specs never touch real systemd/Task Scheduler/source rebuilds
        // (design/testing.md constraint 1).
        services.AddSingleton<IServiceInstaller>(installer ?? BuildDefaultInstaller(output, error, fileSystem, commandExecutor));
        if (updater is not null)
            services.AddSingleton(updater);
        else
            services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton(sp => new UpdateOperations(output, error, sp.GetRequiredService<IServiceInstaller>(), commandExecutor, fileSystem, environment));
        services.AddSingleton(new RuntimeConsistencyValidator(http, commandExecutor, fileSystem, environment, output));
        services.AddSingleton(new ServiceReadinessProbe(http, output));
        services.AddSingleton(new RunnerRefreshVerifier(http, commandExecutor, fileSystem));
        services.AddSingleton(new UpdateOutcomeReporter(http, output));
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();
        services.AddSingleton<InfoVerboseCollector>();
        services.AddSingleton<InfoCollector>();
        services.AddSingleton<InfoRenderer>();
        var provider = services.BuildServiceProvider();
        var root = Build(api, provider);
        var config = new InvocationConfiguration { Output = output, Error = error };
        var parseConfig = new ParserConfiguration { ResponseFileTokenReplacer = null };
        var parseResult = CommandLineParser.Parse(root, args, parseConfig);
        if (args.Any(arg => string.Equals(arg, "--project-id", StringComparison.Ordinal)
            || arg.StartsWith("--project-id=", StringComparison.Ordinal)))
        {
            await error.WriteLineAsync("--project-id is not supported; use --project <name-or-id>.").ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }
        if (parseResult.Errors.Count > 0)
        {
            var invocation = new CliInvocation(
                output,
                error,
                standardInput ?? Console.In,
                terminal,
                cliEnvironment,
                cancellationToken);
            foreach (var parseError in parseResult.Errors)
                await error.WriteLineAsync(parseError.Message).ConfigureAwait(false);

            var nearestCommand = parseResult.CommandResult.Command;
            var helpConfig = new InvocationConfiguration { Output = error, Error = error };
            await nearestCommand.Parse(["--help"]).InvokeAsync(helpConfig).ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }

        try
        {
            return await parseResult.InvokeAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Operation cancelled.").ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.Cancelled);
        }
    }

    private sealed class EnvironmentVariableAdapter : ICliEnvironment
    {
        private readonly IEnvironmentVariableProvider _provider;

        public EnvironmentVariableAdapter(IEnvironmentVariableProvider provider) => _provider = provider;

        public string? Get(string name) => _provider.GetEnvironmentVariable(name);
    }

    private static IServiceInstaller BuildDefaultInstaller(TextWriter output, TextWriter error, IFileSystem fileSystem, ICommandExecutor commandExecutor)
        => OperatingSystem.IsWindows()
            ? new WindowsScheduledTaskInstaller(output, error, fileSystem, commandExecutor)
            : new SystemdServiceInstaller(output, error, fileSystem, commandExecutor);

    internal static SourceCodeUpdater ResolveSourceCodeUpdater(IServiceProvider provider)
    {
        try
        {
            var registered = provider.GetService<SourceCodeUpdater>();
            if (registered is not null)
                return registered;
        }
        catch (InvalidOperationException)
        {
            // Some help-rendering tests intentionally build a minimal provider.
            // Keep command tree construction independent from update internals.
        }

        var api = provider.GetRequiredService<MohistCliApi>();
        var installer = provider.GetRequiredService<IServiceInstaller>();
        return SourceCodeUpdater.CreateWithDefaults(
            api.Output,
            api.Error,
            installer,
            provider.GetService<ICommandExecutor>() ?? api.CommandExecutor,
            provider.GetService<IFileSystem>() ?? api.FileSystem,
            provider.GetService<IEnvironmentVariableProvider>(),
            api.Http);
    }

    internal static string Query(
        string? ProjectId = null,
        string? Stage = null,
        string? Label = null,
        IReadOnlyList<string>? Labels = null,
        string? Priority = null,
        string? Repository = null,
        int? Parent = null,
        bool? Archived = null,
        bool? All = null)
    {
        var parts = new List<string>();
        Add("projectId", ProjectId);
        Add("stage", Stage);
        Add("label", Label);
        if (Labels is not null)
        {
            foreach (var label in Labels)
                Add("label", label);
        }
        Add("priority", Priority);
        Add("repository", Repository);
        Add("parent", Parent?.ToString());
        Add("archived", Archived?.ToString().ToLowerInvariant());
        Add("all", All?.ToString().ToLowerInvariant());
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }
    }
}

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class MohistCliCommands
{
    public static RootCommand Build(MohistCliApi api, IServiceProvider provider)
    {
        var root = new CliRootCommand("Mohist CLI");

        root.Subcommands.Add(InfoCommands.Build(provider));
        root.Subcommands.Add(AuthCommands.Build(api));
        root.Subcommands.Add(AuditCommands.Build(api));
        root.Subcommands.Add(ServerCommands.Build(api, provider));
        root.Subcommands.Add(RunnerCommands.Build(api, provider));
        root.Subcommands.Add(ServiceCommands.Build(provider));
        root.Subcommands.Add(InstallCommands.Build(provider));
        root.Subcommands.Add(UpdateCommands.Build(provider));
        root.Subcommands.Add(SkillCommands.Build(provider));
        root.Subcommands.Add(RunCommands.Build(api));
        root.Subcommands.Add(WorkflowCommands.Build(api));
        root.Subcommands.Add(EventCommands.Build(api));
        root.Subcommands.Add(ActivityCommands.Build(api));
        root.Subcommands.Add(RoutingCommands.Build(api));
        root.Subcommands.Add(WebhookCommands.Build(api));
        root.Subcommands.Add(ProjectCommands.Build(api));
        root.Subcommands.Add(RepositoryCommands.Build(api));
        root.Subcommands.Add(WorkspaceCommands.Build(api));
        root.Subcommands.Add(IssueCommands.Build(api));
        root.Subcommands.Add(AgentCommands.Build(api));
        root.Subcommands.Add(SlackCommands.Build(api));
        root.Subcommands.Add(GithubCommands.Build(api));
        root.Subcommands.Add(SessionCommands.Build(api));
        root.Subcommands.Add(EpicCommands.Build(api));
        root.Subcommands.Add(LabelCommands.Build(api));
        root.Subcommands.Add(NotifyCommands.Build(api));
        root.Subcommands.Add(OtelCommands.Build(api));
        root.Subcommands.Add(CommandHelpHook.BuildHelpCommand());

        CommandHelpHook.Install(root);
        CommandPresentations.AttachTo(root);

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

    internal static Option<string?> ProjectRefOption() =>
        new("--project") { Description = ProjectRefOptionDescription };

    internal static Option<string?> OutputOption(ResourceDescriptor descriptor, string defaultValue = "table")
    {
        var option = CreateJsonOption(defaultValue);
        CommandPresentationCatalog.AttachJsonFields(option, descriptor);
        return option;
    }

    private static Option<string?> CreateJsonOption(string? defaultValue = null)
    {
        var option = new CliJsonOption("--json")
        {
            Description = "Return selected fields, or list available fields when no value is supplied",
            Arity = ArgumentArity.ZeroOrOne,
        };
        if (defaultValue is not null)
            option.DefaultValueFactory = _ => defaultValue;
        option.Validators.Add(result => OutputOptionState.Explicit = !result.Implicit);
        return option;
    }

    internal static Option<string?> JsonSelectionOption(ResourceDescriptor descriptor)
    {
        var option = CreateJsonOption();
        CommandPresentationCatalog.AttachJsonFields(option, descriptor);
        return option;
    }

    private static Option<string?> CreateJsonOption() => new CliJsonOption("--json")
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
        new("--stage") { Description = "Filter by stage" };

    internal static Option<string?> IssuePriorityOption() =>
        new("--priority", "-p") { Description = "Issue priority (p0|p1|p2|p3|p4)" };

    internal static (Option<bool> Ready, Option<bool> Draft) IsDraftFlags(string action)
    {
        var ready = new Option<bool>("--ready")
        {
            Description = $"Mark the issue as ready (isDraft=false) when {action}",
        };
        var draft = new Option<bool>("--draft")
        {
            Description = $"Mark the issue as a draft (isDraft=true) when {action} (default for new issues)",
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

    private static bool IsHelpToken(string arg)
    {
        if (arg == "--help" || arg == "-h" || arg == "-?" || arg == "/?")
            return true;
        if (arg.StartsWith("--help=", StringComparison.Ordinal))
            return true;
        return false;
    }

    internal static string Escape(string value) => Uri.EscapeDataString(value);

    internal static async Task<int> RunAsync(HttpClient http, string[] args, TextWriter output, TextWriter error, IFileSystem fileSystem, ICommandExecutor commandExecutor, IEnvironmentVariableProvider? environment = null, TextReader? standardInput = null, IServiceInstaller? installer = null, SourceCodeUpdater? updater = null, Func<string>? getUserHome = null, CancellationToken cancellationToken = default, ICliTerminal? terminalOverride = null, TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? pollWait = null, Func<string?>? getLocalHostname = null, IEventSocketFactory? eventSocketFactory = null, Func<double>? eventReconnectJitter = null)
    {
        OutputOptionState.Explicit = false;
        if (IsDirectSlackCredentialArgument(args))
        {
            await error.WriteLineAsync("Slack credentials must be supplied through hidden input or --credentials-file; command-line token arguments are refused.").ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.UsageFailure);
        }
        environment ??= SystemEnvironmentVariableProvider.Instance;
        // The machine-local home is resolved the same way for the provider,
        // the handler and the api so all three agree on where the
        // credentials file lives.
        var effectiveUserHome = getUserHome
            ?? (fileSystem is RealFileSystem
                ? () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : () => "/mohist-tests/user");
        // Single injection point for the command credential: every request
        // this client sends carries Authorization: Bearer when a credential
        // is resolvable (CliCredentialHandler), regardless of which command
        // originates it. The caller-supplied client remains the transport.
        var credentials = new CliCredentialProvider(fileSystem, environment, effectiveUserHome);
        var credentialSession = new CliCredentialSession(
            credentials, http, fileSystem, effectiveUserHome, error);
        http = new HttpClient(new CliCredentialHandler(credentialSession, http))
        {
            BaseAddress = http.BaseAddress,
            Timeout = http.Timeout,
        };
        var composition = CliComposition.Create(new CliCompositionOptions(
            Http: http,
            Output: output,
            Error: error,
            FileSystem: fileSystem,
            CommandExecutor: commandExecutor,
            Environment: environment,
            StandardInput: standardInput,
            Installer: installer,
            Updater: updater,
            GetUserHome: effectiveUserHome,
            GetLocalHostname: getLocalHostname,
            CancellationToken: cancellationToken,
            Terminal: terminalOverride,
            TimeProvider: timeProvider,
            PollWait: pollWait,
            CredentialSession: credentialSession,
            EventSocketFactory: eventSocketFactory,
            EventReconnectJitter: eventReconnectJitter));
        var root = composition.Root;
        var config = new InvocationConfiguration { Output = output, Error = error };
        var parseConfig = new ParserConfiguration { ResponseFileTokenReplacer = null };
        var parseResult = CommandLineParser.Parse(root, args, parseConfig);
        var helpRequested = args.Any(arg => IsHelpToken(arg));
        if (parseResult.Errors.Count > 0 && !helpRequested)
        {
            foreach (var parseError in parseResult.Errors)
                await error.WriteLineAsync(parseError.Message).ConfigureAwait(false);
            if (args.Any(arg => string.Equals(arg, "--output", StringComparison.Ordinal)))
                await error.WriteLineAsync("Use --json for structured output.").ConfigureAwait(false);
            if (args.Any(arg => string.Equals(arg, "true", StringComparison.Ordinal))
                && args.Any(arg => string.Equals(arg, "--json", StringComparison.Ordinal)))
                await error.WriteLineAsync("A required value for the variable was not provided.").ConfigureAwait(false);
            return CommandHelpHook.RenderNearestUsage(parseResult, error);
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

    private static bool IsDirectSlackCredentialArgument(string[] args)
    {
        var credentialCommand = args.Length >= 2
            && string.Equals(args[0], "slack", StringComparison.Ordinal)
            && (string.Equals(args[1], "setup", StringComparison.Ordinal)
                || string.Equals(args[1], "install-agent", StringComparison.Ordinal));
        if (!credentialCommand)
            return false;

        if (args.Any(arg => string.Equals(arg, "--app-token", StringComparison.Ordinal)
            || string.Equals(arg, "--bot-token", StringComparison.Ordinal)
            || string.Equals(arg, "--manager-bot-token", StringComparison.Ordinal)))
            return true;

        // install-agent takes exactly one positional agent reference (index 2);
        // setup takes none. Any other positional value is a refused token literal.
        var positionalStart = string.Equals(args[1], "install-agent", StringComparison.Ordinal) ? 3 : 2;
        for (var index = positionalStart; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--workspace-team" or "--credentials-file" or "--configuration-token-file"
                or "--manager-app-id" or "--manager-bot-user-id" or "--manager-credential-ref" or "--project")
            {
                index++;
                continue;
            }
            if (argument.StartsWith("--workspace-team=", StringComparison.Ordinal)
                || argument.StartsWith("--credentials-file=", StringComparison.Ordinal)
                || argument.StartsWith("--configuration-token-file=", StringComparison.Ordinal)
                || argument.StartsWith("--manager-app-id=", StringComparison.Ordinal)
                || argument.StartsWith("--manager-bot-user-id=", StringComparison.Ordinal)
                || argument.StartsWith("--manager-credential-ref=", StringComparison.Ordinal)
                || argument.StartsWith("--project=", StringComparison.Ordinal)
                || IsHelpToken(argument))
                continue;
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                if (index + 1 < args.Length
                    && !args[index + 1].StartsWith("-", StringComparison.Ordinal)
                    && !IsJsonFieldList(args[index + 1]))
                    return true;
                index++;
                continue;
            }
            if (!argument.StartsWith("-", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsJsonFieldList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .All(part => part.Length > 0 && part.All(char.IsAsciiLetterOrDigit));

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
        int? Epic = null,
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
        Add("epic", Epic?.ToString());
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

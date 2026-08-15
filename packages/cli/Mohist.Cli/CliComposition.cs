using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal sealed record CliCompositionOptions(
    HttpClient Http,
    TextWriter Output,
    TextWriter Error,
    IFileSystem FileSystem,
    ICommandExecutor CommandExecutor,
    IEnvironmentVariableProvider? Environment = null,
    TextReader? StandardInput = null,
    IServiceInstaller? Installer = null,
    SourceCodeUpdater? Updater = null,
    SkillAssetService? SkillAssets = null,
    Func<string>? GetUserHome = null,
    Func<string?>? GetLocalHostname = null,
    CancellationToken CancellationToken = default,
    ICliTerminal? Terminal = null,
    TimeProvider? TimeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? PollWait = null);

internal sealed class CliComposition
{
    private CliComposition(
        RootCommand root,
        MohistCliApi api,
        IServiceProvider services)
    {
        Root = root;
        Api = api;
        Services = services;
    }

    internal RootCommand Root { get; }
    internal MohistCliApi Api { get; }
    internal IServiceProvider Services { get; }

    internal static CliComposition Create(CliCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Http);
        ArgumentNullException.ThrowIfNull(options.Output);
        ArgumentNullException.ThrowIfNull(options.Error);
        ArgumentNullException.ThrowIfNull(options.FileSystem);
        ArgumentNullException.ThrowIfNull(options.CommandExecutor);

        var environment = options.Environment ?? SystemEnvironmentVariableProvider.Instance;
        var effectiveUserHome = options.GetUserHome
            ?? (options.FileSystem is RealFileSystem
                ? () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : () => "/mohist-tests/user");
        var timeProvider = options.TimeProvider ?? TimeProvider.System;
        var pollWait = options.PollWait
            ?? ((TimeSpan delay, CancellationToken cancellationToken) =>
                Task.Delay(delay, timeProvider, cancellationToken));
        var terminal = options.Terminal ?? new CliTerminal(
            options.StandardInput is null || options.StandardInput == Console.In
                ? !Console.IsInputRedirected
                : options.StandardInput != TextReader.Null);
        var cliEnvironment = new EnvironmentVariableAdapter(environment);
        var api = new MohistCliApi(
            options.Http,
            options.Output,
            options.Error,
            options.FileSystem,
            options.CommandExecutor,
            options.StandardInput,
            effectiveUserHome,
            terminal: terminal,
            cliEnvironment: cliEnvironment,
            timeProvider: timeProvider,
            pollWait: pollWait,
            cancellationToken: options.CancellationToken);

        var services = new ServiceCollection();
        services.AddSingleton(api);
        services.AddSingleton(api.ResponseReader);
        services.AddSingleton(options.Output);
        services.AddSingleton(options.Error);
        services.AddSingleton<IFileSystem>(options.FileSystem);
        services.AddSingleton<ICommandExecutor>(options.CommandExecutor);
        services.AddSingleton<IEnvironmentVariableProvider>(environment);
        services.AddSingleton(timeProvider);
        services.AddSingleton(options.Http);
        services.AddSingleton<IServiceInstaller>(options.Installer ?? BuildDefaultInstaller(
            options.Output,
            options.Error,
            options.FileSystem,
            options.CommandExecutor));
        if (options.Updater is not null)
            services.AddSingleton(options.Updater);
        else
            services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton(sp => new UpdateOperations(
            options.Output,
            options.Error,
            sp.GetRequiredService<IServiceInstaller>(),
            options.CommandExecutor,
            options.FileSystem,
            environment,
            getUserHome: effectiveUserHome));
        services.AddSingleton(new RuntimeConsistencyValidator(
            options.Http,
            options.CommandExecutor,
            options.FileSystem,
            environment,
            options.Output,
            getUserHome: effectiveUserHome,
            timeProvider: timeProvider,
            pollWait: pollWait));
        services.AddSingleton(new ServiceReadinessProbe(options.Http, options.Output, timeProvider, pollWait));
        services.AddSingleton(new RunnerRefreshVerifier(
            options.Http,
            options.CommandExecutor,
            options.FileSystem,
            getLocalHostname: options.GetLocalHostname,
            timeProvider: timeProvider,
            pollWait: pollWait));
        services.AddSingleton(new UpdateOutcomeReporter(options.Http, options.Output));
        services.AddSingleton(options.SkillAssets ?? BuildDefaultSkillAssets(options.FileSystem, environment));
        services.AddSingleton<SkillInstallService>(sp => new SkillInstallService(
            sp.GetRequiredService<SkillAssetService>(),
            options.FileSystem,
            environment,
            options.Output,
            options.Error));
        services.AddSingleton<InfoVerboseCollector>();
        services.AddSingleton<InfoCollector>();
        services.AddSingleton<InfoRenderer>();

        var provider = services.BuildServiceProvider();
        var root = MohistCliCommands.Build(api, provider);
        return new CliComposition(root, api, provider);
    }

    private static SkillAssetService BuildDefaultSkillAssets(
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment) =>
        fileSystem is RealFileSystem
            ? new SkillAssetService()
            : new SkillAssetService(fileSystem, environment);

    private static IServiceInstaller BuildDefaultInstaller(
        TextWriter output,
        TextWriter error,
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor) =>
        OperatingSystem.IsWindows()
            ? new WindowsScheduledTaskInstaller(output, error, fileSystem, commandExecutor)
            : new SystemdServiceInstaller(output, error, fileSystem, commandExecutor);

    private sealed class EnvironmentVariableAdapter : ICliEnvironment
    {
        private readonly IEnvironmentVariableProvider _provider;

        public EnvironmentVariableAdapter(IEnvironmentVariableProvider provider) => _provider = provider;

        public string? Get(string name) => _provider.GetEnvironmentVariable(name);
    }
}

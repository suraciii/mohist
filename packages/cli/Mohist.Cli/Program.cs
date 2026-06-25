using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;

namespace Mohist.Cli;

internal static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        var environment = SystemEnvironmentVariableProvider.Instance;
        var http = new HttpClient
        {
            BaseAddress = new Uri(environment.GetEnvironmentVariable(SourceCodeUpdater.ServerUrlEnvironmentVariable) ?? "http://localhost:3456"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var fileSystem = RealFileSystem.Instance;
        var commandExecutor = new SystemCommandExecutor();
        var api = new MohistCliApi(http, Console.Out, Console.Error, fileSystem, commandExecutor, Console.In);
        var installer = new SystemdServiceInstaller(Console.Out, Console.Error, fileSystem, commandExecutor);
        var updateOperations = new UpdateOperations(Console.Out, Console.Error, installer, commandExecutor, fileSystem, environment);
        var validator = new RuntimeConsistencyValidator(http, commandExecutor, fileSystem, environment, Console.Out);
        var readinessProbe = new ServiceReadinessProbe(http, Console.Out);
        var runnerRefreshVerifier = new RunnerRefreshVerifier(http, commandExecutor, fileSystem);
        var outcomeReporter = new UpdateOutcomeReporter(http, Console.Out);
        var updater = new SourceCodeUpdater(
            Console.Out,
            Console.Error,
            updateOperations,
            validator,
            readinessProbe,
            runnerRefreshVerifier,
            outcomeReporter);

        var services = new ServiceCollection();
        services.AddSingleton(api);
        services.AddSingleton<TextWriter>(Console.Out);
        services.AddSingleton(_ => (TextWriter)Console.Error);
        services.AddSingleton<IFileSystem>(fileSystem);
        services.AddSingleton<ICommandExecutor>(commandExecutor);
        services.AddSingleton<IEnvironmentVariableProvider>(environment);
        services.AddSingleton<IServiceInstaller>(_ => OperatingSystem.IsWindows() ? new WindowsScheduledTaskInstaller(Console.Out, Console.Error, fileSystem, commandExecutor) : new SystemdServiceInstaller(Console.Out, Console.Error, fileSystem, commandExecutor));
        services.AddSingleton(updateOperations);
        services.AddSingleton<RuntimeConsistencyValidator>(validator);
        services.AddSingleton<ServiceReadinessProbe>(readinessProbe);
        services.AddSingleton<RunnerRefreshVerifier>(runnerRefreshVerifier);
        services.AddSingleton<UpdateOutcomeReporter>(outcomeReporter);
        services.AddSingleton(updater);
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();
        services.AddSingleton<InfoVerboseCollector>();
        services.AddSingleton<InfoCollector>();
        services.AddSingleton<InfoRenderer>();

        var provider = services.BuildServiceProvider();
        var root = MohistCliCommands.Build(api, provider);
        return await root.Parse(args).InvokeAsync();
    }
}

using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;

namespace Mohist.Cli;

internal static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("MOHIST_SERVER_URL") ?? "http://localhost:3456"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var fileSystem = RealFileSystem.Instance;
        var commandExecutor = new SystemCommandExecutor();
        var api = new MohistCliApi(http, Console.Out, Console.Error, fileSystem, commandExecutor);
        var systemd = new SystemdServiceInstaller(Console.Out, Console.Error, fileSystem, commandExecutor);
        var updater = new SourceCodeUpdater(Console.Out, Console.Error, systemd, commandExecutor);

        var services = new ServiceCollection();
        services.AddSingleton(api);
        services.AddSingleton<IFileSystem>(fileSystem);
        services.AddSingleton<ICommandExecutor>(commandExecutor);
        services.AddSingleton(systemd);
        services.AddSingleton(updater);

        var provider = services.BuildServiceProvider();
        var root = MohistCliCommands.Build(api, provider);
        return await root.Parse(args).InvokeAsync();
    }
}

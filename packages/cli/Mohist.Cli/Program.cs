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
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            return await MohistCliCommands.RunAsync(
                http, args, Console.Out, Console.Error, fileSystem, commandExecutor,
                environment, Console.In, cancellationToken: cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mohist.Server.Tests")]

namespace Mohist.Cli;

internal static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        return await MohistCliCommands.RunAsync(args);
    }
}
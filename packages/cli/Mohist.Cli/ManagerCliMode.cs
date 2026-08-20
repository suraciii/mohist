using Mohist.Workflow.Definition;

namespace Mohist.Cli;

internal static class ManagerCliMode
{
    public const string ExplicitModeFlag = "--manager";
    private static readonly AsyncLocal<bool> ActiveValue = new();

    public static bool Active => ActiveValue.Value;

    public static IDisposable Push(bool enabled)
    {
        var previous = ActiveValue.Value;
        ActiveValue.Value = enabled;
        return new ActiveScope(previous);
    }

    public static bool IsEnabled(
        IReadOnlyList<string> args,
        IEnvironmentVariableProvider environment)
    {
        if (args.Any(arg => IsModeFlag(arg)))
            return true;

        return ManagerCapabilityCatalog.IsManagerModeValue(
            environment.GetEnvironmentVariable(ManagerCapabilityCatalog.ManagerModeEnvironmentVariable));
    }

    public static string[] RemoveModeFlags(IReadOnlyList<string> args) =>
        args.Where(arg => !IsModeFlag(arg)).ToArray();

    public static bool IsModeFlag(string arg) =>
        string.Equals(arg, ExplicitModeFlag, StringComparison.Ordinal)
        || string.Equals(arg, ExplicitModeFlag + "=true", StringComparison.OrdinalIgnoreCase);

    public static bool IsHelpRequest(IReadOnlyList<string> args) =>
        args.Any(arg => arg is "--help" or "-h" or "-?" or "/?"
            || arg.StartsWith("--help=", StringComparison.Ordinal));

    public static async Task<int> RejectIfUnlistedAsync(
        IReadOnlyList<string> args,
        TextWriter error)
    {
        var capability = ManagerCapabilityCatalog.ResolveCli(args);
        if (ManagerCapabilityCatalog.IsManagement(capability))
            return 0;

        await error.WriteLineAsync(
            "The requested command is unavailable in Manager mode; use an allowlisted management capability.")
            .ConfigureAwait(false);
        return CliExitCode.For(CliExitOutcome.UsageFailure);
    }

    private sealed class ActiveScope(bool previous) : IDisposable
    {
        public void Dispose() => ActiveValue.Value = previous;
    }
}

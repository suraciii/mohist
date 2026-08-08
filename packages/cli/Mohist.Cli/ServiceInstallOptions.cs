namespace Mohist.Cli;

internal sealed record ServiceInstallOptions(
    bool DryRun,
    string? UnitDir,
    string? RepoRoot,
    string? ListenUrl,
    string? ServerUrl,
    string? RunnerRoot,
    string? EnrollmentToken = null)
{
    public static ServiceInstallOptions From(string[] args) => new(
        DryRun: args.Contains("--dry-run"),
        UnitDir: Option(args, "--unit-dir"),
        RepoRoot: Option(args, "--repo-root"),
        ListenUrl: Option(args, "--listen-url"),
        ServerUrl: Option(args, "--server-url"),
        RunnerRoot: Option(args, "--runner-root"),
        EnrollmentToken: Option(args, "--enrollment-token"));

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i + 1 >= args.Length) throw new ArgumentException($"{name} requires a value");
            return args[i + 1];
        }
        return null;
    }
}

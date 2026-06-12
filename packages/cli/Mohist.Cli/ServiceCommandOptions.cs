namespace Mohist.Cli;

internal sealed record ServiceCommandOptions(
    bool DryRun,
    string? UnitDir,
    int Lines,
    bool Follow)
{
    public static ServiceCommandOptions From(string[] args) => new(
        DryRun: args.Contains("--dry-run"),
        UnitDir: Option(args, "--unit-dir"),
        Lines: ParseLines(Option(args, "--lines", "-n")),
        Follow: args.Contains("--follow") || args.Contains("-f"));

    private static int ParseLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 100;
        if (int.TryParse(value, out var lines) && lines > 0) return lines;
        throw new ArgumentException("--lines requires a positive integer");
    }

    private static string? Option(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!names.Contains(args[i])) continue;
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value");
            return args[i + 1];
        }
        return null;
    }
}

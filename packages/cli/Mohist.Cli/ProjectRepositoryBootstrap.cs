namespace Mohist.Cli;

internal static class ProjectRepositoryBootstrap
{
    public sealed record Result(
        string WorkTreeRoot,
        string RepositoryName,
        string GitUrl,
        string BaseBranch);

    public abstract record Outcome
    {
        private Outcome() { }

        public sealed record Success(Result Result) : Outcome;

        public sealed record Failure(string Message) : Outcome;
    }

    public static async Task<Outcome> TryResolveAsync(
        string rawPath,
        IFileSystem fileSystem,
        ICommandExecutor commandExecutor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return new Outcome.Failure("--path is required to create a project from a local Git repository.");

        var canonicalized = TryCanonicalize(rawPath, out var canonical, out var canonicalError);
        if (!canonicalized)
            return new Outcome.Failure(canonicalError);

        if (!fileSystem.DirectoryExists(canonical))
            return new Outcome.Failure($"--path '{rawPath}' is not an existing directory.");

        var workTreeRoot = await TryRunGitAsync(
            commandExecutor,
            canonical,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);

        if (workTreeRoot.ExitCode != 0)
            return new Outcome.Failure($"--path '{rawPath}' is not inside a Git working tree (rev-parse failed).");

        var root = TrimLineEnding(workTreeRoot.Stdout);
        if (string.IsNullOrWhiteSpace(root))
            return new Outcome.Failure($"--path '{rawPath}' is not inside a Git working tree (rev-parse returned no root).");

        var headSha = await TryRunGitAsync(
            commandExecutor,
            root,
            ["rev-parse", "HEAD"],
            cancellationToken);

        if (headSha.ExitCode != 0 || string.IsNullOrWhiteSpace(headSha.Stdout.Trim()))
            return new Outcome.Failure($"--path '{rawPath}' has no commit yet (HEAD is unreachable).");

        var originUrl = await TryRunGitAsync(
            commandExecutor,
            root,
            ["remote", "get-url", "origin"],
            cancellationToken);

        if (originUrl.ExitCode != 0 || string.IsNullOrWhiteSpace(originUrl.Stdout.Trim()))
            return new Outcome.Failure($"--path '{root}' has no 'origin' remote with a usable Git URL.");

        var gitUrl = originUrl.Stdout.Trim();

        var baseBranch = await TryResolveBaseBranchAsync(root, commandExecutor, cancellationToken);
        if (baseBranch is null)
            return new Outcome.Failure($"--path '{root}' has no resolvable base branch (no origin/HEAD and no checked-out branch).");

        var repositoryName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repositoryName))
            return new Outcome.Failure($"--path '{root}' produced an empty repository resource name.");

        return new Outcome.Success(new Result(root, repositoryName, gitUrl, baseBranch));
    }

    private static async Task<string?> TryResolveBaseBranchAsync(
        string workTreeRoot,
        ICommandExecutor commandExecutor,
        CancellationToken cancellationToken)
    {
        var originHead = await TryRunGitAsync(
            commandExecutor,
            workTreeRoot,
            ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"],
            cancellationToken);

        if (originHead.ExitCode == 0)
        {
            var raw = originHead.Stdout.Trim();
            var branch = StripOriginPrefix(raw);
            if (!string.IsNullOrWhiteSpace(branch))
                return branch;
        }

        var checkedOut = await TryRunGitAsync(
            commandExecutor,
            workTreeRoot,
            ["symbolic-ref", "--short", "HEAD"],
            cancellationToken);

        if (checkedOut.ExitCode == 0)
        {
            var raw = checkedOut.Stdout.Trim();
            if (!string.IsNullOrWhiteSpace(raw))
                return raw;
        }

        return null;
    }

    private static string StripOriginPrefix(string raw)
    {
        const string prefix = "origin/";
        if (raw.StartsWith(prefix, StringComparison.Ordinal))
            return raw[prefix.Length..];
        return raw;
    }

    private static bool TryCanonicalize(
        string rawPath,
        out string canonical,
        out string error)
    {
        try
        {
            canonical = Path.GetFullPath(rawPath);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            canonical = string.Empty;
            error = $"--path '{rawPath}' could not be canonicalized: {ex.Message}";
            return false;
        }
    }

    private static string TrimLineEnding(string value) => value.TrimEnd('\r', '\n');

    private static async Task<(int ExitCode, string Stdout, string Stderr)> TryRunGitAsync(
        ICommandExecutor commandExecutor,
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var fullArgs = new List<string>(args.Count + 2) { "-C", workingDirectory };
        fullArgs.AddRange(args);
        return await commandExecutor.ExecuteAsync("git", fullArgs.ToArray(), workingDirectory, cancellationToken);
    }
}

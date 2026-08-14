using System.Text.Json;

namespace Mohist.Cli;

internal sealed class UpdateSourceResolver
{
    private readonly ICommandExecutor _commands;
    private readonly IFileSystem _files;
    private readonly Func<string?> _getUserHome;

    public UpdateSourceResolver(
        ICommandExecutor commands,
        IFileSystem files,
        Func<string?>? getUserHome = null)
    {
        _commands = commands;
        _files = files;
        _getUserHome = getUserHome ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public async Task<(UpdateSourceContext? Context, string? Error)> ResolveAsync(
        string? repoRoot,
        string scope,
        string transactionId,
        string? cliPath,
        CancellationToken cancellationToken = default)
    {
        var explicitRoot = !string.IsNullOrWhiteSpace(repoRoot);
        var root = NormalizeRoot(repoRoot);
        if (string.IsNullOrWhiteSpace(root))
            return (null, "repository root is unavailable");

        var sourceRoot = new DirectoryInfo(root).FullName.Replace('\\', '/');
        if (!_files.Exists(Path.Combine(sourceRoot, "Mohist.sln")))
            return (null, $"repository root '{sourceRoot}' does not contain Mohist.sln");

        var (commitCode, commitOutput, commitError) = await _commands.ExecuteAsync(
            "git", ["rev-parse", "HEAD"], sourceRoot, cancellationToken);
        var commit = NormalizeIdentity(commitOutput);
        if (commitCode != 0 || string.IsNullOrWhiteSpace(commit))
            return (null, $"source Git commit could not be determined at '{sourceRoot}'{FormatCommandError(commitError)}");

        var (treeCode, treeOutput, treeError) = await _commands.ExecuteAsync(
            "git", ["rev-parse", "HEAD^{tree}"], sourceRoot, cancellationToken);
        var tree = NormalizeIdentity(treeOutput);
        if (treeCode != 0 || string.IsNullOrWhiteSpace(tree))
            return (null, $"source tree identity could not be determined at '{sourceRoot}'{FormatCommandError(treeError)}");

        var (statusCode, statusOutput, statusError) = await _commands.ExecuteAsync(
            "git", ["status", "--porcelain", "--untracked-files=all"], sourceRoot, cancellationToken);
        if (statusCode != 0)
            return (null, $"source cleanliness could not be determined at '{sourceRoot}'{FormatCommandError(statusError)}");
        if (!string.IsNullOrWhiteSpace(statusOutput))
            return (null, $"source root '{sourceRoot}' is dirty; commit or remove changes before updating");

        var runtimeRoot = ResolveRuntimeRoot(_getUserHome());
        var transactionRoot = Path.Combine(runtimeRoot, "transactions", transactionId).Replace('\\', '/');
        var snapshotRoot = Path.Combine(transactionRoot, "snapshot").Replace('\\', '/');
        var buildRoot = Path.Combine(transactionRoot, "build").Replace('\\', '/');
        var buildSourceRoot = Path.Combine(buildRoot, "source").Replace('\\', '/');
        var candidateRoot = Path.Combine(transactionRoot, "candidate").Replace('\\', '/');

        try
        {
            _files.CreateDirectory(runtimeRoot);
            _files.CreateDirectory(transactionRoot);
            _files.CreateDirectory(snapshotRoot);
            _files.CreateDirectory(buildRoot);
            _files.CreateDirectory(buildSourceRoot);
            _files.CreateDirectory(candidateRoot);
        }
        catch (Exception ex)
        {
            return (null, $"managed update workspace could not be prepared at '{transactionRoot}': {ex.Message}");
        }

        var archivePath = Path.Combine(buildRoot, "source.tar").Replace('\\', '/');
        var (archiveCode, _, archiveError) = await _commands.ExecuteAsync(
            "git",
            ["archive", "--format=tar", $"--output={archivePath}", commit],
            sourceRoot,
            cancellationToken);
        if (archiveCode != 0)
            return (null, $"source snapshot could not be created from commit '{commit}'{FormatCommandError(archiveError)}");

        var (extractCode, _, extractError) = await _commands.ExecuteAsync(
            "tar", ["-xf", archivePath, "-C", snapshotRoot], buildRoot, cancellationToken);
        if (extractCode != 0)
            return (null, $"source snapshot could not be extracted at '{snapshotRoot}'{FormatCommandError(extractError)}");

        var (buildExtractCode, _, buildExtractError) = await _commands.ExecuteAsync(
            "tar", ["-xf", archivePath, "-C", buildSourceRoot], buildRoot, cancellationToken);
        if (buildExtractCode != 0)
            return (null, $"writable source build workspace could not be extracted at '{buildSourceRoot}'{FormatCommandError(buildExtractError)}");

        var marker = new SourceSnapshotMarker(
            sourceRoot,
            commit,
            tree,
            UpdateSourceIdentity.ComputeDigest(commit, tree));
        try
        {
            _files.WriteAllText(
                Path.Combine(snapshotRoot, ".mohist-source-marker.json"),
                JsonSerializer.Serialize(marker, JsonOptions));
        }
        catch (Exception ex)
        {
            return (null, $"source snapshot marker could not be persisted at '{snapshotRoot}': {ex.Message}");
        }

        var (readonlyCode, _, readonlyError) = await _commands.ExecuteAsync(
            "chmod", ["-R", "a-w", snapshotRoot], buildRoot, cancellationToken);
        if (readonlyCode != 0)
            return (null, $"source snapshot could not be made read-only at '{snapshotRoot}'{FormatCommandError(readonlyError)}");

        var source = new UpdateSourceIdentity(
            sourceRoot,
            explicitRoot,
            commit,
            tree,
            marker.SourceDigest);
        var context = new UpdateSourceContext(
            source,
            snapshotRoot,
            buildSourceRoot,
            candidateRoot,
            runtimeRoot,
            transactionId,
            scope,
            string.IsNullOrWhiteSpace(cliPath)
                ? UpdateOperations.ResolveCliWrapperPath(_getUserHome())
                : cliPath);
        return (context, null);
    }

    public async Task<string?> VerifyUnchangedAsync(
        UpdateSourceContext context,
        CancellationToken cancellationToken = default)
    {
        var root = context.Source.RepositoryRoot;
        var (commitCode, commitOutput, _) = await _commands.ExecuteAsync(
            "git", ["rev-parse", "HEAD"], root, cancellationToken);
        var (treeCode, treeOutput, _) = await _commands.ExecuteAsync(
            "git", ["rev-parse", "HEAD^{tree}"], root, cancellationToken);
        var (statusCode, statusOutput, _) = await _commands.ExecuteAsync(
            "git", ["status", "--porcelain", "--untracked-files=all"], root, cancellationToken);

        if (commitCode != 0 || treeCode != 0 || statusCode != 0)
            return "source identity could not be read back after staging";
        if (!string.Equals(NormalizeIdentity(commitOutput), context.Source.GitCommit, StringComparison.Ordinal)
            || !string.Equals(NormalizeIdentity(treeOutput), context.Source.TreeHash, StringComparison.Ordinal))
            return $"source changed after target capture (expected {context.Source.GitCommit}/{context.Source.TreeHash}, observed {NormalizeIdentity(commitOutput)}/{NormalizeIdentity(treeOutput)})";
        if (!string.IsNullOrWhiteSpace(statusOutput))
            return "source became dirty after target capture";
        return null;
    }

    public static string ResolveRuntimeRoot(string? home)
    {
        var resolvedHome = string.IsNullOrWhiteSpace(home)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : home;
        if (OperatingSystem.IsWindows())
        {
            var localAppData = SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData, "Mohist", "runtime").Replace('\\', '/');
            return Path.Combine(resolvedHome, "AppData", "Local", "Mohist", "runtime").Replace('\\', '/');
        }

        return Path.Combine(resolvedHome, ".local", "share", "mohist", "runtime").Replace('\\', '/');
    }

    private string NormalizeRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(explicitRoot).Replace('\\', '/');

        var current = _files.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var directory = new DirectoryInfo(current);
            while (directory is not null)
            {
                if (_files.Exists(Path.Combine(directory.FullName, "Mohist.sln")))
                    return directory.FullName.Replace('\\', '/');
                directory = directory.Parent;
            }
        }

        return string.Empty;
    }

    private static string NormalizeIdentity(string value) => value.Trim().ToLowerInvariant();

    private static string FormatCommandError(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $": {error.Trim()}";

    private sealed record SourceSnapshotMarker(
        string AuthorityRoot,
        string GitCommit,
        string TreeHash,
        string SourceDigest);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

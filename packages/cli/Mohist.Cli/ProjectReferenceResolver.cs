using System.Text.Json;

namespace Mohist.Cli;

internal sealed class ProjectReferenceResolver
{
    private const string StateFileName = "cli-state.json";
    private readonly IFileSystem _fileSystem;
    private readonly Func<string> _getUserHome;

    public ProjectReferenceResolver(IFileSystem fileSystem, Func<string> getUserHome)
    {
        _fileSystem = fileSystem;
        _getUserHome = getUserHome;
    }

    public async Task<Result> ResolveAsync(string? explicitReference)
    {
        if (!string.IsNullOrWhiteSpace(explicitReference))
            return new Result.Resolved(explicitReference.Trim(), "--project");

        var directoryResult = await ReadNearestDirectoryContextAsync().ConfigureAwait(false);
        if (directoryResult is not null)
            return directoryResult;

        var selectedPath = StatePath(_getUserHome());
        if (!_fileSystem.Exists(selectedPath))
            return Result.Missing.Instance;

        return await ReadContextAsync(selectedPath, "the locally selected Project").ConfigureAwait(false);
    }

    public static string StatePath(string root) => Path.Combine(root, ".mohist", StateFileName);

    private async Task<Result?> ReadNearestDirectoryContextAsync()
    {
        var directory = Path.GetFullPath(_fileSystem.CurrentDirectory);
        while (true)
        {
            var path = StatePath(directory);
            if (_fileSystem.Exists(path))
                return await ReadContextAsync(path, $"current-directory context '{path}'").ConfigureAwait(false);

            var parent = Directory.GetParent(directory)?.FullName;
            if (parent is null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                return null;
            directory = parent;
        }
    }

    private async Task<Result> ReadContextAsync(string path, string source)
    {
        try
        {
            using var document = JsonDocument.Parse(await _fileSystem.ReadAllTextAsync(path).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count(property => property.NameEquals("activeProjectId")) != 1
                || !root.TryGetProperty("activeProjectId", out var active)
                || active.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(active.GetString()))
                return new Result.Invalid(source);

            return new Result.Resolved(active.GetString()!.Trim(), source);
        }
        catch (JsonException)
        {
            return new Result.Invalid(source);
        }
        catch (IOException)
        {
            return new Result.Invalid(source);
        }
    }

    internal abstract record Result
    {
        private Result() { }

        public sealed record Resolved(string ProjectReference, string Source) : Result;
        public sealed record Invalid(string Source) : Result;
        public sealed record Missing : Result
        {
            public static readonly Missing Instance = new();
        }

    }
}

namespace Mohist.Server.Workspace.Domain;

public static class WorkspacePolicy
{
    public const int MaxNameLength = 128;

    public sealed record ValidationError(string Code, string Message);

    public static bool TryNormalizeName(string? raw, out string name)
    {
        name = (raw ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameLength)
            return false;
        // The grain key is `{projectId}:{name}`; a separator inside the
        // name would make the key ambiguous.
        return !name.Contains(':');
    }

    public static ValidationError? ValidateCreate(
        string name,
        WorkspaceOrigin origin,
        IReadOnlyCollection<string> repositoryNames,
        IReadOnlyCollection<string> knownRepositoryNames)
    {
        if (!TryNormalizeName(name, out _))
            return new("workspace_name_invalid", $"Workspace name must be a non-empty string of at most {MaxNameLength} characters and must not contain ':'.");

        if (origin is null)
            return new("workspace_origin_required", "Workspace origin is required.");

        var known = new HashSet<string>(knownRepositoryNames, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repositoryNames)
        {
            if (!seen.Add(repo))
                return new("workspace_repository_duplicate", $"Repository '{repo}' is listed more than once.");
            if (!known.Contains(repo))
                return new("workspace_repository_not_found", $"Repository '{repo}' is not declared on the project.");
        }

        return null;
    }

    private static bool TryNormalizeRepositoryName(string? raw, out string name)
    {
        name = (raw ?? string.Empty).Trim();
        return name.Length > 0;
    }

    public static ValidationError? ValidateAddRepository(
        string repoName,
        IReadOnlyCollection<string> current,
        IReadOnlyCollection<string> knownRepositoryNames)
    {
        if (!TryNormalizeRepositoryName(repoName, out var name))
            return new("workspace_repository_required", "Repository name is required.");

        if (current.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            return new("workspace_repository_duplicate", $"Workspace already includes repository '{name}'.");

        if (!knownRepositoryNames.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            return new("workspace_repository_not_found", $"Repository '{name}' is not declared on the project.");

        return null;
    }

    public static ValidationError? ValidateRemoveRepository(string repoName, IReadOnlyCollection<string> current)
    {
        if (!TryNormalizeRepositoryName(repoName, out var name))
            return new("workspace_repository_required", "Repository name is required.");

        if (!current.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            return new("workspace_repository_not_found", $"Workspace does not include repository '{name}'.");

        return null;
    }

    public static bool IsManual(WorkspaceOrigin origin) => origin is WorkspaceOrigin.Manual;
}

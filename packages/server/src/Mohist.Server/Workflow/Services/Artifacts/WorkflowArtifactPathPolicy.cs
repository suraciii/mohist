namespace Mohist.Server.Workflow.Services.Artifacts;

/// <summary>
/// Defines the Workspace-relative path boundary for artifact provisioning.
/// Repository and Runner control trees remain outside the artifact input
/// channel.
/// </summary>
public static class WorkflowArtifactPathPolicy
{
    private static readonly HashSet<string> ReservedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "REPOS",
        ".mohist",
        ".scratch",
        ".mohist-provision-temp",
    };

    public static bool TryNormalizeProvisionPath(
        string? rawPath,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "artifact path is required";
            return false;
        }

        var path = rawPath.Trim().Replace('\\', '/');
        if (path.StartsWith('/', StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/'))
        {
            error = $"artifact path '{rawPath}' must be Workspace-relative";
            return false;
        }

        var parts = path.Split('/');
        if (parts.Length == 0 || parts.Any(part =>
                part.Length == 0
                || part == "."
                || part == ".."
                || part.Any(char.IsControl)))
        {
            error = $"artifact path '{rawPath}' is not a normalized Workspace-relative path";
            return false;
        }

        if (ReservedRoots.Contains(parts[0]))
        {
            error = $"artifact path '{rawPath}' is outside the Workspace artifact boundary";
            return false;
        }

        normalized = string.Join('/', parts);
        return true;
    }
}

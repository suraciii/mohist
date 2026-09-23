namespace Mohist.Server.Runner.Services.WebSocket;

public sealed record RunnerControlHandshake(
    string? BuildGitHash,
    string? Component,
    string? Version,
    string? SourceRevision,
    string? TreeHash,
    string? ArtifactDigest,
    string? ReleaseId,
    long? Generation,
    string? ProcessGeneration,
    int? SchemaVersion = null)
{
    public static RunnerControlHandshake FromQuery(IQueryCollection query) => new(
        Normalize(query["buildGitHash"]),
        Normalize(query["component"]),
        Normalize(query["version"]),
        Normalize(query["sourceRevision"]),
        Normalize(query["treeHash"]),
        Normalize(query["artifactDigest"]),
        Normalize(query["releaseId"]),
        long.TryParse(query["generation"], out var generation) && generation > 0 ? generation : null,
        Exact(query["processGeneration"]),
        ParseSchemaVersion(query));

    // Only an absent schemaVersion is legacy v0. A present value that is not a
    // parseable integer is malformed and is reported as 0 so it never triggers
    // the bounded SourceRevision ?? BuildGitHash fallback.
    private static int? ParseSchemaVersion(IQueryCollection query)
    {
        if (!query.TryGetValue("schemaVersion", out var values))
            return null;
        return int.TryParse(values.ToString(), out var schemaVersion) ? schemaVersion : 0;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Exact(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

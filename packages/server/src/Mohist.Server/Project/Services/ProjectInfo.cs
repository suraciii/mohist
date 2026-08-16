using Mohist.Server.Project.Domain;
using Mohist.Server.Infrastructure;
using System.Text.Json;

namespace Mohist.Server.Project.Services;

[GenerateSerializer]
public class ProjectInfo
{
    [Id(0)] public string Id { get; set; } = null!;
    [Id(1)] public string Name { get; set; } = null!;
    [Id(2)] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [Id(3)] public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [Id(4)] public List<RepositoryInfo> Repositories { get; set; } = [];
    [Id(5)] public ProjectVariablesBag Variables { get; set; } = ProjectVariablesBag.Empty;

    /// <summary>
    /// The Project's default execution configuration (Runtime, Model,
    /// optional Variant) or null when unset. Read by the Project read
    /// surface (<c>defaultExecutionConfig</c>) so Web and CLI can branch
    /// without a second endpoint, and consumed by Readiness / launch
    /// resolution through the DB-backed scoped reader.
    /// </summary>
    [Id(6)] public ExecutionConfigHint? DefaultExecutionConfig { get; set; }

    public RepositoryInfo? DefaultRepository =>
        Repositories.FirstOrDefault(r => r.IsDefault);

    public RepositoryInfo? GetRepository(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DefaultRepository;
        return Repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

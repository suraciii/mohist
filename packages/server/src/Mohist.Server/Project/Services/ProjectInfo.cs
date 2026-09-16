using Mohist.Server.Project.Domain;
using System.Text.Json.Serialization;

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
    /// The Project-owned deterministic verification command. This is a
    /// startup fact for new WorkflowRuns and is intentionally separate from
    /// mutable Project Variables.
    /// </summary>
    [Id(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? VerificationCommand { get; set; }

    public RepositoryInfo? DefaultRepository =>
        Repositories.FirstOrDefault(r => r.IsDefault);

    public RepositoryInfo? GetRepository(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DefaultRepository;
        return Repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

namespace Mohist.Server.Infrastructure.Data.Project;

public class ProjectRow
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string RepositoriesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RepositoryRevision { get; set; }
    public string? LastRepositoryCommandJson { get; set; }

    /// <summary>
    /// Serialized Project default execution configuration
    /// (<c>{ runtime, model, variant? }</c>) or null when no default is
    /// configured. Nullable and additive — deployments without a default
    /// keep null and observe no behavior change.
    /// </summary>
    public string? DefaultExecutionConfigJson { get; set; }
}

namespace Mohist.Server.Project.Queries;

[GenerateSerializer]
public class RepositoryInfo
{
    [Id(0)] public string Name { get; set; } = null!;
    [Id(1)] public string? Path { get; set; }
    [Id(2)] public string? Remote { get; set; }
    [Id(3)] public string BaseBranch { get; set; } = "main";
    [Id(4)] public bool IsDefault { get; set; }

    public string ResolvedPath => Path ?? "";
    public string ResolvedBaseBranch => string.IsNullOrWhiteSpace(BaseBranch) ? "main" : BaseBranch;
}

[GenerateSerializer]
public class ProjectInfo
{
    [Id(0)] public string Id { get; set; } = null!;
    [Id(1)] public string Name { get; set; } = null!;
    [Id(2)] public string Path { get; set; } = null!;
    [Id(3)] public string BaseBranch { get; set; } = "main";
    [Id(4)] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [Id(5)] public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [Id(6)] public List<RepositoryInfo> Repositories { get; set; } = [];

    public RepositoryInfo? DefaultRepository
    {
        get
        {
            if (Repositories.Count == 0) return null;
            return Repositories.FirstOrDefault(r => r.IsDefault) ?? Repositories[0];
        }
    }

    public RepositoryInfo? GetRepository(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DefaultRepository;
        return Repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public string EffectivePath => DefaultRepository?.ResolvedPath ?? Path;
    public string EffectiveBaseBranch => DefaultRepository?.ResolvedBaseBranch ?? BaseBranch;
}

namespace Mohist.Server.Project.Domain;

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

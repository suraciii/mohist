namespace Mohist.Server.Project.Domain;

[GenerateSerializer]
public class RepositoryInfo
{
    [Id(0)] public string Name { get; set; } = null!;
    [Id(1)] public string GitUrl { get; set; } = null!;
    [Id(2)] public string BaseBranch { get; set; } = "main";
    [Id(3)] public bool IsDefault { get; set; }

    public string ResolvedBaseBranch => string.IsNullOrWhiteSpace(BaseBranch) ? "main" : BaseBranch;
}

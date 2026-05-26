namespace Mohist.Server.Project.Queries;

[GenerateSerializer]
public class ProjectInfo
{
    [Id(0)] public string Id { get; set; } = null!;
    [Id(1)] public string Name { get; set; } = null!;
    [Id(2)] public string Path { get; set; } = null!;
    [Id(3)] public string BaseBranch { get; set; } = "main";
    [Id(4)] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [Id(5)] public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}

namespace Mohist.Server.Infrastructure.Data.Label;

public class LabelDefinitionRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string Key { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string SupportedValuesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace Mohist.Server.Infrastructure.Data.Runner;

public class RunnerRow
{
    public string Id { get; set; } = string.Empty;
    public int Slots { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

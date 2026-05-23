namespace Mohist.Server.Storage.Db.Entities;

public class GrainState
{
    public string Key { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string JsonState { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

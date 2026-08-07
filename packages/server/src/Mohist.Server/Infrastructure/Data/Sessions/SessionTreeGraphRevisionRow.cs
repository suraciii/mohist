namespace Mohist.Server.Infrastructure.Data.Sessions;

public class SessionTreeGraphRevisionRow
{
    public string ProjectId { get; set; } = string.Empty;
    public long PublishedRevision { get; set; }
    public string PublishedAt { get; set; } = string.Empty;
}

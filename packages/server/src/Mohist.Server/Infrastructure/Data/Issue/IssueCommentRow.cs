namespace Mohist.Server.Infrastructure.Data.Issue;

public class IssueCommentRow
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public int IssueNumber { get; set; }

    /// <summary>Attribution anchor: the authenticated principal's id
    /// (<c>admin</c> / <c>service</c> / agent principal id).</summary>
    public string? Author { get; set; }

    /// <summary>Display alias supplied by the caller; never the
    /// attribution basis.</summary>
    public string? DisplayName { get; set; }

    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

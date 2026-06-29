using Mohist.Server.Inbox;

namespace Mohist.Server.Api;

/// <summary>
/// HTTP routes for the project-scoped inbox.
///
/// <para>
/// The routes sit under <c>/api/projects/{projectRef}/inbox</c> with the
/// shared <see cref="ProjectResolutionEndpointFilter"/>, mirroring
/// <c>EpicRoutes</c>. The resolver turns <c>projectRef</c> into a
/// <c>ProjectInfo</c>; every read/mutation call is then scoped to the
/// resolved <c>projectId</c>, so a request scoped to project A can never
/// observe or mutate project B's items.
/// </para>
///
/// <para>
/// Mutations (<c>POST /{itemId}/read</c>, <c>POST /read-all</c>,
/// <c>POST /{itemId}/archive</c>) drive their 404 behaviour from the
/// store: <see cref="InboxStore.MarkReadAsync"/> and
/// <see cref="InboxStore.ArchiveAsync"/> filter
/// <c>WHERE ProjectId = @pid AND Id = @itemId</c>, and
/// <see cref="InboxStore.MarkAllReadAsync"/> filters
/// <c>WHERE ProjectId = @pid</c>. Zero affected rows therefore means
/// the item belongs to another project (or does not exist) and the
/// route surfaces that as 404.
/// </para>
/// </summary>
public static class InboxRoutes
{
    public static WebApplication MapInboxRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/inbox")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        // GET /api/projects/{projectRef}/inbox
        group.MapGet("/", async (HttpContext context, InboxQuerier querier) =>
        {
            var pid = context.GetResolvedProject().Id;
            var items = await querier.ListAsync(pid);
            var dto = items.Select(InboxItemDto.FromView).ToArray();
            return ApiResults.Ok(dto);
        });

        // POST /api/projects/{projectRef}/inbox/{itemId}/read
        group.MapPost("/{itemId}/read", async (HttpContext context, string itemId, InboxStore store) =>
        {
            var pid = context.GetResolvedProject().Id;
            var affected = await store.MarkReadAsync(pid, itemId);
            return affected == 0
                ? ApiResults.NotFound($"Inbox item {itemId} not found")
                : ApiResults.Ok(new { itemId, read = true });
        });

        // POST /api/projects/{projectRef}/inbox/read-all
        group.MapPost("/read-all", async (HttpContext context, InboxStore store) =>
        {
            var pid = context.GetResolvedProject().Id;
            var affected = await store.MarkAllReadAsync(pid);
            return ApiResults.Ok(new { projectId = pid, marked = affected });
        });

        // POST /api/projects/{projectRef}/inbox/{itemId}/archive
        group.MapPost("/{itemId}/archive", async (HttpContext context, string itemId, InboxStore store) =>
        {
            var pid = context.GetResolvedProject().Id;
            var affected = await store.ArchiveAsync(pid, itemId);
            return affected == 0
                ? ApiResults.NotFound($"Inbox item {itemId} not found")
                : ApiResults.Ok(new { itemId, archived = true });
        });

        return app;
    }
}

/// <summary>
/// Response shape for an inbox item. The server stores only structured
/// fields (<c>notificationKind</c>, issue identity, timestamps) — the
/// product-facing text is rendered on the Web client from
/// <see cref="NotificationKind"/> and issue identity, per design D7.
/// </summary>
public sealed class InboxItemDto
{
    public string ItemId { get; init; } = "";
    public string NotificationKind { get; init; } = "";
    public string IssueId { get; init; } = "";
    public int IssueNumber { get; init; }
    public string IssueTitle { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReadAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public bool IsRead { get; init; }
    public bool IsArchived { get; init; }

    public static InboxItemDto FromView(InboxItemView view) => new()
    {
        ItemId = view.Id,
        NotificationKind = view.NotificationKind,
        IssueId = view.IssueId,
        IssueNumber = view.IssueNumber,
        IssueTitle = view.IssueTitle,
        CreatedAt = view.CreatedAt,
        ReadAt = view.ReadAt,
        ArchivedAt = view.ArchivedAt,
        IsRead = view.IsRead,
        IsArchived = view.IsArchived,
    };
}
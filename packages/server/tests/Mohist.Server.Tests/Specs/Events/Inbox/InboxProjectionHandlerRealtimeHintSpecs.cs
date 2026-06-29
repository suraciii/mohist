using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Events.Inbox;

/// <summary>
/// Unit specs for the realtime-hint emission added to
/// <see cref="InboxProjectionHandler"/>. Covers the spec requirement
/// "Server emits a project-scoped realtime hint strictly after an inbox
/// item is persisted": exactly one hint per non-duplicate insert, no
/// hint on deduplicated inserts, no hint on insert failure, identity-only
/// payload, projectid extension stamped, and publish failure swallowed.
/// Shared DB / scope / event-builder helpers live in
/// <see cref="InboxProjectionTestSupport"/>.
/// </summary>
public class InboxProjectionHandlerRealtimeHintSpecs
{
    private const string HintType = "com.mohist.inbox.item-persisted";
    private const string HintSource = "/mohist/inbox";

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task NonDuplicateInsert_PublishesExactlyOneHint()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42,
            title: "Issue 42");

        var publisher = new CapturingEventPublisher();
        var handler = InboxProjectionTestSupport.CreateHandler(database, publisher);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42,
            eventId: "evt-hint-once");

        await handler.HandleAsync(evt, CancellationToken.None);

        var hint = Assert.Single(publisher.Published);
        Assert.Equal(HintType, hint.Type);
        Assert.Equal(HintSource, hint.Source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task DeduplicatedInsert_PublishesNoHint()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var publisher = new CapturingEventPublisher();
        var handler = InboxProjectionTestSupport.CreateHandler(database, publisher);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-dedup");

        await handler.HandleAsync(evt, CancellationToken.None);
        // After the first call exactly one hint has been published.
        Assert.Single(publisher.Published);

        // Two more deliveries of the same CloudEvent: insert returns
        // AlreadyExisted, handler must not publish any further hint.
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Single(publisher.Published);
        // The single inbox row is still there, untouched.
        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal("evt-dedup", item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task FailedInsert_PublishesNoHint()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var publisher = new CapturingEventPublisher();
        // Replace the DI DbContextFactory with one that throws on
        // CreateDbContextAsync so InboxStore.InsertAsync fails before it
        // can persist a row — this exercises the "no hint before
        // persistence" ordering invariant.
        var handler = InboxProjectionTestSupport.CreateHandler(
            database,
            publisher,
            configureServices: services =>
            {
                var existing = services.Single(d => d.ServiceType == typeof(IDbContextFactory<MohistDbContext>));
                services.Remove(existing);
                services.AddSingleton<IDbContextFactory<MohistDbContext>>(new ThrowingDbContextFactory());
            });
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-insert-fails");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(publisher.Published);
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Hint_PayloadContainsOnlyIdentity()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_42",
            issueNumber: 42,
            title: "Approval target");

        var publisher = new CapturingEventPublisher();
        var handler = InboxProjectionTestSupport.CreateHandler(database, publisher);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_42",
            issueNumber: 42,
            eventId: "evt-identity");

        await handler.HandleAsync(evt, CancellationToken.None);

        var hint = Assert.Single(publisher.Published);
        var data = Assert.IsType<JsonElement>(hint.Data);
        // Strict identity-only payload: exactly five fields, no inbox
        // state, no source event details, no body / text / read state.
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        var propertyNames = data.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "itemId", "projectId", "kind", "issueId", "issueNumber",
        }, propertyNames);
        Assert.Equal(NotificationKinds.IssueStarted, data.GetProperty("kind").GetString());
        Assert.Equal("proj_a", data.GetProperty("projectId").GetString());
        Assert.Equal("issue_42", data.GetProperty("issueId").GetString());
        Assert.Equal(42, data.GetProperty("issueNumber").GetInt32());
        // itemId matches the row that was persisted (i.e. is the new
        // inbox item's id, not the source event id).
        var row = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(row.Id, data.GetProperty("itemId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Hint_CarriesProjectIdExtension()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_x",
            issueId: "issue_x",
            issueNumber: 1,
            title: "X");

        var publisher = new CapturingEventPublisher();
        var handler = InboxProjectionTestSupport.CreateHandler(database, publisher);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_x",
            issueId: "issue_x",
            issueNumber: 1,
            eventId: "evt-projectid");

        await handler.HandleAsync(evt, CancellationToken.None);

        var hint = Assert.Single(publisher.Published);
        Assert.NotNull(hint.Extensions);
        Assert.Equal("proj_x", hint.Extensions!["projectid"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Hint_PublishException_SwallowedAndDoesNotBreakProjection()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(
            database,
            eventPublisher: new ThrowingEventPublisher(),
            configureServices: null);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-publish-fails");

        // The handler must swallow the publish failure; the source-event
        // projection (the inbox row) must still be created.
        await handler.HandleAsync(evt, CancellationToken.None);

        var row = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal("evt-publish-fails", row.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Hint_EmittedForEveryProjectScopedNotificationKind()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_failed",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_approval",
            projectId: "proj_a",
            issueId: "issue_2",
            issueNumber: 2);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Failed issue");
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_2",
            issueNumber: 2,
            title: "Approval issue");

        var publisher = new CapturingEventPublisher();
        var handler = InboxProjectionTestSupport.CreateHandler(database, publisher);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "wf_failed", "evt-f"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "wf_approval", "evt-a"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "proj_a", "issue_1", 1, "evt-s"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkCompleted, "proj_a", "issue_2", 2, "evt-c"), CancellationToken.None);

        Assert.Equal(4, publisher.Published.Count);
        var kinds = publisher.Published
            .Select(p => p.Data!.Value.GetProperty("kind").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            NotificationKinds.WorkflowFailed,
            NotificationKinds.ApprovalRequested,
            NotificationKinds.IssueStarted,
            NotificationKinds.IssueCompleted,
        }, kinds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task DisabledKind_PublishesNoHint()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42,
            title: "No hint when disabled");
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            issueStartedEnabled: false);

        var publisher = new CapturingEventPublisher();
        var handler = InboxProjectionTestSupport.CreateHandler(database, publisher);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42,
            eventId: "evt-no-hint-disabled");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(publisher.Published);
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Hint_ProjectIdExtensionMatchesOwningProjectNotSourceRoute()
    {
        // Defends against accidental leak: the projectid stamped on the
        // hint must be the project that owns the inbox item, not the
        // source URL of the source CloudEvent.
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_owned_by_b",
            projectId: "proj_b",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_b",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Owned by B");

        var publisher = new CapturingEventPublisher();
        var handler = InboxProjectionTestSupport.CreateHandler(database, publisher);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_owned_by_b",
            eventId: "evt-b");

        await handler.HandleAsync(evt, CancellationToken.None);

        var hint = Assert.Single(publisher.Published);
        Assert.Equal("proj_b", hint.Extensions!["projectid"]);
        Assert.Equal("proj_b", hint.Data!.Value.GetProperty("projectId").GetString());
    }

    private sealed class CapturingEventPublisher : IEventPublisher
    {
        private readonly List<RecordedPublish> _published = [];

        public IReadOnlyList<RecordedPublish> Published => _published.ToArray();

        public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
        {
            _published.Add(new RecordedPublish(
                envelope.Type,
                envelope.Source.ToString(),
                envelope.Subject,
                envelope.Extensions.Count == 0 ? null : new Dictionary<string, string>(envelope.Extensions),
                envelope.Data));
            return Task.CompletedTask;
        }

        public Task PublishAsync<TData>(
            TData data,
            string type,
            string source,
            string? subject = null,
            IReadOnlyDictionary<string, string>? extensions = null,
            CancellationToken ct = default)
        {
            JsonElement? element = data is not null
                ? JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions)
                : null;
            _published.Add(new RecordedPublish(
                type,
                source,
                subject,
                extensions is null ? null : new Dictionary<string, string>(extensions),
                element));
            return Task.CompletedTask;
        }

        public sealed record RecordedPublish(
            string Type,
            string Source,
            string? Subject,
            IReadOnlyDictionary<string, string>? Extensions,
            JsonElement? Data);
    }

    private sealed class ThrowingEventPublisher : IEventPublisher
    {
        public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated hint-publish failure");

        public Task PublishAsync<TData>(
            TData data,
            string type,
            string source,
            string? subject = null,
            IReadOnlyDictionary<string, string>? extensions = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated hint-publish failure");
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public DbContextOptions<MohistDbContext> Options => throw new NotSupportedException();

        public MohistDbContext CreateDbContext() =>
            throw new InvalidOperationException("simulated insert failure");

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated insert failure");
    }
}

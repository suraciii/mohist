using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Epic.Subscriptions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using System.Text.Json;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Events;

public class EpicAutoDonePrerequisiteHandlerSpecs : EpicAutoDoneHandlerTestSupport
{
    [Fact]
    public async Task HandleAsync_ExternalPrerequisiteCompletes_DispatchesToDependentEpic()
    {
        // An external prerequisite (issue 10) is NOT a member of the epic,
        // but issue 2 (a member) depends on it. When issue 10 completes,
        // the handler must reverse-look-up the dependent epic and recompute.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        // Member issue 2 depends on external prerequisite 10
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        // External prerequisite issue 10 — not linked to the epic
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        // Issue 10 completes — it has no direct membership, but the
        // prerequisite reverse lookup should find epic 1 through Issue 2.
        var evt = BuildCompletedEvent(projectId: "project_1", issueId: "issue_10");
        evt = new CloudEvent<IssueCompleted>(
            id: evt.Id,
            source: evt.Source,
            type: evt.Type,
            time: evt.Time,
            data: evt.Data,
            subject: "10",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project_1",
                [EventCatalog.Lineage.Issue] = "10",
            });
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

    [Fact]
    public async Task HandleAsync_ExternalPrerequisiteCompletes_DispatchesToDependentEpic_ViaUnifiedIssueKey()
    {
        // Post-change row stamped with the unified `issue` key. The
        // dispatcher's prerequisite reverse lookup must read `issue`
        // and dispatch the dependent epic.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "10",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
                [EventCatalog.Lineage.Issue] = "10",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

    [Fact]
    public async Task HandleAsync_ExternalPrerequisiteCompletes_MissingIssue_NoOps()
    {
        // The canonical envelope always carries `issue`; without it,
        // this handler cannot identify the completed Issue.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "10",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task HandleAsync_ExtraExtension_DoesNotChangeUnifiedIssueRouting()
    {
        // Unknown extensions cannot alter routing from the canonical
        // `issue` context.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "10",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
                [EventCatalog.Lineage.Issue] = "10",
                ["ignored"] = "999",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        // The canonical issue is 10, so the dependent epic is dispatched.
        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

    [Fact]
    public async Task HandleAsync_MissingIssue_NoOps()
    {
        // The envelope must identify its Issue before any owning-epic or
        // prerequisite lookup can run.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "1",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task CancelledHandler_ExternalCancelledPrerequisite_DoesNotDispatchDependentEpic()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);
        var evt = new CloudEvent<IssueCancelled>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issues/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: EventTime,
            data: new IssueCancelled("cancelled"),
            subject: "10",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project_1",
                [EventCatalog.Lineage.Issue] = "10",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    // --- Fix D: EpicStartRetryHandler (start-attempt-failed triggers recompute) ---

    [Fact]
    public async Task StartRetryHandler_HasSubscriptionAttributeOnStartAttemptFailedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicStartRetryHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.EpicStartAttemptFailed, attr!.Type);
    }

    [Fact]
    public async Task StartRetryHandler_StartAttemptFailedEvent_InvokesRecomputeOnOwningEpic()
    {
        var grains = new TestEpicGrainFactory(CreateDatabase().Factory);
        var handler = new EpicStartRetryHandler(grains, NullLogger<EpicStartRetryHandler>.Instance);

        var evt = BuildStartAttemptFailedEvent(projectId: "project_1", epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

}

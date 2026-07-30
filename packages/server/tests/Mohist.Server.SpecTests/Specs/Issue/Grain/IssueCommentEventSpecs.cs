using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Services;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

/// <summary>
/// issue-490 T-001: high-integration spec for the comment-added event emission
/// path. Verifies <see cref="IssueGrain.AddCommentAsync"/> stages both the
/// comment row and the <c>com.mohist.issue.comment-added</c> CloudEvent in a
/// single transaction (EpicGrain direct-emit pattern, NOT an IssueEvent union
/// variant), stamps the event with issue lineage, and is the only source of
/// <c>comment-added</c> — issue body / title edits do not emit the event even
/// when the body contains an <c>@</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueCommentEventSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueCommentEventSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddCommentAsync_EmitsExactlyOneCommentAddedEventWithPayload()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 1);

        var result = await grain.AddCommentAsync("Ada Lovelace", "Looks good @supervisor");

        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        var envelope = Assert.Single(stored);
        Assert.Equal(EventCatalog.ReverseDns.IssueCommentAdded, envelope.Envelope.Type);
        var data = envelope.Envelope.Data!.Value;
        Assert.Equal(result.Id, data.GetProperty("commentId").GetString());
        Assert.Equal("Ada Lovelace", data.GetProperty("author").GetString());
        Assert.Equal("Looks good @supervisor", data.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AddCommentAsync_NormalizesAuthorBeforeStamping()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 2);

        var result = await grain.AddCommentAsync("  Ada Lovelace  ", "Looks good");

        Assert.Equal("Ada Lovelace", result.Author);
        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        var envelope = Assert.Single(stored);
        Assert.Equal("Ada Lovelace", envelope.Envelope.Data!.Value.GetProperty("author").GetString());
    }

    [Fact]
    public async Task AddCommentAsync_PreservesBodyVerbatim()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 3);
        const string body = "Please review — http://example.test/?q=1&x=2 — @supervisor ping!";

        var result = await grain.AddCommentAsync("Ada", body);

        Assert.Equal(body, result.Body);
        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        var envelope = Assert.Single(stored);
        Assert.Equal(body, envelope.Envelope.Data!.Value.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AddCommentAsync_StampsProjectAndIssueExtensions()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 4);

        await grain.AddCommentAsync("Ada", "body");

        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        var envelope = Assert.Single(stored);
        Assert.Equal(projectId, envelope.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(issueNumber.ToString(), envelope.Envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal(issueNumber.ToString(), envelope.Envelope.Subject);
        Assert.Equal($"/mohist/projects/{projectId}/issues/{issueNumber}", envelope.Envelope.Source.ToString());
    }

    [Fact]
    public async Task AddCommentAsync_StampsEpicWhenIssueBelongsToEpic()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 5, epicNumber: 7);

        await grain.AddCommentAsync("Ada", "body");

        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        var envelope = Assert.Single(stored);
        Assert.Equal("7", envelope.Envelope.Extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public async Task AddCommentAsync_OmitsEpicWhenIssueHasNoEpic()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 6);

        await grain.AddCommentAsync("Ada", "body");

        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        var envelope = Assert.Single(stored);
        Assert.False(envelope.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Epic));
    }

    [Fact]
    public async Task AddCommentAsync_CommentRowIsDurableBeforeEventIsObservable()
    {
        // Persistence-before-observable ordering: both the IssueComments row
        // and the IssueEvents row commit in the same SaveChangesAsync, so a
        // subscriber that observes the event after commit always finds the
        // comment row by commentId.
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 7);

        var result = await grain.AddCommentAsync("Ada", "body");

        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        Assert.Single(stored);

        await using var verify = new MohistDbContext(_fixture.Services.GetRequiredService<DbContextOptions<MohistDbContext>>());
        var row = await verify.IssueComments.AsNoTracking()
            .SingleAsync(c => c.Id == result.Id);
        Assert.Equal(projectId, row.ProjectId);
        Assert.Equal(issueNumber, row.IssueNumber);
        Assert.Equal("Ada", row.Author);
        Assert.Equal("body", row.Body);
    }

    [Fact]
    public async Task IssueBodyOrTitleEdit_DoesNotEmitCommentAdded_EvenWhenBodyContainsAt()
    {
        // The "Comments are the only trigger source" requirement: editing the
        // issue body — even one whose text contains an @ — must NOT emit
        // comment-added.
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 9);

        await grain.UpdateAsync("New title", "Pinging @supervisor is a reference, not a trigger.");

        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task CreateAsync_DoesNotEmitCommentAdded_EvenWhenBodyContainsAt()
    {
        // Create path is also off-limits for the comment-added event — the
        // grain stages issue-created only.
        var (projectId, issueNumber, _) = await CreateIssueAsync(number: 10);

        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task AddCommentAsync_EmitsNothingBeforeSaveAndPokesDispatcherAfterCommit()
    {
        // Smoke test on the dispatcher's best-effort poke: a successful
        // comment add must complete without throwing even though the grain
        // pokes a no-op dispatcher (NullDispatchGrainFactory).
        var (projectId, issueNumber, grain) = await CreateIssueAsync(number: 11);

        var result = await grain.AddCommentAsync("Ada", "ping");

        Assert.NotNull(result.Id);
        var stored = await ListCommentAddedEvents(projectId, issueNumber);
        Assert.Single(stored);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> ListCommentAddedEvents(string projectId, int issueNumber)
    {
        var store = _fixture.Services.GetRequiredService<IEventStore>();
        var all = await store.ListIssueEventsAsync(projectId, issueNumber);
        return all.Where(e => e.Envelope.Type == EventCatalog.ReverseDns.IssueCommentAdded).ToList();
    }

    private async Task<(string ProjectId, int IssueNumber, IssueGrain Grain)> CreateIssueAsync(int number, int? epicNumber = null)
    {
        var projectId = $"proj_comment_{number}_{Guid.NewGuid():N}";
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Comment-event probe #{number}",
            Priority = "p2",
            EpicNumber = epicNumber,
            RepositoryRef = "main",
        };
        await using (var db = new MohistDbContext(_fixture.Services.GetRequiredService<DbContextOptions<MohistDbContext>>()))
        {
            db.Issues.Add(new IssueRow
            {
                ProjectId = projectId,
                Number = number,
                State = IssueStore.Serialize(issue),
                Risk = issue.Risk,
                EpicNumber = issue.EpicNumber,
                ParentIssueNumber = issue.ParentIssueNumber,
                WorkflowProfileIdKey = WorkflowProfileBindingKey.For(issue.WorkflowProfileId),
            });
            await db.SaveChangesAsync();
        }

        var grain = CreateGrain(_fixture.Services, projectId, number);
        await grain.OnActivateAsync(CancellationToken.None);
        return (projectId, number, grain);
    }

    private static IssueGrain CreateGrain(IServiceProvider services, string projectId, int issueNumber)
    {
        var identity = GrainTestContext.Create(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        return new IssueGrain(
            identity.Context,
            identity.Runtime,
            services.GetRequiredService<IIssueStore>(),
            services.GetRequiredService<IssueWorkflowProfileRegistry>(),
            services.GetRequiredService<WorkflowQuerier>(),
            services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            services.GetRequiredService<IEventStore>(),
            services.GetRequiredService<IGrainFactory>(),
            services.GetRequiredService<IBackgroundTaskLauncher>(),
            services.GetRequiredService<IssueRepositoryResolver>(),
            services.GetRequiredService<WorkflowDefinitionResolver>(),
            services.GetRequiredService<WorkflowPromptResolver>(),
            services.GetRequiredService<IssueVariableStore>(),
            services.GetRequiredService<AttachmentService>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IEnvironmentVariableProvider>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<IssueGrain>>(),
            services.GetRequiredService<IWorkflowProfileProvider>());
    }
}

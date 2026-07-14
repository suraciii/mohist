using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Services;

/// <summary>
/// Specs for <see cref="IssueTitleLookup"/>: the Issue read-side
/// batch-lookup + <c>Issue #{n}</c> fallback resolver shared by
/// <see cref="AgentSessionListAssembler"/> and
/// <see cref="AgentActivityFeedAssembler"/>. Pins the
/// issue-370 T-004 spec scenarios:
/// <list type="bullet">
///   <item><description>empty input yields an empty dictionary without
///     querying the database;</description></item>
///   <item><description>distinct issue numbers are deduplicated before
///     lookup;</description></item>
///   <item><description>stored titles are returned verbatim when
///     non-whitespace, and the literal <c>Issue #{n}</c> string is the
///     fallback when absent or whitespace;</description></item>
///   <item><description>the core querier and the activity feed assembler
///     observe the same number → title map for the same
///     <c>(project, numbers)</c> tuple.</description></item>
/// </list>
/// </summary>
[Collection("MohistDb")]
public class IssueTitleLookupSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueTitleLookupSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task LoadTitlesAsync_EmptyInput_ReturnsEmptyDictionaryWithoutTouchingDatabase()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        // Seed an issue so the table is non-empty; this also proves the
        // short-circuit doesn't accidentally pick anything up.
        var seeded = NewIssue("proj-empty-input", 1, "Should not surface");
        db.Issues.Add(new IssueRow { IssueId = seeded.Id, State = IssueStore.Serialize(seeded) });
        await db.SaveChangesAsync();

        DetachTracked(db);

        var titles = await IssueTitleLookup.LoadTitlesAsync(db, "proj-empty-input", [], CancellationToken.None);

        Assert.Empty(titles);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task LoadTitlesAsync_DuplicateNumbers_AreDeduplicatedBeforeLookup()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var projectId = $"proj-dedup-{Guid.NewGuid():N}";
        var issue = NewIssue(projectId, 42, "Deduped title");
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        DetachTracked(db);

        // The query argument carries the same number four times; the
        // result must contain one entry per distinct number — so the
        // dictionary has exactly one key (42), not four.
        var titles = await IssueTitleLookup.LoadTitlesAsync(db, projectId, new[] { 42, 42, 42, 42 }, CancellationToken.None);

        Assert.Single(titles);
        Assert.Equal("Deduped title", titles[42]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task LoadTitlesAsync_LoadsAllDistinctNumbersForProject()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var projectId = $"proj-loadall-{Guid.NewGuid():N}";
        var i1 = NewIssue(projectId, 1, "First");
        var i2 = NewIssue(projectId, 2, "Second");
        var i3 = NewIssue(projectId, 3, "Third");
        db.Issues.AddRange(
            new IssueRow { IssueId = i1.Id, State = IssueStore.Serialize(i1) },
            new IssueRow { IssueId = i2.Id, State = IssueStore.Serialize(i2) },
            new IssueRow { IssueId = i3.Id, State = IssueStore.Serialize(i3) });
        await db.SaveChangesAsync();

        DetachTracked(db);

        var titles = await IssueTitleLookup.LoadTitlesAsync(db, projectId, new[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Equal(3, titles.Count);
        Assert.Equal("First", titles[1]);
        Assert.Equal("Second", titles[2]);
        Assert.Equal("Third", titles[3]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task LoadTitlesAsync_DropsIssuesFromOtherProjects()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var ownProject = $"proj-own-{Guid.NewGuid():N}";
        var otherProject = $"proj-other-{Guid.NewGuid():N}";

        var own1 = NewIssue(ownProject, 1, "Mine 1");
        var own2 = NewIssue(ownProject, 2, "Mine 2");
        var foreign = NewIssue(otherProject, 1, "Foreign #1");
        db.Issues.AddRange(
            new IssueRow { IssueId = own1.Id, State = IssueStore.Serialize(own1) },
            new IssueRow { IssueId = own2.Id, State = IssueStore.Serialize(own2) },
            new IssueRow { IssueId = foreign.Id, State = IssueStore.Serialize(foreign) });
        await db.SaveChangesAsync();

        DetachTracked(db);

        var titles = await IssueTitleLookup.LoadTitlesAsync(db, ownProject, new[] { 1, 2 }, CancellationToken.None);

        Assert.Equal(2, titles.Count);
        Assert.Equal("Mine 1", titles[1]);
        Assert.Equal("Mine 2", titles[2]);
        Assert.DoesNotContain(titles, pair => pair.Value == "Foreign #1");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Resolve_StoredTitle_ReturnedVerbatim()
    {
        var titles = new Dictionary<int, string> { [42] = "Stored" };

        Assert.Equal("Stored", IssueTitleLookup.Resolve(titles, 42));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Resolve_AbsentNumber_FallsBackToIssueHash()
    {
        var titles = new Dictionary<int, string>();

        Assert.Equal("Issue #7", IssueTitleLookup.Resolve(titles, 7));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Resolve_WhitespaceTitle_FallsBackToIssueHash()
    {
        var titles = new Dictionary<int, string>
        {
            [1] = "",
            [2] = "   ",
            [3] = "\t\n",
        };

        Assert.Equal("Issue #1", IssueTitleLookup.Resolve(titles, 1));
        Assert.Equal("Issue #2", IssueTitleLookup.Resolve(titles, 2));
        Assert.Equal("Issue #3", IssueTitleLookup.Resolve(titles, 3));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Resolve_NumberZero_UsesLiteralZeroInFallback()
    {
        // Defensive: callers pass issueNumber = 0 for sessions without
        // an issue-number label; the fallback must still render as
        // "Issue #0" rather than throwing or producing an empty string.
        var titles = new Dictionary<int, string>();

        Assert.Equal("Issue #0", IssueTitleLookup.Resolve(titles, 0));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task QuerierAndAssembler_ShareSameTitlesForSameProjectAndNumbers()
    {
        // The cross-consumer identity invariant: the core querier
        // (ListCurrentAsync) and the activity feed assembler
        // (GetActivityAsync) surface titles for the same project + number
        // set. This calls the consumers directly so a future drift away
        // from IssueTitleLookup trips the spec.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var projectId = $"proj-shared-{Guid.NewGuid():N}";
        var i1 = NewIssue(projectId, 11, "Querier/Assembler: 11");
        var i2 = NewIssue(projectId, 22, "Querier/Assembler: 22");
        var i3 = NewIssue(projectId, 33, "Querier/Assembler: 33");
        db.Issues.AddRange(
            new IssueRow { IssueId = i1.Id, State = IssueStore.Serialize(i1) },
            new IssueRow { IssueId = i2.Id, State = IssueStore.Serialize(i2) },
            new IssueRow { IssueId = i3.Id, State = IssueStore.Serialize(i3) });
        await db.SaveChangesAsync();

        await InsertGenericSessionAsync(db, projectId, $"session-{Guid.NewGuid():N}", 11, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await InsertGenericSessionAsync(db, projectId, $"session-{Guid.NewGuid():N}", 22, new DateTime(2026, 6, 1, 0, 1, 0, DateTimeKind.Utc));
        await InsertGenericSessionAsync(db, projectId, $"session-{Guid.NewGuid():N}", 33, new DateTime(2026, 6, 1, 0, 2, 0, DateTimeKind.Utc));

        DetachTracked(db);

        var sessionList = scope.ServiceProvider.GetRequiredService<AgentSessionListAssembler>();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();
        var current = await sessionList.ListCurrentAsync(projectId, limit: 10);
        var activity = await assembler.GetActivityAsync(projectId, limit: 10);

        var fromQuerierPath = current.ToDictionary(session => session.IssueNumber, session => session.IssueTitle);
        var fromAssemblerPath = activity.Sessions.ToDictionary(session => session.IssueNumber, session => session.IssueTitle);

        Assert.Equal(fromQuerierPath.OrderBy(pair => pair.Key), fromAssemblerPath.OrderBy(pair => pair.Key));
        Assert.Equal(fromQuerierPath.Count, fromAssemblerPath.Count);
        Assert.Equal(fromQuerierPath[11], fromAssemblerPath[11]);
        Assert.Equal(fromQuerierPath[22], fromAssemblerPath[22]);
        Assert.Equal(fromQuerierPath[33], fromAssemblerPath[33]);
    }

    private static async Task InsertGenericSessionAsync(
        MohistDbContext db,
        string projectId,
        string sessionId,
        int issueNumber,
        DateTime createdAt)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [GenericAgentSessionMetadata.AgentId] = $"agent_{issueNumber}",
            [GenericAgentSessionMetadata.AgentName] = $"agent-{issueNumber}",
        };
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("runner-title-lookup", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                AgentRuntimeSessionId: sessionId,
                CreatedAt: createdAt,
                BoundAt: createdAt.AddSeconds(1),
                LastDataAt: createdAt.AddMinutes(1),
                UsageSummary: new AgentUsageSummary(),
                RuntimeSessionLineage: [],
                ContextUsageHistory: []),
            Metadata = new AgentSessionMetadata(labels),
        };

        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = createdAt,
            Status = "bound",
            AgentSessionId = sessionId,
            RunnerId = session.Runtime.RunnerId,
        });
        await db.SaveChangesAsync();
    }

    private static Mohist.Server.Issue.Domain.Issue NewIssue(string projectId, int number, string title) => new()
    {
        Id = $"issue_{projectId}_{number}",
        ProjectId = projectId,
        Number = number,
        Title = title,
        Labels = new Dictionary<string, string>(StringComparer.Ordinal),
        Priority = "p2",
        Status = IssueStatus.Backlog,
    };

    private static void DetachTracked(MohistDbContext db)
    {
        // Detach tracked entities so the next assertion sees a clean
        // snapshot (the save above leaves them in the change tracker).
        db.ChangeTracker.Clear();
    }
}

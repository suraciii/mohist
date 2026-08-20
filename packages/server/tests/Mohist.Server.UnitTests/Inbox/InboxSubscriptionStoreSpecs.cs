using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Inbox;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Inbox;

/// <summary>
/// Calculation specs for the inbox subscription store behind
/// <c>/api/projects/&#123;projectRef&#125;/inbox/subscription</c>: the
/// all-five-enabled default, the put-then-reread persistence round-trip,
/// and project isolation. Runs against MohistDbFixture without an HTTP
/// round-trip. The route contract (400 unknown/missing/non-object/non-bool
/// body, 404 unknown project) stays in <c>InboxSubscriptionApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class InboxSubscriptionStoreSpecs
{
    private readonly MohistDbFixture _fixture;

    public InboxSubscriptionStoreSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private InboxSubscriptionStore CreateStore() =>
        _fixture.Services.GetRequiredService<InboxSubscriptionStore>();

    private async Task<string> CreateProjectAsync()
    {
        var projectId = $"proj-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var now = TestTime.UtcNow;
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    [Fact]
    public async Task GetAsync_NoStoredPreferences_ReturnsAllFiveEnabled()
    {
        var store = CreateStore();

        var state = await store.GetAsync($"proj-{Guid.NewGuid():N}");

        Assert.True(state.WorkflowFailedEnabled);
        Assert.True(state.ApprovalRequestedEnabled);
        Assert.True(state.IssueStartedEnabled);
        Assert.True(state.IssueCompletedEnabled);
        Assert.True(state.AgentResponseFailedEnabled);
    }

    [Fact]
    public async Task SetAsync_PersistsAndIsReadableOnNextGet()
    {
        var store = CreateStore();
        var projectId = await CreateProjectAsync();

        await store.SetAsync(projectId, new InboxSubscriptionState(
            WorkflowFailedEnabled: true,
            ApprovalRequestedEnabled: false,
            IssueStartedEnabled: true,
            IssueCompletedEnabled: false,
            AgentResponseFailedEnabled: false));

        var state = await store.GetAsync(projectId);

        Assert.True(state.WorkflowFailedEnabled);
        Assert.False(state.ApprovalRequestedEnabled);
        Assert.True(state.IssueStartedEnabled);
        Assert.False(state.IssueCompletedEnabled);
        Assert.False(state.AgentResponseFailedEnabled);
    }

    [Fact]
    public async Task SetAsync_IsScopedByProject()
    {
        var store = CreateStore();
        var projectA = await CreateProjectAsync();
        var projectB = await CreateProjectAsync();

        await store.SetAsync(projectA, new InboxSubscriptionState(
            WorkflowFailedEnabled: true,
            ApprovalRequestedEnabled: false,
            IssueStartedEnabled: false,
            IssueCompletedEnabled: true,
            AgentResponseFailedEnabled: false));

        var stateB = await store.GetAsync(projectB);

        Assert.True(stateB.WorkflowFailedEnabled);
        Assert.True(stateB.ApprovalRequestedEnabled);
        Assert.True(stateB.IssueStartedEnabled);
        Assert.True(stateB.IssueCompletedEnabled);
        Assert.True(stateB.AgentResponseFailedEnabled);
    }
}

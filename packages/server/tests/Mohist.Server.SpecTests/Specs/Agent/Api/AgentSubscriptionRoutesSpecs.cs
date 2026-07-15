using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentSubscriptionRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentSubscriptionRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_PersistsActiveSubscriptionAndReturnsIdentity()
    {
        var project = await CreateProjectAsync("subs-create");
        var agent = await CreateAgentAsync(project.Id, "watcher");

        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "approval-watch"));

        Assert.StartsWith("subs_", created.Id);
        Assert.Equal(project.Id, created.ProjectId);
        Assert.Equal(agent.Id, created.AgentId);
        Assert.Equal("approval-watch", created.Name);
        Assert.Equal("com.mohist.workflow.stage.*", created.Filter.Type);
        Assert.Equal("/mohist/workflow-runs/run_abc", created.Filter.Source);
        Assert.Equal("review and approve", created.ResponsePrompt);
        Assert.Equal(0, created.Priority);
        Assert.Equal("active", created.Status);
        Assert.False(string.IsNullOrWhiteSpace(created.CreatedAt));
        Assert.False(string.IsNullOrWhiteSpace(created.UpdatedAt));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_AcceptsMissingFilterSourceAndSubjectAsNullConstraint()
    {
        var project = await CreateProjectAsync("subs-source");
        var agent = await CreateAgentAsync(project.Id, "broad");

        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            new
            {
                name = "broad-fallback",
                filter = new { type = "com.mohist.workflow.stage.*" },
                responsePrompt = "review",
            });

        Assert.Null(created.Filter.Source);
        Assert.Null(created.Filter.Subject);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_PriorityOptional_AbsentStoredAsNull()
    {
        var project = await CreateProjectAsync("subs-default-priority");
        var agent = await CreateAgentAsync(project.Id, "default-priority");

        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            new
            {
                name = "no-priority",
                filter = new { type = "com.mohist.workflow.stage.*" },
                responsePrompt = "review",
            });

        Assert.Null(created.Priority);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_RequiredFields_RejectsMissingNameFilterTypeResponsePrompt()
    {
        var project = await CreateProjectAsync("subs-required");
        var agent = await CreateAgentAsync(project.Id, "required");

        using var missingName = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            new { filter = new { type = "x" }, responsePrompt = "p" });
        using var missingFilterType = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            new { name = "a", responsePrompt = "p", filter = new { type = (string?)null } });
        using var missingPrompt = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            new { name = "a", filter = new { type = "x" } });

        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingFilterType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingPrompt.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_DuplicateNameOnSameAgent_Returns409AndLeavesOriginal()
    {
        var project = await CreateProjectAsync("subs-conflict");
        var agent = await CreateAgentAsync(project.Id, "conflict-agent");
        var original = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "shared"));

        using var duplicate = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "shared"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var stillThere = await _client.GetDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{original.Id}");
        Assert.Equal(original.Id, stillThere.Id);
        Assert.Equal("shared", stillThere.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_UnknownAgent_Returns404()
    {
        var project = await CreateProjectAsync("subs-unknown-agent");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/agent_{Guid.NewGuid():N}/subscriptions",
            NewSubscription(name: "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_UnknownProject_Returns404()
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{Guid.NewGuid():N}/agents/agent_x/subscriptions",
            NewSubscription(name: "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_ArchivedAgent_Returns409WithAgentArchivedCode()
    {
        var project = await CreateProjectAsync("subs-agent-archived");
        var agent = await CreateAgentAsync(project.Id, "archived-agent");
        await _fixture.Client.DeleteAsync($"/api/projects/{project.Id}/agents/{agent.Id}");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "blocked"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_archived", body.GetProperty("code").GetString());

        using var listAfter = await _client.GetAsync($"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions");
        var listed = await listAfter.ReadDataAsync<SubscriptionDto[]>();
        Assert.Empty(listed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Create_SameNameOnDifferentAgents_BothPersist()
    {
        var project = await CreateProjectAsync("subs-multi-agent");
        var agentA = await CreateAgentAsync(project.Id, "agent-a");
        var agentB = await CreateAgentAsync(project.Id, "agent-b");

        var onA = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agentA.Id}/subscriptions",
            NewSubscription(name: "shared"));
        var onB = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agentB.Id}/subscriptions",
            NewSubscription(name: "shared"));

        Assert.NotEqual(onA.Id, onB.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Create_ResolvesAgentByName()
    {
        var project = await CreateProjectAsync("subs-name-resolve");
        var agent = await CreateAgentAsync(project.Id, "named-resolver");

        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/named-resolver/subscriptions",
            NewSubscription(name: "by-name"));

        Assert.Equal(agent.Id, created.AgentId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task List_ReturnsAllSubscriptionsIncludingArchived()
    {
        var project = await CreateProjectAsync("subs-list");
        var agent = await CreateAgentAsync(project.Id, "list-agent");
        var first = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "stays-active"));
        var second = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "will-archive"));
        await _client.PostOkAsync($"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{second.Id}/archive");

        var listed = await _client.GetDataAsync<SubscriptionDto[]>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions");

        Assert.Equal(2, listed.Length);
        Assert.Contains(listed, s => s.Id == first.Id && s.Status == "active");
        Assert.Contains(listed, s => s.Id == second.Id && s.Status == "archived");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task List_UnknownAgent_Returns404()
    {
        var project = await CreateProjectAsync("subs-list-unknown");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/agents/agent_{Guid.NewGuid():N}/subscriptions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task List_OnlyIncludesOwningAgent()
    {
        var project = await CreateProjectAsync("subs-isolation");
        var agentA = await CreateAgentAsync(project.Id, "agent-a-iso");
        var agentB = await CreateAgentAsync(project.Id, "agent-b-iso");
        await _client.PostOkAsync($"/api/projects/{project.Id}/agents/{agentA.Id}/subscriptions",
            NewSubscription(name: "on-a"));
        await _client.PostOkAsync($"/api/projects/{project.Id}/agents/{agentB.Id}/subscriptions",
            NewSubscription(name: "on-b"));

        var onA = await _client.GetDataAsync<SubscriptionDto[]>(
            $"/api/projects/{project.Id}/agents/{agentA.Id}/subscriptions");
        var onB = await _client.GetDataAsync<SubscriptionDto[]>(
            $"/api/projects/{project.Id}/agents/{agentB.Id}/subscriptions");

        Assert.Single(onA);
        Assert.Equal("on-a", onA[0].Name);
        Assert.Single(onB);
        Assert.Equal("on-b", onB[0].Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Show_ResolvesOwnedSubscription()
    {
        var project = await CreateProjectAsync("subs-show");
        var agent = await CreateAgentAsync(project.Id, "show-agent");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "show-this"));

        var shown = await _client.GetDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{created.Id}");

        Assert.Equal(created.Id, shown.Id);
        Assert.Equal(created.Name, shown.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Show_CrossProject_Returns404()
    {
        var projectA = await CreateProjectAsync("subs-cross-a");
        var projectB = await CreateProjectAsync("subs-cross-b");
        var agent = await CreateAgentAsync(projectA.Id, "cross-agent");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{projectA.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "owned-by-a"));

        using var wrongProject = await _client.GetAsync(
            $"/api/projects/{projectB.Id}/agents/{agent.Id}/subscriptions/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Show_WrongAgent_Returns404()
    {
        var project = await CreateProjectAsync("subs-show-wrong-agent");
        var agentA = await CreateAgentAsync(project.Id, "agent-a-show");
        var agentB = await CreateAgentAsync(project.Id, "agent-b-show");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agentA.Id}/subscriptions",
            NewSubscription(name: "owned-by-a"));

        using var wrongAgent = await _client.GetAsync(
            $"/api/projects/{project.Id}/agents/{agentB.Id}/subscriptions/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, wrongAgent.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Patch_UpdatesFilterResponsePromptAndAdvancesUpdatedAt()
    {
        var project = await CreateProjectAsync("subs-patch");
        var agent = await CreateAgentAsync(project.Id, "patch-agent");
        var before = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "will-patch", priority: 1));
        var beforeUpdatedAt = DateTimeOffset.Parse(before.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var patched = await _client.PatchDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{before.Id}",
            new
            {
                filter = new
                {
                    type = "com.mohist.issue.completed",
                    source = "/mohist/issues/issue_x",
                    subject = "42",
                },
                responsePrompt = "after-prompt",
                priority = 9,
            });

        Assert.Equal("com.mohist.issue.completed", patched.Filter.Type);
        Assert.Equal("/mohist/issues/issue_x", patched.Filter.Source);
        Assert.Equal("42", patched.Filter.Subject);
        Assert.Equal("after-prompt", patched.ResponsePrompt);
        Assert.Equal(9, patched.Priority);
        Assert.True(DateTimeOffset.Parse(patched.UpdatedAt) > beforeUpdatedAt);
        Assert.Equal("will-patch", patched.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Patch_OmittedFields_AreUnchanged()
    {
        var project = await CreateProjectAsync("subs-patch-omit");
        var agent = await CreateAgentAsync(project.Id, "patch-omit");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "kept-name", priority: 4));

        var patched = await _client.PatchDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{created.Id}",
            new { responsePrompt = "new-prompt" });

        Assert.Equal("kept-name", patched.Name);
        Assert.Equal(4, patched.Priority);
        Assert.Equal("new-prompt", patched.ResponsePrompt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Patch_PriorityResetToNull_StoresAsNull()
    {
        var project = await CreateProjectAsync("subs-patch-null");
        var agent = await CreateAgentAsync(project.Id, "patch-null");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "reset", priority: 7));

        var patched = await _client.PatchDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{created.Id}",
            new { priority = (int?)null });

        Assert.Null(patched.Priority);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Patch_DuplicateNameOnSameAgent_Returns409AndOriginalUnchanged()
    {
        var project = await CreateProjectAsync("subs-patch-conflict");
        var agent = await CreateAgentAsync(project.Id, "patch-conflict");
        await _client.PostOkAsync($"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "taken"));
        var target = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "free"));

        using var conflict = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{target.Id}",
            new { name = "taken" });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var reFetched = await _client.GetDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{target.Id}");
        Assert.Equal("free", reFetched.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Archive_TogglesStatusToArchivedAndAdvancesUpdatedAt()
    {
        var project = await CreateProjectAsync("subs-archive");
        var agent = await CreateAgentAsync(project.Id, "archive-agent");
        var before = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "archive-me"));
        var beforeUpdatedAt = DateTimeOffset.Parse(before.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var archived = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{before.Id}/archive");

        Assert.Equal("archived", archived.Status);
        Assert.True(DateTimeOffset.Parse(archived.UpdatedAt) > beforeUpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Archive_IsIdempotentForAlreadyArchived()
    {
        var project = await CreateProjectAsync("subs-double-archive");
        var agent = await CreateAgentAsync(project.Id, "double-archive");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "first-archive"));
        var onceArchived = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{created.Id}/archive");
        var snapshot = DateTimeOffset.Parse(onceArchived.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var again = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{created.Id}/archive");

        Assert.Equal("archived", again.Status);
        Assert.Equal(snapshot, DateTimeOffset.Parse(again.UpdatedAt));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Restore_TogglesStatusToActiveAndAdvancesUpdatedAt()
    {
        var project = await CreateProjectAsync("subs-restore");
        var agent = await CreateAgentAsync(project.Id, "restore-agent");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "restore-me"));
        var archived = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{created.Id}/archive");
        var archivedAt = DateTimeOffset.Parse(archived.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var restored = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{created.Id}/restore");

        Assert.Equal("active", restored.Status);
        Assert.True(DateTimeOffset.Parse(restored.UpdatedAt) > archivedAt);
        Assert.Equal(created.Id, restored.Id);
        Assert.Equal("restore-me", restored.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Archive_Restore_CrossOwnershipReturns404()
    {
        var project = await CreateProjectAsync("subs-restore-cross");
        var agentA = await CreateAgentAsync(project.Id, "cross-owner-a");
        var agentB = await CreateAgentAsync(project.Id, "cross-owner-b");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agentA.Id}/subscriptions",
            NewSubscription(name: "owned-by-a"));

        using var wrongAgentArchive = await _client.PostAsync(
            $"/api/projects/{project.Id}/agents/{agentB.Id}/subscriptions/{created.Id}/archive",
            null);
        using var wrongAgentRestore = await _client.PostAsync(
            $"/api/projects/{project.Id}/agents/{agentB.Id}/subscriptions/{created.Id}/restore",
            null);

        Assert.Equal(HttpStatusCode.NotFound, wrongAgentArchive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongAgentRestore.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Delete_RemovesSubscriptionFromListAndShow()
    {
        var project = await CreateProjectAsync("subs-delete");
        var agent = await CreateAgentAsync(project.Id, "delete-agent");
        var first = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "keep"));
        var deleted = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "remove"));

        await _client.DeleteAsync($"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{deleted.Id}");

        using var showAfterDelete = await _client.GetAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{deleted.Id}");
        Assert.Equal(HttpStatusCode.NotFound, showAfterDelete.StatusCode);
        var listAfter = await _client.GetDataAsync<SubscriptionDto[]>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions");
        var only = Assert.Single(listAfter);
        Assert.Equal(first.Id, only.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task Delete_WrongAgent_Returns404()
    {
        var project = await CreateProjectAsync("subs-delete-wrong-agent");
        var agentA = await CreateAgentAsync(project.Id, "del-wrong-a");
        var agentB = await CreateAgentAsync(project.Id, "del-wrong-b");
        var created = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agentA.Id}/subscriptions",
            NewSubscription(name: "stay"));

        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/agents/{agentB.Id}/subscriptions/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ArchiveIndependentSubscription_DoesNotMutateSiblingOrAgent()
    {
        var project = await CreateProjectAsync("subs-independent");
        var agent = await CreateAgentAsync(project.Id, "independent-agent");
        var first = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "first"));
        var second = await _client.PostDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions",
            NewSubscription(name: "second"));
        var agentBefore = await _client.GetDataAsync<AgentSnapshotDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{first.Id}/archive");

        var secondAfter = await _client.GetDataAsync<SubscriptionDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}/subscriptions/{second.Id}");
        var agentAfter = await _client.GetDataAsync<AgentSnapshotDto>(
            $"/api/projects/{project.Id}/agents/{agent.Id}");

        Assert.Equal("active", secondAfter.Status);
        Assert.Equal(agentBefore.UpdatedAt, agentAfter.UpdatedAt);
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix) =>
        await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"{prefix}-{Guid.NewGuid():N}");

    private async Task<AgentDto> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"agent description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { type = "opencode" },
                skills = new[] { "review" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentDto(
            body.GetProperty("data").GetProperty("id").GetString()!,
            name);
    }

    private static object NewSubscription(string name, int? priority = 0) => new
    {
        name,
        filter = new
        {
            type = "com.mohist.workflow.stage.*",
            source = "/mohist/workflow-runs/run_abc",
            subject = (string?)null,
        },
        responsePrompt = "review and approve",
        priority,
    };

    private sealed record ProjectDto(string Id);

    private sealed record AgentDto(string Id, string Name);

    private sealed record AgentSnapshotDto(string Id, string Name, string Status, string UpdatedAt);

    private sealed record SubscriptionFilterPayload(string Type, string? Source, string? Subject);

    private sealed record SubscriptionDto(
        string Id,
        string ProjectId,
        string AgentId,
        string Name,
        SubscriptionFilterPayload Filter,
        string ResponsePrompt,
        int? Priority,
        string Status,
        string CreatedAt,
        string UpdatedAt);
}

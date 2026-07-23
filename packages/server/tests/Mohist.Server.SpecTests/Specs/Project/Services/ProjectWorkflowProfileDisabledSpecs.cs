using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Project.Services;

public class ProjectWorkflowProfileDisabledSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly ProjectWorkflowProfileManager _manager;

    public ProjectWorkflowProfileDisabledSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _manager = new ProjectWorkflowProfileManager(new TestDbContextFactory(_database.Options), new StubPromptLoader(), new PromptTemplateEngine(), NullActionCatalogSource.Instance);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GetDisabledWorkflowProfileIds_DefaultProject_ReturnsEmpty()
    {
        var disabled = await _manager.GetDisabledWorkflowProfileIdsAsync("proj-new");

        Assert.Empty(disabled);
    }

    [Fact]
    public async Task DisableProfile_AddsToBlacklist()
    {
        await _manager.SetProfileEnabledAsync("proj-disable", "MOHIST/GITHUB-PR", enabled: false);

        var disabled = await _manager.GetDisabledWorkflowProfileIdsAsync("proj-disable");
        Assert.Contains("mohist/github-pr", disabled);
        Assert.Contains("MOHIST/GITHUB-PR", disabled);
        Assert.Single(disabled);
    }

    [Fact]
    public async Task EnableProfile_RemovesFromBlacklist()
    {
        await _manager.SetProfileEnabledAsync("proj-enable", "mohist/github-pr", enabled: false);
        Assert.Single(await _manager.GetDisabledWorkflowProfileIdsAsync("proj-enable"));

        await _manager.SetProfileEnabledAsync("proj-enable", "mohist/github-pr", enabled: true);

        Assert.Empty(await _manager.GetDisabledWorkflowProfileIdsAsync("proj-enable"));
    }

    [Fact]
    public async Task DisableOneOfSeveralProfiles_Succeeds()
    {
        // Given: both profiles enabled initially

        // When: disable one
        await _manager.SetProfileEnabledAsync("proj-several", "mohist/local", enabled: false);

        // Then: one profile is disabled, other stays enabled
        var disabled = await _manager.GetDisabledWorkflowProfileIdsAsync("proj-several");
        Assert.Contains("mohist/local", disabled);
        Assert.Single(disabled);
    }

    [Fact]
    public async Task DisableLastEnabledProfile_ThrowsAndBlacklistUnchanged()
    {
        // Given: both profiles enabled initially

        // When: disable one first
        await _manager.SetProfileEnabledAsync("proj-last", "mohist/local", enabled: false);

        // Then: second disable of the remaining one throws
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.SetProfileEnabledAsync("proj-last", "mohist/github-pr", enabled: false));

        Assert.Contains("at least one workflow profile must remain enabled", ex.Message);

        // And: blacklist still only has the first profile
        var disabled = await _manager.GetDisabledWorkflowProfileIdsAsync("proj-last");
        Assert.Single(disabled);
        Assert.Contains("mohist/local", disabled);
    }

    [Fact]
    public async Task EnableNonExistentProfile_ThrowsAndDoesNotChangeBlacklist()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _manager.SetProfileEnabledAsync("proj-noop", "does/not/exist", enabled: true));

        Assert.Empty(await _manager.GetDisabledWorkflowProfileIdsAsync("proj-noop"));
    }

    [Fact]
    public async Task DisableNonExistentProfile_ThrowsAndDoesNotChangeBlacklist()
    {
        await _manager.SetProfileEnabledAsync("proj-unknown", "mohist/local", enabled: false);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _manager.SetProfileEnabledAsync("proj-unknown", "does/not/exist", enabled: false));

        var disabled = await _manager.GetDisabledWorkflowProfileIdsAsync("proj-unknown");
        Assert.Single(disabled);
        Assert.Contains("mohist/local", disabled);
    }

    [Fact]
    public async Task DisableMohistLocal_WithOtherEnabled_Succeeds()
    {
        await _manager.SetProfileEnabledAsync("proj-local", "mohist/local", enabled: false);

        var disabled = await _manager.GetDisabledWorkflowProfileIdsAsync("proj-local");
        Assert.Contains("mohist/local", disabled);
        Assert.Single(disabled);
    }

    private sealed class StubPromptLoader : IPromptLoader
    {
        public Dictionary<string, string> LoadAll() => new(StringComparer.Ordinal);
    }
}

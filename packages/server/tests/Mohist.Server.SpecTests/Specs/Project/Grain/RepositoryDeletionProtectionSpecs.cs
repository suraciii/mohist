using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Grain;

/// <summary>
/// issue-417 T-004: lock the repository deletion contract spelled out in
/// <c>openspec/changes/issue-417/specs/project-management/spec.md</c>.
/// <para>
/// Coverage:
/// <list type="bullet">
///   <item>unused non-default repository can be removed</item>
///   <item>backlog / draft issue blocks deletion with a distinct
///     <see cref="RepositoryInUseException"/></item>
///   <item>in-progress issue (any workflow health) blocks deletion</item>
///   <item>terminal done / cancelled issues never block deletion, and
///     historical targets survive the deletion in IssueRow</item>
///   <item>an in-progress issue in a different project does not block
///     deletion of the selected project's repository of the same name</item>
///   <item>default-repository precedence is preserved: not-found
///     wins over blocker; default still wins over blocker.</item>
///   <item>the idempotent participant command records a receipt and
///     rejects stale revisions; a duplicate replay returns the
///     already-applied outcome without mutating state.</item>
///   <item>alias rejection in <see cref="RepositoryPolicy"/> fires
///     through the grain path on add / update of an equivalent
///     remote.</item>
/// </list>
/// </para>
/// </summary>
[Collection("IntegrationIssue")]
public class RepositoryDeletionProtectionSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly IGrainFactory _grains;
    private readonly string _connectionString;

    public RepositoryDeletionProtectionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
        _connectionString = fixture.ConnectionString;
    }

    private IProjectGrain NewProjectGrain() =>
        _grains.GetGrain<IProjectGrain>($"proj_{Guid.NewGuid():N}");

    private async Task<(string ProjectId, ProjectInfo Project)> SeedProjectAsync(
        string defaultName = "server",
        string defaultUrl = "git@example.com:server.git",
        string? secondaryName = "web",
        string? secondaryUrl = "git@example.com:web.git")
    {
        var grain = NewProjectGrain();
        var project = await grain.CreateAsync(
            $"proj-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = defaultName,
                GitUrl = defaultUrl,
                BaseBranch = "main",
                IsDefault = true,
            });
        if (secondaryName is not null && secondaryUrl is not null)
            await grain.AddRepositoryAsync(secondaryName, secondaryUrl, "main");
        return (grain.GetPrimaryKeyString(), project);
    }

    private async Task<int> SeedIssueAsync(
        string projectId,
        string repositoryName,
        bool isDraft = false)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        await _grains.CreateIssueThroughCoordinatorAsync(
            projectId,
            number,
            issueId,
            $"Issue #{number}",
            repositoryName: repositoryName,
            isDraft: isDraft);
        return number;
    }

    private async Task<(int Number, string IssueId, string WrId)> SeedInProgressIssueAsync(
        string projectId,
        string repositoryName)
    {
        var number = await SeedIssueAsync(projectId, repositoryName, isDraft: false);
        var issueId = await ResolveIssueIdAsync(projectId, number);
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        var wrId = await grain.StartWorkAsync();
        return (number, issueId, wrId);
    }

    private async Task<string> ResolveIssueIdAsync(string projectId, int number)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var row = await db.Issues.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Number == number)
            .Select(r => r.IssueId)
            .SingleAsync();
        return row;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_UnusedNonDefault_Succeeds()
    {
        var (projectId, _) = await SeedProjectAsync();

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var updated = await grain.RemoveRepositoryAsync("web");

        Assert.NotNull(updated);
        Assert.Single(updated!.Repositories);
        Assert.True(updated.Repositories[0].IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_DraftBacklogIssue_ThrowsInUseWithoutMutation()
    {
        var (projectId, _) = await SeedProjectAsync();
        await SeedIssueAsync(projectId, "web", isDraft: true);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var ex = await Assert.ThrowsAsync<RepositoryInUseException>(() =>
            grain.RemoveRepositoryAsync("web"));
        Assert.Equal("web", ex.RepositoryName);

        var after = await grain.GetAsync();
        Assert.Equal(2, after!.Repositories.Count);
        Assert.Contains(after.Repositories, r => r.Name == "web");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_BacklogNonDraftIssue_ThrowsInUse()
    {
        var (projectId, _) = await SeedProjectAsync();
        await SeedIssueAsync(projectId, "web", isDraft: false);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        await Assert.ThrowsAsync<RepositoryInUseException>(() =>
            grain.RemoveRepositoryAsync("web"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_InProgressIssue_ThrowsInUseRegardlessOfWorkflowHealth()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (_, _, wrId) = await SeedInProgressIssueAsync(projectId, "web");

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        await Assert.ThrowsAsync<RepositoryInUseException>(() =>
            grain.RemoveRepositoryAsync("web"));

        // Even after stopping the workflow the in-progress issue still
        // blocks deletion — the guard reads committed IssueRow status,
        // not workflow health. CancelAsync moves the issue to terminal
        // cancelled state, which then releases the repository.
        var wfGrain = _grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("user-stopped");

        await Assert.ThrowsAsync<RepositoryInUseException>(() =>
            grain.RemoveRepositoryAsync("web"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_OnlyTerminalIssuesBound_SucceedsAndKeepsHistoricalName()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (number, issueId, wrId) = await SeedInProgressIssueAsync(projectId, "web");
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.CompleteWorkAsync(wrId);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var updated = await grain.RemoveRepositoryAsync("web");
        Assert.NotNull(updated);
        Assert.Single(updated!.Repositories);

        // The IssueRow still names 'web' even though the repository no
        // longer exists in the project — historical bindings are not
        // rewritten by deletion.
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var row = await db.Issues.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Number == number)
            .SingleAsync();
        Assert.Equal("web", row.RepositoryName);
        Assert.Equal("done", row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_CancelledIssueDoesNotBlock_Succeeds()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (number, issueId, wrId) = await SeedInProgressIssueAsync(projectId, "web");
        var wfGrain = _grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("user-stopped");
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.CancelAsync();

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var updated = await grain.RemoveRepositoryAsync("web");
        Assert.NotNull(updated);
        Assert.Single(updated!.Repositories);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var row = await db.Issues.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Number == number)
            .SingleAsync();
        Assert.Equal("web", row.RepositoryName);
        Assert.Equal("cancelled", row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_CrossProjectIssueDoesNotBlock()
    {
        // Project A has a backlog issue bound to its own "web"; project B
        // is the one whose deletion we're testing. The cross-project
        // binding must not affect B's guard.
        var (projectA, _) = await SeedProjectAsync(
            defaultName: "server-a", defaultUrl: "git@example.com:a.git");
        await SeedIssueAsync(projectA, "web", isDraft: false);

        var (projectB, _) = await SeedProjectAsync(
            defaultName: "server-b", defaultUrl: "git@example.com:b.git");

        var grainB = _grains.GetGrain<IProjectGrain>(projectB);
        var updated = await grainB.RemoveRepositoryAsync("web");
        Assert.NotNull(updated);
        Assert.Single(updated!.Repositories);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_UnknownRepository_ReturnsNullWithoutBlockerQuery()
    {
        // The blocker guard must not fire when the repository does not
        // exist — not-found precedence is preserved over the in-use
        // conflict.
        var (projectId, _) = await SeedProjectAsync();
        await SeedIssueAsync(projectId, "web", isDraft: false);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var result = await grain.RemoveRepositoryAsync("ghost");
        Assert.Null(result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_DefaultRepository_DefaultConflictWinsOverBlocker()
    {
        // If a non-terminal issue is bound to the default repository,
        // the existing default-repository precedence must be preserved:
        // the deletion reports the default conflict, not the in-use
        // conflict. This keeps operator-facing error messages
        // unambiguous.
        var (projectId, _) = await SeedProjectAsync(secondaryName: null);
        await SeedIssueAsync(projectId, "server", isDraft: false);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.RemoveRepositoryAsync("server"));
        Assert.Contains("default", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task AddRepository_AliasRemoteRejectedAsAliasConflict()
    {
        var (projectId, _) = await SeedProjectAsync(secondaryName: null);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AddRepositoryAsync("web", "git@example.com:server.git", "main"));
        Assert.Contains("shares its Git remote", ex.Message, StringComparison.OrdinalIgnoreCase);

        var after = await grain.GetAsync();
        Assert.Single(after!.Repositories);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task UpdateRepository_AliasRemoteRejectedAsAliasConflict()
    {
        var (projectId, _) = await SeedProjectAsync();

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.UpdateRepositoryAsync("web", gitUrl: "git@example.com:server.git"));
        Assert.Contains("shares its Git remote", ex.Message, StringComparison.OrdinalIgnoreCase);

        var after = await grain.GetAsync();
        var web = after!.Repositories.Single(r => r.Name == "web");
        Assert.Equal("git@example.com:web.git", web.GitUrl);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepositoryWithReceipt_FirstCall_RemovesAndRecordsReceipt()
    {
        var (projectId, _) = await SeedProjectAsync();
        var grain = _grains.GetGrain<IProjectGrain>(projectId);

        var outcome = await grain.RemoveRepositoryWithReceiptAsync(
            "web", commandId: "cmd-1", expectedRevision: null);
        Assert.Equal(ProjectRepositoryRemovalOutcome.Removed, outcome);

        var row = await LoadProjectRowAsync(projectId);
        Assert.Equal(3L, row.RepositoryRevision);
        Assert.False(string.IsNullOrEmpty(row.LastRepositoryCommandJson));
        var receipt = JSON.Deserialize<ProjectRepositoryCommandReceipt>(
            row.LastRepositoryCommandJson!)!;
        Assert.Equal("cmd-1", receipt.CommandId);
        Assert.Equal("remove", receipt.Kind);
        Assert.Equal("web", receipt.RepositoryName);
        Assert.Equal(3L, receipt.AppliedRevision);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepositoryWithReceipt_DuplicateReplay_ReturnsAlreadyAppliedWithoutMutation()
    {
        var (projectId, _) = await SeedProjectAsync();
        var grain = _grains.GetGrain<IProjectGrain>(projectId);

        var first = await grain.RemoveRepositoryWithReceiptAsync(
            "web", commandId: "cmd-1", expectedRevision: null);
        Assert.Equal(ProjectRepositoryRemovalOutcome.Removed, first);

        var second = await grain.RemoveRepositoryWithReceiptAsync(
            "web", commandId: "cmd-1", expectedRevision: null);
        Assert.Equal(ProjectRepositoryRemovalOutcome.AlreadyApplied, second);

        // Receipt revision must not advance on duplicate replay.
        var row = await LoadProjectRowAsync(projectId);
        Assert.Equal(3L, row.RepositoryRevision);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepositoryWithReceipt_StaleRevision_ThrowsAndDoesNotMutate()
    {
        // Three-repository project so the stale-revision check is the
        // first failure mode to fire (default-check is bypassed because
        // we never touch the default, not-found is bypassed because we
        // name an existing repository).
        var (projectId, _) = await SeedProjectAsync(
            secondaryName: "worker",
            secondaryUrl: "git@example.com:worker.git");
        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        await grain.AddRepositoryAsync("web", "git@example.com:web.git", "main");

        var first = await grain.RemoveRepositoryWithReceiptAsync(
            "worker", commandId: "cmd-1", expectedRevision: null);
        Assert.Equal(ProjectRepositoryRemovalOutcome.Removed, first);

        // Replay with an explicit, stale expectedRevision must reject
        // before mutating state. Even though "web" exists, the stored
        // revision (1) disagrees with the caller's (99), so the
        // stale-revision check is what fires.
        await Assert.ThrowsAsync<ProjectRepositoryStaleRevisionException>(() =>
            grain.RemoveRepositoryWithReceiptAsync(
                "web", commandId: "cmd-2", expectedRevision: 99L));

        var after = await grain.GetAsync();
        Assert.Equal(2, after!.Repositories.Count);
        Assert.Contains(after.Repositories, r => r.Name == "web");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepositoryWithReceipt_UnknownRepository_ThrowsNotFound()
    {
        var (projectId, _) = await SeedProjectAsync();
        var grain = _grains.GetGrain<IProjectGrain>(projectId);

        await Assert.ThrowsAsync<ProjectRepositoryNotFoundException>(() =>
            grain.RemoveRepositoryWithReceiptAsync("ghost", "cmd-1", null));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepositoryWithReceipt_DefaultRepository_DefaultConflictWinsOverBlocker()
    {
        // The participant path preserves the existing default-repository
        // precedence: a non-terminal issue bound to the default must
        // surface the default conflict, not the in-use conflict.
        var (projectId, _) = await SeedProjectAsync(secondaryName: null);
        await SeedIssueAsync(projectId, "server", isDraft: false);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.RemoveRepositoryWithReceiptAsync("server", "cmd-1", null));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepositoryWithReceipt_NonTerminalIssue_ThrowsInUseWithoutMutation()
    {
        var (projectId, _) = await SeedProjectAsync();
        await SeedIssueAsync(projectId, "web", isDraft: false);

        var grain = _grains.GetGrain<IProjectGrain>(projectId);
        await Assert.ThrowsAsync<RepositoryInUseException>(() =>
            grain.RemoveRepositoryWithReceiptAsync("web", "cmd-1", null));

        var after = await grain.GetAsync();
        Assert.Equal(2, after!.Repositories.Count);
        Assert.Contains(after.Repositories, r => r.Name == "web");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepositoryWithReceipt_BlockerCheck_RunsAfterDefaultCheck()
    {
        // Default-repository conflict must be reported before the
        // blocker query — even when a non-terminal issue is bound to
        // it. Spec: "preserving not-found/default error precedence".
        var (projectId, _) = await SeedProjectAsync(secondaryName: null);
        var grain = _grains.GetGrain<IProjectGrain>(projectId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.RemoveRepositoryWithReceiptAsync("server", "cmd-1", null));
    }

    private async Task<Mohist.Server.Infrastructure.Data.Project.ProjectRow> LoadProjectRowAsync(
        string projectId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        return await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .SingleAsync();
    }
}

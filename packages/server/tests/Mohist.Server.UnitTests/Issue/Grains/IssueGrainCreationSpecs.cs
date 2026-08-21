using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Grains;

/// <summary>
/// Owner coverage for the <see cref="IssueGrain"/> create/update orchestration
/// that the former full-host specs proved through the shared silo (#676):
/// identity binding, numbering, prerequisite create-time validation, risk and
/// label persistence, hydration guards, and the start-readiness gate. The
/// grains run against the production service graph and real in-memory SQLite
/// with a stub grain factory serving manually activated project/counter
/// grains; workflow-bound behaviors stay in the application-level specs.
/// </summary>
[Collection("MohistDb")]
public sealed class IssueGrainCreationSpecs
{
    private readonly MohistDbFixture _fixture;
    private readonly IGrainFactory _grains;

    public IssueGrainCreationSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
        _grains = new StubGrainFactory(fixture);
    }

    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private async Task<ProjectInfo> SetupProjectAsync()
    {
        // Arrange the project rows the create path resolves against. The
        // row shape mirrors ProjectGrain.CreateAsync's persistence (project
        // + default workflow profile) without booting that grain; the read
        // side maps through the production ProjectQuerier.ToInfo.
        var id = $"proj_{Guid.NewGuid():N}";
        var repositories = JsonSerializer.Serialize(new[]
        {
            new RepositoryInfo { Name = "main", GitUrl = "git@example.com:main.git", BaseBranch = "main", IsDefault = true },
        });
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Projects.Add(new ProjectRow
        {
            Id = id,
            Name = $"proj-{Guid.NewGuid():N}",
            RepositoriesJson = repositories,
            RepositoryRevision = 1,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        });
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = id,
            DefaultWorkflowProfileId = WorkflowProfileCatalog.LocalId,
        });
        await db.SaveChangesAsync();

        var project = await _grains.GetGrain<IProjectGrain>(id).GetAsync();
        return project!;
    }

    private async Task<IssueInfo> CreateIssueAsync(
        string projectId,
        string title,
        string? body = null,
        IReadOnlyDictionary<string, string>? labels = null,
        string? priority = null,
        string? risk = null,
        bool isDraft = false,
        int[]? prerequisiteNumbers = null)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = await IssueGrainAsync(projectId, number);
        await grain.CreateAsync(projectId, number, title, body, labels, priority, null, risk, isDraft, null, null, prerequisiteNumbers);
        return (await GetIssueInfoAsync(projectId, number))!;
    }

    private async Task<IssueGrain> IssueGrainAsync(string projectId, int number)
    {
        var scope = _fixture.Services.CreateScope();
        var grain = CreateGrain(scope.ServiceProvider, projectId, number);
        await grain.OnActivateAsync(CancellationToken.None);
        return grain;
    }

    private IssueGrain CreateGrain(IServiceProvider services, string projectId, int number)
    {
        var identity = GrainTestContext.Create(GrainKey.Issue(new IssueKey(projectId, number)), _grains);
        return new IssueGrain(
            identity.Context,
            identity.Runtime,
            services.GetRequiredService<IIssueStore>(),
            services.GetRequiredService<IssueWorkflowProfileRegistry>(),
            services.GetRequiredService<WorkflowQuerier>(),
            services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            services.GetRequiredService<IEventStore>(),
            _grains,
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

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _fixture.Services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetInfoAsync(projectId, number);
    }

    private async Task<IssueReadModel?> GetIssueReadModelAsync(string projectId, int number)
    {
        var project = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        using var scope = _fixture.Services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetAsync(projectId, number, project!);
    }

    [Fact]
    public async Task CreateIssue_ReturnsInfoWithNumber()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Test issue", "body");

        Assert.Equal(1, issue.Number);
        Assert.Equal("Test issue", issue.Title);
        Assert.Equal("body", issue.Body);
        Assert.Equal("backlog", issue.Status);
        Assert.Equal("active", issue.Health);
        Assert.Equal(project.Id, issue.ProjectId);
        Assert.Equal("mohist/local", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task CreateIssue_RejectsIdentityOutsideGrainKey()
    {
        var projectA = await SetupProjectAsync();
        var projectB = await SetupProjectAsync();
        var grain = await IssueGrainAsync(projectB.Id, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync(projectA.Id, 1, "cross-project issue", null, null, null));

        Assert.Null(await GetIssueInfoAsync(projectA.Id, 1));
        Assert.Null(await GetIssueInfoAsync(projectB.Id, 1));
    }

    // Regression guard for the bug that left IssueEvents permanently empty:
    // SaveIssueAsync snapshotted PendingEvents by reference, then
    // ClearPendingEvents() drained the same list, so the publish path
    // no-op'd on an empty collection. This spec asserts the issue→IssueEvents
    // append happens through the real grain + EventStore transaction.
    [Fact]
    public async Task CreateIssue_PersistsCreatedEventToIssueEvents()
    {
        var project = await SetupProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Event persistence probe");

        using var scope = _fixture.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var stored = await events.ListIssueEventsAsync(project.Id, issue.Number);

        var created = Assert.Single(stored);
        Assert.Equal("com.mohist.issue.created", created.Envelope.Type);
        Assert.Equal($"/mohist/projects/{project.Id}/issues/{issue.Number}", created.Envelope.Source.ToString());
    }

    [Fact]
    public async Task CreateIssue_DefaultWorkflowProfile_ComesFromDefaultProfile()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Default profile");

        Assert.Equal("mohist/local", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task CreateIssue_SequentialNumbers()
    {
        var project = await SetupProjectAsync();

        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task CreateIssue_WithLabelsAndPriority()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(
            project.Id,
            "Labeled",
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["priority"] = "p0",
            },
            priority: "p0");

        Assert.Equal("frontend", issue.Labels["stream"]);
        Assert.Equal("p0", issue.Labels["priority"]);
        Assert.Equal("p0", issue.Priority);
    }

    [Fact]
    public async Task Querier_ReturnsIssueInfo()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Info test", "desc");

        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal(created.Number, info.Number);
        Assert.Equal("Info test", info.Title);
        Assert.Equal("desc", info.Body);
    }

    [Fact]
    public async Task Update_ChangesTitleAndBody()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Original", "old body");

        var grain = await IssueGrainAsync(project.Id, created.Number);
        await grain.UpdateAsync("Updated", "new body");
        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal("Updated", info.Title);
        Assert.Equal("new body", info.Body);
    }

    [Fact]
    public async Task Hydrate_Duplicate_Throws()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Dup");

        var grain = await IssueGrainAsync(project.Id, created.Number);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync(project.Id, 999, "dup", null, null, null, null));
    }

    [Fact]
    public async Task DifferentProjects_IndependentNumbering()
    {
        var project1 = await SetupProjectAsync();
        var project2 = await SetupProjectAsync();

        var issue1 = await CreateIssueAsync(project1.Id, "P1-Issue");
        var issue2 = await CreateIssueAsync(project2.Id, "P2-Issue");

        Assert.Equal(1, issue1.Number);
        Assert.Equal(1, issue2.Number);
    }

    [Fact]
    public async Task AddPrerequisite_StartReadinessAndStartGateComeFromIssueGrain()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var grain = await IssueGrainAsync(project.Id, dependent.Number);

        await grain.AddPrerequisiteAsync(prereq.Number);
        var info = await GetIssueInfoAsync(project.Id, dependent.Number);
        var readiness = await grain.GetStartReadinessAsync();

        Assert.NotNull(info);
        Assert.Contains(prereq.Number, info.PrerequisiteNumbers);
        Assert.False(readiness.CanStart);
        var waiting = Assert.IsType<IssueStartBlockerDto.WaitingForBlocker>(readiness.Blocker);
        Assert.Equal(prereq.Number, waiting.Issue.Number);
        await Assert.ThrowsAsync<IssueStartBlockedException>(() => grain.StartWorkAsync());
    }

    [Fact]
    public async Task CreateIssue_WithRisk_PersistsAndReturnsIt()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Risked", risk: "high");

        Assert.Equal("high", issue.Risk);
    }

    [Fact]
    public async Task CreateIssue_WithoutRisk_ReturnsNull()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "NoRisk");

        Assert.Null(issue.Risk);
    }

    [Fact]
    public async Task ReadModel_IncludesRisk_AfterCreate()
    {
        var project = await SetupProjectAsync();

        var number = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var grain = await IssueGrainAsync(project.Id, number);
        await grain.CreateAsync(project.Id, number, "Medium risk", body: null, labels: null, priority: null, repositoryRef: null, risk: "medium");

        using var scope = _fixture.Services.CreateScope();
        var issuesQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var readModel = await issuesQuery.GetAsync(project.Id, number, project);

        Assert.NotNull(readModel);
        Assert.Equal("medium", readModel!.Risk);
    }

    [Fact]
    public async Task CreateIssue_WithInvalidRisk_Throws()
    {
        var project = await SetupProjectAsync();
        var number = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var grain = await IssueGrainAsync(project.Id, number);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.CreateAsync(project.Id, number, "Bad", null, null, null, null, "unknown"));
    }

    [Fact]
    public async Task CreateIssue_WithPrerequisiteNumbers_RecordsBothAndExposesReadModels()
    {
        var project = await SetupProjectAsync();
        var prereqA = await CreateIssueAsync(project.Id, "Prereq A");
        var prereqB = await CreateIssueAsync(project.Id, "Prereq B");

        var dependent = await CreateIssueAsync(
            project.Id,
            "Dependent",
            prerequisiteNumbers: [prereqA.Number, prereqB.Number]);

        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, dependent.PrerequisiteNumbers);

        var readModel = await GetIssueReadModelAsync(project.Id, dependent.Number);
        Assert.NotNull(readModel);
        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, readModel!.PrerequisiteNumbers);
        Assert.Equal(2, readModel.Prereq.Length);
        var summaryNumbers = readModel.Prereq.Select(p => p.Number).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, summaryNumbers);
        Assert.All(readModel.Prereq, p => Assert.False(p.Completed));
        Assert.False(readModel.CanStart);
        var waiting = Assert.IsType<IssueStartBlockerDto.WaitingForBlocker>(readModel.Blocker);
        Assert.Equal(prereqA.Number, waiting.Issue.Number);
    }

    [Fact]
    public async Task CreateIssue_WithPrerequisiteNumbers_CollapsesDuplicatesIdempotently()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Only one prereq");

        var dependent = await CreateIssueAsync(
            project.Id,
            "Dependent",
            prerequisiteNumbers: [prereq.Number, prereq.Number, prereq.Number]);

        Assert.Equal(new[] { prereq.Number }, dependent.PrerequisiteNumbers);
        var readModel = await GetIssueReadModelAsync(project.Id, dependent.Number);
        Assert.NotNull(readModel);
        Assert.Single(readModel!.Prereq);
    }

    [Fact]
    public async Task CreateIssue_WithoutPrerequisiteNumbers_LeavesEmptySet()
    {
        var project = await SetupProjectAsync();

        var plain = await CreateIssueAsync(project.Id, "Plain");

        Assert.Empty(plain.PrerequisiteNumbers);
        var readModel = await GetIssueReadModelAsync(project.Id, plain.Number);
        Assert.NotNull(readModel);
        Assert.Empty(readModel!.Prereq);
        Assert.True(readModel.CanStart || readModel.Blocker is not null);
    }

    [Fact]
    public async Task CreateIssue_WithEmptyPrerequisiteNumbers_BehavesAsAbsent()
    {
        var project = await SetupProjectAsync();

        var plain = await CreateIssueAsync(project.Id, "Plain empty", prerequisiteNumbers: []);

        Assert.Empty(plain.PrerequisiteNumbers);
    }

    [Fact]
    public async Task CreateIssue_WithNonexistentPrerequisite_ThrowsAndLeavesNoIssue()
    {
        var project = await SetupProjectAsync();

        var attemptNumber = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var grain = await IssueGrainAsync(project.Id, attemptNumber);

        await Assert.ThrowsAsync<PrerequisiteValidationException>(() =>
            grain.CreateAsync(
                project.Id,
                attemptNumber,
                "Will fail",
                body: null,
                labels: null,
                priority: null,
                repositoryRef: null,
                risk: null,
                isDraft: false,
                attachmentIds: null,
                workflowProfileId: null,
                prerequisiteNumbers: new[] { 999_999 }));

        var readModel = await GetIssueReadModelAsync(project.Id, attemptNumber);
        Assert.Null(readModel);
    }

    [Fact]
    public async Task CreateIssue_WithCrossProjectPrerequisite_ThrowsAsNotFound()
    {
        var projectA = await SetupProjectAsync();
        var projectB = await SetupProjectAsync();
        var issueInA = await CreateIssueAsync(projectA.Id, "A issue");

        await Assert.ThrowsAsync<PrerequisiteValidationException>(() =>
            CreateIssueAsync(projectB.Id, "B dependent", prerequisiteNumbers: [issueInA.Number]));

        var readModel = await GetIssueReadModelAsync(projectB.Id, 1);
        Assert.Null(readModel);
    }

    [Fact]
    public async Task CreateIssue_WithSelfReferencingPrerequisite_ThrowsAndLeavesNoIssue()
    {
        var project = await SetupProjectAsync();

        await CreateIssueAsync(project.Id, "First");

        // AddPrerequisiteAsync path cannot self-reference (it would have to
        // know its own number in advance). For create, reserve the next
        // number via the counter, then attempt CreateAsync on a fresh grain
        // with prerequisiteNumbers pointing at it.
        var reserved = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var freshGrain = await IssueGrainAsync(project.Id, reserved);

        await Assert.ThrowsAsync<PrerequisiteValidationException>(() =>
            freshGrain.CreateAsync(
                project.Id,
                reserved,
                "Self ref",
                body: null,
                labels: null,
                priority: null,
                repositoryRef: null,
                risk: null,
                isDraft: false,
                attachmentIds: null,
                workflowProfileId: null,
                prerequisiteNumbers: new[] { reserved }));

        var readModel = await GetIssueReadModelAsync(project.Id, reserved);
        Assert.Null(readModel);
    }

    /// <summary>
    /// Serves exactly the grain references the create/update paths reach for
    /// without an Orleans silo: the project grain (repository resolution),
    /// the counter grain (numbering), and the post-commit dispatcher poke.
    /// Anything else is a misuse and throws instead of returning a dead ref.
    /// </summary>
    private sealed class StubGrainFactory(MohistDbFixture fixture) : IGrainFactory
    {
        private readonly ConcurrentDictionary<string, IssueCounterState> _counters = new();
        private readonly ConcurrentDictionary<string, FakePersistentState<IssueCounterState>> _counterStates = new();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IProjectGrain))
                return (TGrainInterface)(object)BuildProjectGrain(primaryKey);
            if (typeof(TGrainInterface) == typeof(IIssueCounterGrain))
            {
                // One state instance per key mirrors Orleans: the storage
                // provider loads state once per activation and keeps it for
                // the grain's lifetime.
                var state = _counterStates.GetOrAdd(
                    $"counter-{primaryKey}",
                    k => new FakePersistentState<IssueCounterState>(k, _counters));
                return (TGrainInterface)(object)new IssueCounterGrain(state);
            }
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException($"StubGrainFactory does not support {typeof(TGrainInterface).Name}");
        }

        private object BuildProjectGrain(string primaryKey)
        {
            var proxy = DispatchProxy.Create<IProjectGrain, ProjectGrainProxy>();
            ((ProjectGrainProxy)(object)proxy!).ProjectId = primaryKey;
            ((ProjectGrainProxy)(object)proxy!).DbFactory =
                fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            return proxy!;
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"StubGrainFactory does not support {typeof(TGrainInterface).Name}");
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"StubGrainFactory does not support {typeof(TGrainInterface).Name}");
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"StubGrainFactory does not support {typeof(TGrainInterface).Name}");
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"StubGrainFactory does not support {typeof(TGrainInterface).Name}");
        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();
        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-only stand-in for the project grain: the create path only calls
    /// <c>GetAsync</c> for repository resolution, and the returned
    /// <see cref="ProjectInfo"/> is mapped by the production
    /// <see cref="ProjectQuerier.ToInfo"/> from the seeded rows.
    /// </summary>
    private class ProjectGrainProxy : DispatchProxy
    {
        public string ProjectId { get; set; } = string.Empty;

        public IDbContextFactory<MohistDbContext> DbFactory { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IProjectGrain.GetAsync))
                return GetAsync();
            throw new NotSupportedException($"ProjectGrainProxy does not support {targetMethod?.Name}");
        }

        private async Task<ProjectInfo?> GetAsync()
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var entry = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == ProjectId);
            return entry is null ? null : ProjectQuerier.ToInfo(entry);
        }
    }

    /// <summary>In-memory stand-in for the counter grain's Orleans storage.</summary>
    private sealed class FakePersistentState<TState>(string key, ConcurrentDictionary<string, TState> store) : IPersistentState<TState>
    {
        public TState State { get; set; } = default!;

        public string Etag => Guid.NewGuid().ToString("N");

        public bool RecordExists => store.ContainsKey(key);

        public Task ReadStateAsync()
        {
            State = store.TryGetValue(key, out var value) ? value : default!;
            return Task.CompletedTask;
        }

        public Task WriteStateAsync()
        {
            store[key] = State;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync()
        {
            store.TryRemove(key, out _);
            State = default!;
            return Task.CompletedTask;
        }
    }
}

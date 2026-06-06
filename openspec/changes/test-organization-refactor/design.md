---
purpose: "Implementation plan for reorganizing the Mohist test suite by Bounded Context, factoring shared helpers, and adding test categorization."
include:
  - "Phase ordering and per-phase verification."
  - "Fixture, helper, and directory layout changes."
  - "Trait attribute vocabulary and naming rules."
exclude:
  - "Production code changes (none expected)."
  - "Per-spec assertion rewrites (out of scope)."
  - "New test scenarios added for behavior coverage."
style:
  - "Reference current files by relative path so each phase is reviewable in isolation."
  - "List the exact files added, modified, and moved for each phase."
---

# Design: Test Organization Refactor

## Phase Plan

The work is split into 5 phases. Each phase is a single, reviewable commit (or small commit stack) and the full test suite must stay green at the end of every phase.

| Phase | Goal | Commit | Risk | Tests after phase |
|-------|------|--------|------|------------------|
| **1. Categorize** | Add `[Trait]` to every spec | 1 commit | zero | 680 pass, `--filter "Speed=Unit"` works |
| **2. Fixture split** | `MohistDbFixture` for D-class specs | 1 commit | low | 680 pass, D-class specs don't start WebApplicationFactory |
| **3. Helper extraction** | Move helpers + test data factories to `Support/` | 1–2 commits | medium | 680 pass, no inherited state |
| **4. Bounded-context layout** | Move 87 files into subdirectories | 1 commit | low (mechanical) | 680 pass, all under new subfolders |
| **5. Archtest enforcement** | Add 4 archtest rules | 1 commit | low | 680 pass, violations caught |

Phase 1 can land first because it has zero behavior risk. Phases 2–4 are interdependent at the test-fixture level: the trait attributes from Phase 1 make it easy to run `--filter "Speed=Unit"` to get a fast feedback loop while doing the larger refactors in Phases 3–4. Phase 5 must come last so the new rules don't fight with intermediate in-progress states.

---

## Phase 1: Categorize

### What changes

Add two `[Trait]` attributes to every `[Fact]` and `[Theory]` in 87 spec files. The trait vocabulary is fixed (see below). No file is moved, no production code changes, no assertions change.

### Trait vocabulary

**`Speed`** — measures how long a single test instance takes to run.

| Value | Meaning | When to use |
|-------|---------|-------------|
| `Unit` | No I/O. Pure functions, in-memory state. < 50 ms typical. | No fixture, no DB, no network, no grain. |
| `Grain` | Talks to Orleans grains + EF SQLite in-memory via `WorkflowGrainFixture`. | Inherits from `WorkflowGrainSpecs` or uses `WorkflowGrainFixture` / `BacklogFixture`. |
| `Integration` | Goes through `WebApplicationFactory<Program>` + HTTP client. | Uses `MohistIntegrationFixture.Client` or `MohistIntegrationCollection`. |
| `Service` | Talks to EF + Orleans DI container but skips `WebApplicationFactory`. | Uses `MohistDbCollection` (introduced in Phase 2). |

**`Sut`** — names the Bounded Context the test exercises. The names mirror the production directory layout in `packages/server/src/Mohist.Server/`:

| Value | Production namespace |
|-------|---------------------|
| `Workflow` | `Mohist.Server.Workflow.*` |
| `Issue` | `Mohist.Server.Issue.*` |
| `Project` | `Mohist.Server.Project.*` |
| `Epic` | `Mohist.Server.Epic.*` |
| `Runner` | `Mohist.Server.Runner.*` |
| `AgentSession` | `Mohist.Server.Sessions.*` |
| `Skills` | `Mohist.Cli.Skills.*` (CLI side) |
| `System` | `Mohist.Server.SystemInfo.*` |
| `Api` | HTTP API contracts / shape |
| `Architecture` | ArchUnitNET rules |
| `Foundation` | Cross-cutting (`PromptTemplateEngine`, `VariableBundle`, `EventBus`, etc.) |

A spec can carry multiple `Sut` values (`[Trait("Sut", "Issue")]` + `[Trait("Sut", "Workflow")]` for cross-cutting specs). This is the standard xUnit `[Trait]` capability.

### Per-spec rule

Apply the rules mechanically. The exact mapping for the existing 87 files is captured in the verification script below; here is the rule:

```
if spec class inherits from WorkflowGrainSpecs or uses WorkflowGrainFixture / BacklogFixture:
    Speed = Grain
elif spec class uses MohistIntegrationFixture.Client:
    Speed = Integration
elif spec class uses MohistIntegrationFixture (any) but never uses .Client:
    Speed = Service   (will become truly Service after Phase 2 moves it off Integration)
else:
    Speed = Unit

Sut is determined by the production namespace the spec's tests exercise.
Cross-cutting specs (test exercises ≥ 2 namespaces) get multiple Sut traits.
```

### Where the attributes go

```csharp
public class IssueDomainSpecs
{
    [Fact]
    [Trait("Speed", "Unit")]
    [Trait("Sut", "Issue")]
    public void StartWorkflow_MarksIssueInProgress() { ... }
}
```

The traits are added **per `[Fact]`, not per class**, because xUnit's filter only sees per-test attributes. Putting them on the class with `[Trait]` is technically possible but doesn't propagate to test methods. To keep the per-test attribute verbose, we use a code-behind helper **only if it reduces noise** (e.g., a `[Fact, Unit, Issue]` shortcut). If it doesn't, raw attributes stay.

### Files modified

- All 87 files in `packages/server/tests/Mohist.Server.Tests/Specs/` (mechanical trait addition).
- One new file: `packages/server/tests/Mohist.Server.Tests/Support/Traits.cs` documenting the vocabulary.

### Verification

```bash
# Must pass:
dotnet test --filter "Speed=Unit"           # ~285 tests, < 30s
dotnet test --filter "Speed=Grain"          # ~148 tests, < 90s
dotnet test --filter "Speed=Integration"    # ~227 tests, < 180s
dotnet test --filter "Speed=Service"        # 0 tests for now (Phase 1 still uses Integration)
dotnet test --filter "Sut=Workflow"         # ~300 tests across Speed=Grain/Integration
dotnet test                                  # full suite, ~300s, all pass
```

The Phase 1 commit message must include the timing measurements captured on the dev machine.

---

## Phase 2: Fixture Split

### What changes

Add a `MohistDbFixture` that gives D-class specs DI + EF + Orleans without the cost of `WebApplicationFactory`. Migrate 5–7 spec files from `MohistIntegrationCollection` to `MohistDbCollection`. The existing `MohistIntegrationFixture` is unchanged.

### Why a new fixture, not just remove D-class from the collection

xUnit collection fixtures work like this: every class in the collection gets a *constructor parameter* typed as the fixture. If we add `MohistDbCollection` and move 5 specs to it, those 5 specs see `MohistDbFixture` in their constructor (not `MohistIntegrationFixture`). The other 21 specs in `MohistIntegrationCollection` keep seeing `MohistIntegrationFixture`. The two collections are independent and can run in parallel (xUnit runs different collections concurrently by default).

### Layout

```
packages/server/tests/Mohist.Server.Tests/Support/
├── MohistIntegrationFixture.cs       (existing; WebApplicationFactory)
├── MohistDbFixture.cs               (NEW; DI + EF + Orleans, no WebApplicationFactory)
└── MohistCollections.cs             (NEW; collection definitions only, replaces inline ones)
```

### `MohistDbFixture` design

```csharp
public sealed class MohistDbFixture : IAsyncLifetime
{
    public IServiceProvider Services { get; private set; } = null!;
    public IGrainFactory Grains { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;
    public RecordingEventStore EventStore => _sharedEventStore;
    public InMemoryEventBus EventBus => _sharedEventBus;

    private readonly InMemoryEventBus _sharedEventBus = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly RecordingEventStore _sharedEventStore = new();
    private SqliteConnection _keeper = null!;

    public Task InitializeAsync()
    {
        var dbName = $"mohist-dbspec-{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(ConnectionString);
        _keeper.Open();

        // Build the same DI graph as the real server, but in-process.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = ConnectionString,
                ["Mohist:RunnerRoot"] = Path.Combine(Path.GetTempPath(), $"mohist-runner-{Guid.NewGuid():N}"),
                ["Mohist:WebRoot"] = Path.Combine(Path.GetTempPath(), $"mohist-web-{Guid.NewGuid():N}"),
                ["Mohist:SystemUpdate:StatePath"] = Path.Combine(Path.GetTempPath(), $"mohist-sys-{Guid.NewGuid():N}.json"),
            })
            .Build());
        // ... same registrations as MohistServiceRegistration.AddMohistServerCore
        // (extract a shared `ConfigureMohistServices` method on the registration class)

        var siloBuilder = new InProcessTestClusterBuilder();
        siloBuilder.ConfigureSilo((_, sb) => { /* add services to silo */ });
        var cluster = siloBuilder.Build();
        cluster.DeployAsync().GetAwaiter().GetResult();

        Services = services.BuildServiceProvider();
        Grains = cluster.Client;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { _keeper.Dispose(); return Task.CompletedTask; }
}
```

The shared `ConfigureMohistServices` extension is extracted from `MohistServiceRegistration.AddMohistServerCore` so both the real server bootstrap and the test fixture use the same service graph. This also catches drift where production code adds a new service that tests forget to register.

### Specs to migrate (5–7)

Audit candidates — specs that use `MohistIntegrationFixture` but do **not** use `.Client`:

- `IssueQuerierSpecs` — uses `_fixture.Services.CreateScope()`
- `IssueWorkflowProfileManagerSpecs` — uses `_fixture.Services.CreateScope()`
- `ProjectWorkflowProfileManagerSpecs` — uses `_fixture.Services.CreateScope()`
- (continue audit at Phase 2 start; exact list captured in tasks.json)

These specs change from:

```csharp
[Collection("MohistIntegration")]
public class IssueQuerierSpecs
{
    public IssueQuerierSpecs(MohistIntegrationFixture fixture) { _fixture = fixture; }
    // uses _fixture.Services.CreateScope()
}
```

to:

```csharp
[Collection("MohistDb")]
public class IssueQuerierSpecs
{
    public IssueQuerierSpecs(MohistDbFixture fixture) { _fixture = fixture; }
    // uses _fixture.Services.CreateScope()  (unchanged code)
}
```

The `[Trait("Speed", "Integration")]` from Phase 1 also flips to `[Trait("Speed", "Service")]`.

### Files modified

- NEW: `packages/server/tests/Mohist.Server.Tests/Support/MohistDbFixture.cs`
- NEW: `packages/server/tests/Mohist.Server.Tests/Support/MohistCollections.cs` (consolidates collection definitions)
- MOD: `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs` — extract `ConfigureMohistServices` extension
- MOD: 5–7 spec files (move to `MohistDbCollection`, change trait to `Service`)

### Verification

```bash
dotnet test --filter "Speed=Service"   # should now show 5–7 specs, ~10s total (not 5min)
dotnet test                            # all 680+ pass
```

---

## Phase 3: Helper Extraction

### What changes

Three categories of helpers get extracted from spec files into `Support/`:

1. **`WorkflowGrainSpecs` abstract base** → 100+ lines of `protected` helpers → static class in `Support/`
2. **Inline `CreateXxx` methods** scattered across 30+ files → factories in `Support/TestData/`
3. **Common DB setup snippets** (e.g., `await using var db = new MohistDbContext(options)`) → a small `TestDb` helper

### 3.1 `WorkflowGrainSpecs` → static helper

Today, 21 spec files inherit from `WorkflowGrainSpecs`, which gives them access to:
- `_fixture` (the `WorkflowGrainFixture`)
- `Grains` (pass-through)
- `EventStore` (pass-through)
- `GetQuerier()` — manual DB context construction
- `RegisterRunnerAsync(...)` / `RegisterRunnerForProjectAsync(...)` — runner setup
- `CreateWorkflowAsync(...)` / `StartWorkflowAsync(...)` — workflow setup
- `TestInput(...)` / `TestProjectId(...)` — input builders
- `SeedLeaseAsync(...)` / `SeedWorkflowTemplateAsync(...)` / `SeedDefinitionAsync(...)` — DB seeding
- `DeactivateWorkflowAsync(...)` — grain lifecycle
- `_workflowId` / `_runnerId` — **mutable per-instance state shared across tests**

The mutable state is the worst part: it couples tests inside one class to a particular ordering. A test that doesn't set `_workflowId` will pick up the previous test's id.

**New design**: static helpers in `Support/WorkflowGrainTestHelpers.cs`:

```csharp
public static class WorkflowGrainTestHelpers
{
    public static async Task<string> RegisterRunnerAsync(
        IGrainFactory grains,
        string projectId,
        string? runnerId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    { ... }

    public static async Task<IWorkflowGrain> StartWorkflowAsync(
        IGrainFactory grains,
        MohistDbContext db,
        string workflowId,
        WorkflowDefinition definition,
        string projectId,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    { ... }

    // ... all other helpers, no instance state
}
```

`db` is now a parameter (or fetched from the fixture), so test ordering no longer depends on `_workflowId` mutation. Each test calls `StartWorkflowAsync(grains, db, id: "wf-foo", ...)` explicitly.

### 3.2 Test data factories

Create `Support/TestData/` with one file per major domain entity:

```
Support/TestData/
├── AgentSessionTestData.cs
├── WorkflowTestData.cs
├── IssueTestData.cs
├── ProjectTestData.cs
├── RepositoryTestData.cs
├── WorkflowDefinitionTestData.cs
└── Index.md   (catalog: which factory for which test scenario)
```

Each file is a static class with `Create*` methods that return both the domain object and its row (because tests often need both):

```csharp
public static class AgentSessionTestData
{
    public static (AgentSession Session, AgentSessionRow Row) CreateRunning(
        string projectId = "proj-test",
        int issueNumber = 1,
        string workflowRunId = "wr-test",
        string sessionName = "session-test",
        string? runnerId = "runner-test")
    { ... }

    public static (AgentSession Session, AgentSessionRow Row) CreateTerminal(
        AgentSessionStatusPhase phase = AgentSessionStatusPhase.Completed,
        ...)
    { ... }
}
```

Spec files change from inline `private static AgentSession CreateSession()` to `AgentSessionTestData.CreateRunning()`.

### 3.3 Common DB setup snippet

```csharp
public static class TestDb
{
    public static MohistDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new MohistDbContext(options);
    }
}
```

Replaces 4–5 inline `new DbContextOptionsBuilder<>().UseSqlite(cs).Options` snippets.

### Files modified

- NEW: `Support/WorkflowGrainTestHelpers.cs`
- NEW: `Support/TestData/AgentSessionTestData.cs`
- NEW: `Support/TestData/WorkflowTestData.cs`
- NEW: `Support/TestData/IssueTestData.cs`
- NEW: `Support/TestData/ProjectTestData.cs`
- NEW: `Support/TestData/RepositoryTestData.cs`
- NEW: `Support/TestData/WorkflowDefinitionTestData.cs`
- NEW: `Support/TestData/Index.md`
- NEW: `Support/TestDb.cs`
- MOD: 21 spec files that inherit from `WorkflowGrainSpecs` — remove inheritance, switch to static helper calls
- DELETE: `WorkflowGrainSpecs.cs` (or convert to a thin facade that re-exports the static helpers, for transition)
- MOD: 30+ spec files that have inline `CreateXxx` methods — replace with factory calls

### Verification

```bash
dotnet test --filter "Speed=Grain"   # all 21 spec files compile and pass
dotnet test                          # all 680+ pass
# Check that WorkflowGrainSpecs.cs (if kept) has no mutable fields
grep -n "_workflowId\|_runnerId" Specs/*Specs.cs
```

---

## Phase 4: Bounded-Context Layout

### What changes

Move 87 spec files from flat `Specs/` into subdirectories that mirror the production layout. Update namespaces to match. Update `using` directives in each file.

### Target directory layout

```
packages/server/tests/Mohist.Server.Tests/
├── Architecture/
│   └── ArchitectureRules.cs                  (unchanged, already at root)
├── Specs/
│   ├── Workflow/                             (NEW)
│   │   ├── Grain/
│   │   │   ├── WorkflowGrainLifecycleSpecs.cs
│   │   │   ├── WorkflowGrainRetrySpecs.cs
│   │   │   └── ...
│   │   ├── Querier/
│   │   │   └── WorkflowQuerierSpecs.cs
│   │   └── Api/
│   │       └── WorkflowEventApiSpecs.cs
│   ├── Issue/
│   │   ├── Domain/
│   │   │   └── IssueDomainSpecs.cs
│   │   ├── Grain/
│   │   │   └── IssueGrainSpecs.cs (if any)
│   │   ├── Querier/
│   │   │   ├── IssueQuerierSpecs.cs
│   │   │   └── IssueRepositoryResolverSpecs.cs
│   │   ├── Api/
│   │   │   ├── IssueApiSpecs.cs
│   │   │   ├── IssueRepositoryApiSpecs.cs
│   │   │   └── ...
│   │   └── Profile/
│   │       ├── IssueWorkflowProfileApiSpecs.cs
│   │       ├── IssueWorkflowProfileManagerSpecs.cs
│   │       └── MohistDefaultWorkflowProfileSpecs.cs
│   ├── Project/
│   │   ├── Grain/
│   │   │   └── ProjectGrainSpecs.cs
│   │   ├── Querier/
│   │   │   └── ProjectQuerierSpecs.cs (if needed)
│   │   └── Api/
│   │       ├── ProjectTemplateRoutesSpecs.cs
│   │       ├── RuntimeEntrySpecs.cs
│   │       └── ...
│   ├── Epic/
│   │   ├── Domain/
│   │   │   └── EpicLifecycleSpecs.cs
│   │   └── Api/
│   │       └── EpicApiSpecs.cs
│   ├── Runner/
│   │   ├── Grain/
│   │   │   ├── RunnerGrainSpecs.cs
│   │   │   ├── RunnerBindingSpecs.cs
│   │   │   ├── RunnerFailureSpecs.cs
│   │   │   ├── RunnerRegistrySpecs.cs
│   │   │   └── RunnerStatusProjectionSpecs.cs
│   │   └── Api/
│   │       └── RunnerStatusApiSpecs.cs
│   ├── AgentSession/
│   │   ├── Domain/
│   │   │   ├── AgentSessionDomainSpecs.cs
│   │   │   └── AgentSessionSpecs.cs
│   │   └── Api/
│   │       └── IssueSessionApiSpecs.cs
│   ├── Skills/                               (CLI / cross-cutting)
│   │   ├── SkillAssetManifestSpecs.cs
│   │   ├── SkillAssetServiceSpecs.cs
│   │   ├── SkillsCliRuntimeSpecs.cs
│   │   ├── SkillsCommandBehaviorSpecs.cs
│   │   ├── SkillsCommandRegistrationSpecs.cs
│   │   ├── SkillsContentSpecs.cs
│   │   ├── SkillsInstallSpecs.cs
│   │   ├── SkillAssetRootResolverSpecs.cs
│   │   └── UpdateInstallSyncSpecs.cs
│   ├── System/                               (SystemInfo + Config + Runtime)
│   │   ├── ConfigServiceSpecs.cs
│   │   ├── RuntimeBuildInfoSpecs.cs
│   │   ├── RuntimeSettingsSpecs.cs
│   │   ├── SystemInfoServiceSpecs.cs
│   │   ├── SystemdInstallDetectorSpecs.cs
│   │   ├── SystemUpdateServiceSpecs.cs
│   │   ├── UpdateSpecs.cs
│   │   ├── EventBridgeSpecs.cs
│   │   ├── EventBusSpecs.cs
│   │   ├── EventStoreSpecs.cs
│   │   └── DatabaseInitializationSpecs.cs
│   ├── Api/                                  (Cross-cutting API specs)
│   │   ├── ApiContractSpecs.cs
│   │   ├── TemplateRoutesSpecs.cs
│   │   ├── WorkflowEventApiSpecs.cs (moved from Workflow)
│   │   ├── WorkspaceSpecs.cs
│   │   └── ...
│   ├── Foundation/                           (Pure unit tests of cross-cutting types)
│   │   ├── PromptFrontmatterParserSpecs.cs
│   │   ├── PromptReferenceScannerSpecs.cs
│   │   ├── PromptTemplateEngineSpecs.cs
│   │   ├── VariableBundleSpecs.cs
│   │   ├── VariableScopeSpecs.cs
│   │   ├── WorkflowEventSerializationSpecs.cs
│   │   ├── BacklogSpecs.cs (or move to Workflow/Backlog/)
│   │   ├── StageLockSpecs.cs (or move to Workflow/Lock/)
│   │   ├── TaskRequiredFilesSpecs.cs
│   │   ├── BoundarySpecs.cs (or move to Workflow/Boundary/)
│   │   ├── WorkflowStateSpecs.cs
│   │   ├── WorkflowEventSpecs.cs
│   │   ├── WorkflowSessionSpecs.cs
│   │   └── ...
│   └── Shared/                               (Cross-cutting integration specs)
│       ├── BacklogCollection.cs (existing, moves here)
│       └── ...
├── Support/
│   ├── (existing files)
│   ├── MohistDbFixture.cs                    (NEW, from Phase 2)
│   ├── MohistCollections.cs                  (NEW, from Phase 2)
│   ├── WorkflowGrainTestHelpers.cs           (NEW, from Phase 3)
│   ├── TestDb.cs                             (NEW, from Phase 3)
│   ├── Traits.cs                             (NEW, from Phase 1)
│   └── TestData/                             (NEW, from Phase 3)
└── Mohist.Server.Tests.csproj
```

### Naming rule after Phase 4

A spec file's name must include the SUT name as a prefix (e.g., `WorkflowGrainLifecycleSpecs.cs`, not `AdvanceSpecs.cs`). Behavior-scenario names become suffix:

- Old: `AdvanceSpecs.cs`, `HappyPathSpecs.cs`
- New: `WorkflowGrainLifecycleSpecs.cs` (consolidate `Advance` + `HappyPath` + `Boundary` + `PausingWork` into one file with method names preserving the original behavior)

This is a deliberate consolidation: 21 behavior-named files become ~8 lifecycle-named files, each with multiple related tests. Original behavior is preserved in the test method names.

### Namespace rule

```csharp
namespace Mohist.Server.Tests.Specs.Workflow.Grain;       // for WorkflowGrainSpecs.cs
namespace Mohist.Server.Tests.Specs.Workflow.Querier;     // for WorkflowQuerierSpecs.cs
namespace Mohist.Server.Tests.Specs.Issue.Api;            // for IssueApiSpecs.cs
namespace Mohist.Server.Tests.Specs.Foundation;          // for VariableBundleSpecs.cs
```

Existing csproj globbing (`<Compile>` defaults) picks up all `.cs` files recursively, so no csproj change is required for the move. Verified in advance with `dotnet build`.

### Files modified

- 87 spec files relocated + namespace updates + `using` directive fixes
- csproj: no change expected (verified)
- `Architecture/ArchitectureRules.cs`: update `FeatureDirectories_ShouldOnlyContainDomainGrainsAndServices` rule to also tolerate the new subdirectory structure (or split into a separate rule for the test mirror)

### Verification

```bash
dotnet test                          # all 680+ pass
# Confirm directory layout matches plan:
find packages/server/tests/Mohist.Server.Tests/Specs -name "*.cs" | sort
# Confirm no file left at the flat root:
ls packages/server/tests/Mohist.Server.Tests/Specs/*.cs  # should be empty (or just index)
```

---

## Phase 5: Archtest Enforcement

### What changes

Add four archtest rules to `ArchitectureRules.cs`. They prevent regression of the new organization conventions.

### Rule 1: Spec files end with `Specs` or `Collection`

```csharp
[Fact]
public void SpecFiles_MustHaveSpecOrCollectionSuffix()
{
    var sourceRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Specs"));
    var specFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories);
    var violations = specFiles
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => !name.EndsWith("Specs") && !name.EndsWith("Collection") && name != "Index")
        .OrderBy(name => name)
        .ToList();
    Assert.True(violations.Count == 0,
        "Spec files must end with 'Specs' or 'Collection'. Violations: " + string.Join(", ", violations));
}
```

### Rule 2: Spec classes are `public`

```csharp
[Fact]
public void SpecClasses_MustBePublic()
{
    var sourceRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Specs"));
    // Roslyn-based: parse each .cs and assert that the *Specs class is public
    // (Skip for now if Roslyn is too heavy; this can be a regex pass.)
}
```

### Rule 3: Spec files don't exceed 600 lines

```csharp
[Fact]
public void SpecFiles_MustStayBellowSizeBudget()
{
    var sourceRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Specs"));
    var tooBig = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
        .Where(p => new FileInfo(p).Length > 24_000)  // ~600 lines × 40 chars
        .Select(p => Path.GetRelativePath(sourceRoot, p))
        .OrderBy(p => p)
        .ToList();
    Assert.True(tooBig.Count == 0,
        "Spec files must stay under ~600 lines. Too big: " + string.Join(", ", tooBig));
}
```

### Rule 4: Specs namespaces must be under `Mohist.Server.Tests.Specs.*`

```csharp
[Fact]
public void SpecNamespaces_MustBeUnderSpecs()
{
    var sourceRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Specs"));
    // Read each file's `namespace` declaration and assert it starts with Mohist.Server.Tests.Specs
}
```

### Files modified

- MOD: `Architecture/ArchitectureRules.cs` — add 4 new `[Fact]` rules

### Verification

```bash
# All 4 new rules pass:
dotnet test --filter "FullyQualifiedName~SpecFiles_MustHaveSpecOrCollectionSuffix"
dotnet test --filter "FullyQualifiedName~SpecFiles_MustStayBellowSizeBudget"
# ... etc

# Verify a deliberately-bad file is caught:
# (1) Create Specs/Bad/TestSpecs.cs (note: must end with Specs/Collection)
# (2) Run archtest — should fail with the bad file listed
# (3) Delete the file
```

---

## Cross-cutting concerns

### What does NOT change

- The four existing collection fixtures (`MohistIntegration`, `WorkflowGrain`, `Backlog`, `WorkflowEvents`) keep their semantics.
- The 680 existing test cases keep their assertions, their inputs, and their expected outputs.
- No production code is touched.
- No documentation is rewritten (the `docs/` directory stays the same).
- No new test project is created.

### What about the test file size growth?

Phase 4 is the only phase that might leave large spec files. The current `AgentSessionSpecs.cs` is 1067 lines; if we move it to `Specs/AgentSession/Domain/AgentSessionSpecs.cs` and the file is unchanged, it still fails the Phase 5 600-line rule. The solution: Phase 3 / Phase 4 should also **split large specs into multiple focused files** when relocating them. Specifically:

| Spec | Current size | Split target |
|------|--------------|--------------|
| `AgentSessionSpecs.cs` | 1067 | 3 files by lifecycle: `SetupSpecs` / `TelemetrySpecs` / `EndOfRunSpecs` |
| `IssueRepositoryResolutionRegressionSpecs.cs` | 1062 | 3 files by scenario: `ExplicitNameSpecs` / `DefaultFallbackSpecs` / `AmbiguitySpecs` |
| `UpdateSpecs.cs` | 654 | 2 files: `UpdateCommandSpecs` / `UpdateStatusSpecs` |
| `IssueQuerierSpecs.cs` | 638 | 2 files: `ListSpecs` / `EnrichmentSpecs` |
| `SystemUpdateServiceSpecs.cs` | 613 | 2 files: `BuildSpecs` / `ReconnectSpecs` |
| `MohistDefaultWorkflowProfileSpecs.cs` | 575 | 2 files: `ProjectionSpecs` / `PromptMergeSpecs` |
| `WorkflowProjectionSpecs.cs` | 483 | keep as is, near limit |
| `WorkflowGrainSpecs.cs` | 467 | refactored to static helper (Phase 3) — shrinks |

The Phase 4 commit should include these splits alongside the directory moves.

### What if a test fails after the refactor?

The 5 phases are designed so the **green-bar invariant holds at every commit boundary**. If a test fails, the change is wrong. The Phase ordering is constructed so that:

- Phase 1 changes only attribute lines → trivially green
- Phase 2 changes only fixture wiring → if a D-class spec breaks, the service graph differs from the real server
- Phase 3 changes only helper call sites → if a grain spec breaks, the helper extraction changed semantics
- Phase 4 changes only file paths and namespaces → build errors are the only possible failure mode
- Phase 5 changes only archtest rules → no spec test changes behavior

### Compatibility with existing CI

- The existing `npm test` script (`dotnet test` under the hood) keeps working unchanged.
- The new `[Trait]` filters work in any xUnit v2 runner (current `xunit.runner.visualstudio 2.8.2` supports them).
- No new package dependencies.
- No new MSBuild targets.
- No changes to `Directory.Build.props` or `Directory.Packages.props`.

---

## Open questions to confirm before Phase 2

1. **D-class spec list**: do the 5–7 specs identified above match your understanding, or are there other specs that should also move off `MohistIntegrationCollection`? The audit is mechanical: grep for `MohistIntegrationFixture` minus `Client`.

2. **BacklogCollection / WorkflowEventsCollection**: these are one-off collections with single spec files. Phase 2 may fold them into `WorkflowGrainCollection` if they share `WorkflowGrainFixture`. Confirm the single-spec collection is a feature (test isolation) or a bug (over-isolation).

3. **WorkflowGrainSpecs file deletion vs facade**: the abstract base has been a useful shim for 21 specs. Phase 3 proposes to delete it after helper extraction. The alternative is keeping it as a thin facade that delegates to static helpers. Confirm the deletion is desired.

4. **Spec file split in Phase 4**: 3 large specs get split (AgentSessionSpecs, IssueRepositoryResolutionRegressionSpecs, UpdateSpecs). If you'd rather defer the split to a later change, Phase 5's 600-line rule is a soft check that can be relaxed to a "todo" comment for now.

5. **`UpdateInstallSyncSpecs.cs`**: this file currently has no `public class *Specs` (it's a helper that the *content* of Skills*Specs uses). Confirm whether it stays as a helper class or gets folded into a real `Specs` class.

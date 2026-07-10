# Make .NET test cave simple and remove tricky scheduler

> **Executor rule**: grug read whole plan before touch code. grug do one chunk,
> run gate, make green, then next chunk. no grand rewrite. no clever replacement
> for old clever thing. if STOP condition happen, grug stop and report. grug not
> invent new abstraction because build red.
>
> **Drift check first**:
>
> ```bash
> git diff --stat a50cd9775..HEAD -- \
>   Mohist.sln \
>   design/testing.md \
>   scripts/analyze-spectests-trx.py \
>   packages/server/src/Mohist.Server/AssemblyInfo.cs \
>   packages/server/tests
> ```
>
> if these files change much from facts below, stop. update plan first. do not
> force old plan onto new cave.

## Status

- **Priority**: P1
- **Effort**: L, many small green commits
- **Risk**: MED
- **Depends on**: none
- **Planned at**: commit `a50cd9775`, 2026-07-10
- **Implementation correction**: classify files by actual dependencies, not
  their old traits. SQLite/EF event-store specs belong in ComponentSpecs; the
  five fixture-free deterministic specs listed below belong in UnitTests.

## Grug goal

grug want test location tell truth:

| Cave | What live here |
|---|---|
| `Mohist.Server.UnitTests` | pure code. no DB. no Orleans host. no ASP.NET host. |
| `Mohist.Server.ComponentSpecs` | SQLite, direct service, `InProcessTestCluster`. |
| `Mohist.Server.IntegrationSpecs` | `WebApplicationFactory<Program>`, HTTP, real app composition. |
| `Mohist.Server.ArchTests` | structure guard only. |
| `Mohist.Server.TestSupport` | small shared deterministic rocks. not test project. |

when done:

- no `CostDescendingCollectionOrderer`;
- no collection cost map;
- no TRX script needed to decide execution order;
- no numbered cost shard like `Issue2`, `Issue3`, `WorkflowGrain2`;
- no Speed or SUT trait mountain on every test;
- no test project reference another test project;
- every behavior matrix has one lowest useful owner;
- `OtelTracing` is the only tracing exception to parallel execution where a
  test assembly registers a process-global provider; `ConsoleOutput` is the
  Unit-only process-global writer exception.
- missing source folder make ArchTests fail, not quietly say ok;
- full solution still green and not more than 10% slower than clean baseline.

grug accept small temporary slowdown during move. grug not accept permanent
complexity demon for save one shiny second.

## Why cave bad now

current `Mohist.Server.SpecTests` mix four beasts in one cage:

- HTTP application host;
- Orleans grain cluster;
- EF/service specs;
- fixture-free tests.

then scheduler must guess which beast eat first. custom orderer born. numbered
collections born. cost script born. complexity demon get food.

facts at planned commit:

- 232 spec files;
- about 95,000 lines;
- 2,545 `[Fact]` / `[Theory]` declarations;
- 90 Integration-only files;
- 72 Service-only files;
- 56 Grain-only files;
- four Unit-only files;
- three mixed Integration + Service files;
- seven files with no Speed trait;
- 2,324 Speed traits and 4,758 SUT traits;
- 47 files bigger than 24KB. byte count is inventory, not defect by itself;
- ten `MohistIntegrationFixture` collections plus one Otel class fixture.

Phase 2 try smarter weight table. result worse by 1.34 seconds. grug nod. big
brain scheduling not answer.

## Grug rules

- project name say test level;
- folder and class name say product area;
- collection only say shared lifetime or real isolation;
- lowest useful layer own big behavior matrix;
- IntegrationSpec prove HTTP/process boundary, not every data permutation;
- no new skip;
- no product behavior change;
- no global mutable host pool;
- no linked source files between projects;
- no test project reference another test project;
- no timing data in collection name or source code;
- no partial class trick to hide giant spec;
- no file-size allowlist or byte cap. split only at a real behavior boundary.

## Test ownership

### UnitTests

grug put here:

- pure transformation;
- parser and mapper;
- domain invariant;
- deterministic classification.

no DB. no service provider. no grain. no HTTP.

### ComponentSpecs

grug put here:

- EF query and store with cloned in-memory SQLite;
- Orleans behavior with `InProcessTestCluster`;
- service orchestration with simple fake;
- date/status/retry/pagination/state matrix.

### IntegrationSpecs

grug put here:

- route and HTTP verb;
- binding and status code;
- JSON shape;
- middleware and production DI wiring;
- one useful success per endpoint contract;
- one useful HTTP-only failure per different mapping;
- few full flows needing HTTP + host + grain + DB together.

if test only proves math or state transition, integration cave wrong cave.

## Files need special move

these four use SQLite/EF and stay in ComponentSpecs. old Speed.Unit trait did
not make them unit tests:

- `Specs/Events/AgentSessionTransactionalEventAppendSpecs.cs`
- `Specs/Events/EventStoreScopedAppendSpecs.cs`
- `Specs/Events/IssueTransactionalEventAppendSpecs.cs`
- `Specs/Events/TransactionalEventAppendSpecs.cs`

these two use component/grain support. move ComponentSpecs:

- `Specs/Agent/Grain/AgentGrainSpecs.cs`
- `Specs/Workflow/WorkflowGrainSpecs.cs`

these five are fixture-free deterministic behavior. move UnitTests and rename
`Specs` to `Tests` in file, class, namespace:

- `Specs/Epic/Domain/EpicProgressBuildSpecs.cs`
- `Specs/Epic/Domain/EpicQuerierExternalPrerequisitesSpecs.cs`
- `Specs/Sessions/TranscriptAccumulatorSpecs.cs`
- `Specs/Workflow/Grain/RuntimeVariableMergeSpecs.cs`
- `Specs/Workflow/Grain/TaskOutputCaptureSpecs.cs`

these two host-backed Otel files start in IntegrationSpecs because process-global
tracing state:

- `Specs/SystemSpecs/Otel/OtelInboundHttpTracingSpecs.cs`
- `Specs/SystemSpecs/Otel/OtelSourceSubscriptionSpecs.cs`

`Specs/SystemSpecs/Otel/OtelOrleansSourceNameSpecs.cs` stays ComponentSpecs:
it uses the Component `BacklogFixture` and has no HTTP boundary. It remains in
the local `OtelTracing` collection because the ActivitySource state is process
global.

## Scope

grug may change:

- `design/testing.md`
- `Mohist.sln`
- `scripts/analyze-spectests-trx.py` (delete near end)
- `packages/server/src/Mohist.Server/AssemblyInfo.cs`
- all active server test projects;
- new ComponentSpecs, IntegrationSpecs, TestSupport projects;
- this plan execution record.

grug not change:

- product behavior;
- public API response;
- xUnit / EF / Orleans / WebApplicationFactory choice;
- existing skip debt, unless reviewer approve exact duplicate removal;
- Node tests;
- archived OpenSpec evidence;
- CI machine count or `bin/obj` cache.

current main worktree may have user change in
`CostDescendingCollectionOrderer.cs`. grug not touch, stash, reset, or revert
user rock. use clean worktree from committed HEAD.

## Commands grug need

```bash
dotnet build Mohist.sln -p:SkipWebBuild=true
dotnet test Mohist.sln -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.ComponentSpecs/Mohist.Server.ComponentSpecs.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.IntegrationSpecs/Mohist.Server.IntegrationSpecs.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.ArchTests/Mohist.Server.ArchTests.csproj \
  -p:SkipWebBuild=true --no-build
git diff --check
```

final searches:

```bash
rg -n 'ITestCollectionOrderer|TestCollectionOrderer|CostDescendingCollectionOrderer|NamedCollectionCost|SlowDefaultClasses' \
  packages/server/tests design scripts

rg -n 'Traits\.(Speed|Sut)|\[Trait\(' \
  packages/server/tests/Mohist.Server.ComponentSpecs \
  packages/server/tests/Mohist.Server.IntegrationSpecs

rg -n '\[Collection\("[^"]*[0-9]+"\)\]|\[CollectionDefinition\("[^"]*[0-9]+"' \
  packages/server/tests/Mohist.Server.ComponentSpecs \
  packages/server/tests/Mohist.Server.IntegrationSpecs -g '*.cs'

rg -n 'ProjectReference.*(UnitTests|ComponentSpecs|IntegrationSpecs|ArchTests)' \
  packages/server/tests -g '*.csproj'
```

all final searches print nothing.

## Work chunk: know fence before club

grug respect ugly fence. maybe fence stop cow. first measure.

- use clean worktree;
- build once;
- run old SpecTests twice;
- run full solution twice;
- record total/pass/skip;
- record two four-core timings:

```bash
/usr/bin/time -f 'elapsed=%e user=%U sys=%S' \
  taskset -c 0-3 dotnet test Mohist.sln \
  -p:SkipWebBuild=true --no-build
```

put TRX under `/tmp`. do not commit result file.

gate: baseline green twice. if not, stop.

## Work chunk: write simple cave law

update `design/testing.md` first:

- define five caves from target table;
- project placement replace Speed trait;
- no test project reference another;
- TestSupport contain no fixture/collection/host/test;
- lowest useful layer own behavior matrix;
- integration not repeat component matrix;
- collection name describe capability/lifetime, not speed;
- custom orderer marked temporary debt, removed by this plan.

gate:

```bash
rg -n 'ComponentSpecs|IntegrationSpecs|TestSupport|temporary|lowest' \
  design/testing.md
```

## Work chunk: split cave, keep every test

### Make small TestSupport crystal

create `Mohist.Server.TestSupport` normal class library. no Test SDK. no xUnit.
no fixture.

extract DB-only code from `GrainTestConfig` into `TestDatabaseSchema`:

- `CreateDbContext`;
- `Migrate`.

keep Orleans `ConfigureSilo` and reminder setup in ComponentSpecs.

move `MigratedSqliteTemplate` to TestSupport. it depend on
`TestDatabaseSchema`, not ComponentSpecs.

extract `FakePromptLoader` from giant
`MohistLocalWorkflowProfileSpecs.cs` into ComponentSpecs `Support/`. It has no
IntegrationSpecs caller, so it does not belong in TestSupport. Component setup
must not import a concrete spec.

move other fake only when both projects really use same fake. simple duplicate
may be better than big generic helper. grug fear premature DRY.

TestSupport forbidden things:

- `Fact`, `Theory`, `CollectionDefinition`;
- `IAsyncLifetime`;
- `WebApplicationFactory`;
- classes ending `Specs` or `Tests`.

### Make ComponentSpecs

- add xUnit, Test SDK, Orleans TestingHost, FakeTimeProvider;
- reference Server, CLI, TestSupport;
- move Service-only, Grain-only, the four SQLite/EF event-store files, and
  the two component-only files listed above;
- move component fixtures into local `Support/`;
- keep old collection names for now;
- copy the current runner config temporarily so the project split does not
  also change parallelism;
- keep required template/skill data copy.

### Move five Unit files

move the five fixture-free files listed above. rename `Specs` to `Tests`. no behavior
change.

### Make IntegrationSpecs

- add xUnit, Test SDK, Mvc.Testing, SignalR client, Orleans TestingHost;
- reference Server, CLI, TestSupport;
- move Integration-only and three mixed Otel files;
- move integration fixture and host helpers local;
- `OtelTracing` is Integration's only disabled-parallel collection;
- keep committed simple orderer temporary;
- copy the current runner config temporarily so the project split does not
  also change parallelism;
- never copy uncommitted weighted map;
- keep required runtime asset copy.

### Switch solution

- add new projects to solution;
- update `InternalsVisibleTo` only where build prove need;
- remove old SpecTests project after all files physically moved;
- no linked file;
- no test project reference another.

### Prove no test fall in crack

make TRX before and after. compare normalized discovery identities and
total/pass/skip: strip the project namespace prefix, and map the five renamed
unit classes from `Specs` to `Tests` before comparing. Keep a file/class/method
mapping for every rename.

gate:

- all projects green alone;
- solution green;
- old SpecTests discovered set equals new Unit + Component + Integration set
  after the documented normalization;
- no missing test;
- no duplicate test;
- no new skip.

if mismatch, stop. do not wave hand.

## Work chunk: make heavy host less dumb

in `MohistIntegrationFixture.InitializeAsync`:

- open keeper;
- immediately call `MigratedSqliteTemplate.CopyTo(_keeper)`;
- then start WebApplicationFactory.

do not change production `Program.cs`. production still call `Migrate()`. copied
history make call cheap no-op.

add `IntegrationFixtureSchemaSpecs.cs` in existing `IntegrationMisc` collection.
test these rocks exist:

- Attachments table/index;
- LabelDefinitions unique index;
- WorkflowRuns Status, AssignedWorkerId, ReadySince, indexes.

when test green, delete `EnsureSchemaAsync` and duplicate DDL. one schema truth.

`DatabaseInitializationSpecs` still start empty DB. migration chain is thing it
tests.

then narrow fixture:

- replace raw `Services.CreateScope()` repeated code with one DB callback helper;
- helper only give scoped `MohistDbContext`, not generic service locator;
- prefer public API setup when simple;
- move pure DB setup to the callback; do not build a fixture-wide service facade;
- a composition test may resolve the concrete fake or host service it asserts;
- keep `Client`, fake time, and `Grains` for real full flow.

gate: IntegrationSpecs green. pure DB setup uses the narrow callback and no
generic fixture service helper appears.

## Work chunk: put test in right cave

start with obvious pair:

- `IssueMetricsApiSpecs` (53 API methods);
- `IssueMetricsQuerierSpecs` (72 component methods).

component own all calculation matrix. integration keep:

- one good JSON contract per metrics endpoint;
- invalid bucket/range gives 400;
- useful unknown-project 404;
- null versus zero JSON when API shape care;
- one range override;
- removed route 404.

before delete API test, name component test that own behavior. if none, add
component test first.

then do same by domain:

- Issue/profile;
- Workflow/Epic;
- Session/Agent;
- Runner/general API;
- System/Otel.

split file when it holds different product abilities or incompatible setup. no
partial class. no mega base fixture. small local TestFactory ok when two sibling
files use it. byte count can point at a reading problem; it does not decide one.

record every removed integration test in table below.

gate after each domain: affected projects green, no new skip, and each moved
matrix has a named lower owner.

## Work chunk: give collection honest name

remove cost names:

- no `MohistIntegration2`;
- no `IntegrationIssue2` / `IntegrationIssue3`;
- no `WorkflowGrain2` / `WorkflowGrain3`.

use meaning names, example:

- Issue lifecycle / repository / profile integration;
- Workflow execution / recovery / coordination / artifacts;
- Runner grain, Agent job grain, Backlog, persistence, event publishing;
- System and Telemetry integration;
- OtelTracing.
- ConsoleOutput for process-global Console writer replacement.

do not balance by stopwatch. semantic collection allowed uneven.

run each renamed collection once. run ComponentSpecs and IntegrationSpecs five
times. if state leak appear, fix ownership. do not make new number shard.

gate: no numeric collection search result. five runs green.

## Work chunk: club tricky things

now fence purpose gone. grug swing club.

delete:

- `CostDescendingCollectionOrderer.cs`;
- assembly `TestCollectionOrderer` attribute;
- `scripts/analyze-spectests-trx.py`;
- order/cost comments and tests;
- all active Speed and SUT traits, including ArchTests;
- Traits type when no caller.

targeted run now use project path or `FullyQualifiedName~Thing`.

do not edit archived OpenSpec history.

gate: scheduler and trait searches print nothing. all tests green.

## Work chunk: make guard real

ArchTests currently look wrong folder then return. complexity demon wear guard
costume.

fix:

- ArchTests csproj emit one repo-root assembly metadata value from
  `Directory.Packages.props` location;
- one `RepositoryPaths` helper read and validate root;
- all source rules use helper;
- delete every `if (!Directory.Exists(root)) return;`;
- update final namespaces.

use `XDocument` for csproj checks. no regex XML soup.

guards must enforce:

- test roots exist;
- spec file public and in the right namespace;
- no test project reference another;
- UnitTests no Mvc.Testing or Orleans TestingHost;
- ComponentSpecs no Mvc.Testing/WebApplicationFactory;
- TestSupport not test project and contain no test/fixture/collection;
- no custom orderer;
- no Speed/SUT trait;
- only OtelTracing and ConsoleOutput disable parallel.

run ArchTests from repo root and from ArchTests directory. both pass and inspect
same roots.

## Work chunk: choose boring native thread setting

measure two states only:

- no `xunit.runner.json`;
- only `maxParallelThreads: 8`.

two clean four-core runs each. no custom order. no parallel algorithm tweak.

prefer no config when medians within 5%. keep thread cap only if full solution
elapsed improve >5%. `/usr/bin/time` user/sys is diagnostic only: `dotnet test`
fans out child processes, so its parent-process accounting is not a stable gate.
write one sentence in `design/testing.md` if cap stay.

if both bad, stop. fix wrong test level or fixture cost. no orderer resurrection.

## Final gate

run:

- build once;
- every .NET test project alone;
- full solution twice normal;
- full solution twice four-core;
- all final searches;
- `git diff --check`.

accept when:

- no new failure;
- no new skip;
- every deleted integration test has named lower owner;
- elapsed median <= baseline * 1.10;
- no custom orderer anywhere.

if 10% gate fail, mark BLOCKED with project timings. do not bring trick back.

## Execution record

### Thread rocks

| State | Run | elapsed | reported user + sys | total/pass/skip |
|---|---:|---:|---:|---|
| `a50cd9775` baseline, fixed four cores, `maxParallelThreads: 8` | 1 | 40.58s | 2.23s | 5035 / 5023 / 12 |
| `a50cd9775` baseline, fixed four cores, `maxParallelThreads: 8` | 2 | 35.35s | 95.69s | 5035 / 5023 / 12 |
| `maxParallelThreads: 8`, fixed four cores | 1 | 33.93s | 114.31s | 5006 / 4994 / 12 |
| `maxParallelThreads: 8`, fixed four cores | 2 | 32.62s | 61.33s | 5006 / 4994 / 12 |
| xUnit default threads, fixed four cores | 1 | 43.22s | 147.30s | 5006 / 4994 / 12 |
| xUnit default threads, fixed four cores | 2 | 32.38s | 111.32s | 5006 / 4994 / 12 |

final eight-thread median is 33.28s; baseline median is 37.97s. final is about
12 percent faster. default-thread median is 37.80s, so grug keep this one boring
cap. reported CPU varies because `dotnet test` child-process accounting varies;
it is not a gate. grug still no orderer, cost table, or number shard.

### Coverage rocks moved down

| Removed integration matrix | Component owner or HTTP-contract reason | State |
|---|---|---|
| Completion bucketing, terminal-event, totals, and range cases | `IssueCompletionBucketsQuerierSpecs` and `IssueCompletionWindowQuerierSpecs` own calculation; `IssueMetricsApiSpecs` keeps default/day, week/range, and invalid-bucket HTTP contracts | implemented in worktree |
| Approval wait samples, statistics, and range cases | `IssueApprovalWaitQuerierSpecs` owns calculation; Integration keeps nullable JSON contract | implemented in worktree |
| Quality classification, lifecycle, trend, previous-window, and range cases | `IssueQualityClassificationQuerierSpecs`, `IssueQualityLifecycleQuerierSpecs`, `IssueQualityTrendQuerierSpecs`, and `IssueQualityWindowQuerierSpecs` own calculation; Integration keeps empty nullable JSON/trend contract | implemented in worktree |
| Delivery points, cycle values, previous average, and range cases | `IssueDeliveryTimePointsQuerierSpecs`, `IssueDeliveryTimePreviousWindowQuerierSpecs`, and `IssueDeliveryTimeWindowQuerierSpecs` own calculation; Integration keeps null-versus-zero JSON contract | implemented in worktree |
| Stage attempts, aggregation, window, and range cases | `IssueStageDurationAttemptsQuerierSpecs`, `IssueStageDurationAggregationQuerierSpecs`, and `IssueStageDurationWindowQuerierSpecs` own calculation; Integration keeps non-empty aggregate DTO mapping | implemented in worktree |

## Done checklist

- [x] project caves exist and old SpecTests project gone;
- [x] Stage split preserved discovery before deliberate test consolidation;
- [x] TestSupport contains no tests or fixtures;
- [x] integration fixture clones migrated template before host;
- [x] duplicate schema DDL is gone;
- [x] migrated pure DB setup uses the narrow fixture callback, not a service facade;
- [x] every removed integration matrix is mapped to a lower owner;
- [x] no numeric cost collection;
- [x] only OtelTracing and ConsoleOutput disable parallel;
- [x] no custom orderer or cost script;
- [x] no active Speed/SUT traits;
- [x] ArchTests fail on missing root;
- [x] all test project boundary guards pass;
- [x] all .NET tests green with no new skip;
- [x] final elapsed median is inside the 10% gate;
- [x] `design/testing.md` describes the boring final model;
- [x] `git diff --check` is clean;
- [x] this plan execution record is DONE; review repairs and validation are complete.

## STOP, grug confused

stop and report when:

- user change exist in file plan need touch;
- baseline fail twice;
- sharing seem require test-project reference or linked file;
- test disappear or duplicate after split;
- integration test deletion have no lower owner and not HTTP-only assertion;
- template not contain current schema;
- semantic collection expose state leak needing product change;
- active automation really use Speed/SUT filter;
- final no-orderer state more than 10% slower after cleanup;
- someone propose new cost map, numbered shard, global host pool, skip, or magic.

complexity demon offer shiny abstraction. grug say no.

## Simplification decision

grug found old 24KB hard gate make wrong kind of simple. it make one clean
ability split because bytes say so, then grug get many small files with hidden
story. no good. byte count is now only a smoke signal for reading review.

grug still split real mixed caves: metrics matrix, update behavior, workflow
profile behavior. ArchTests guard real boundaries, not byte count. no allowlist
needed because no byte gate exist.

## Deferred cleanup

existing Integration fixture DB callers still use `Services` or
`ConnectionString` in older specs. move those in a separate pass: migrate pure
DB setup to `UseDbAsync`, then replace the small number of real host-composition
probes with named access. do not turn this into a generic fixture service API.

## Future grug remember

- choose test cave first, fixture second;
- integration test must prove process boundary worth cost;
- uneven semantic collection okay;
- never put stopwatch result in architecture;
- TestSupport stay small crystal, trap complexity inside, no junk drawer;
- one repo-root helper. one schema truth. one obvious path.

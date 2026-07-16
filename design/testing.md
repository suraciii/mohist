# Testing

All packages (server, runner, web, cli).

## Tracks

| Track | Verifies | Integration | Placement |
|---|---|---|---|
| Spec | product behavior / user flow | high: through product entry point | near product surface |
| Unit | single module / class / function | low: all collaborators faked | near code under test |
| Browser | real layout and browser interaction | production Web build in Chromium; API and Hub responses controlled | `packages/web/tests/browser/` |

Architecture tests (ArchTests) are a separate category: verify structure, not behavior.

Browser tests are Web-only. They verify behavior that requires a real layout and
interaction engine. They do not exercise Server, persistence, or a real network,
so they are not end-to-end tests.

Track expressed by naming + directory (not runtime trait):

| Side | unit | spec |
|---|---|---|
| C# | `*Tests.cs`, UnitTests project | `*Specs.cs`, SpecTests by context dir |
| runner | `*.test.ts`, near src/ | `*.spec.ts` |
| web | `.test` = src/ collocated | `.spec` = tests/ dir |

One file = one subject under test.

### Test support naming

Name active test support by its responsibility:

| Name | Responsibility |
|---|---|
| `Fixture` | provides reusable test data or owns setup lifetime and cleanup |
| `TestHost` | hosts an application or runtime boundary |
| `Probe` | exposes render or hook state for assertions |
| `TestFactory` | constructs collaborators and their fakes |
| `TestSupport` / `TestUtils` | groups shared test support and helper functions |

Do not use `Harness` in active test filenames, identifiers, or descriptions.

## Hard rules

### 1. No real external environment

Must pass in container with no network, no git, no node, no opencode, empty HOME.

No real network, processes, git, shell, agent binaries, DB files, system services, env vars, or filesystem access.

- Tests never read or write the host filesystem, including temp directories, HOME, the current checkout, build output, or assembly content roots. There are no exceptions for tests whose production adapter is file-backed.
- Tests never instantiate or resolve physical filesystem adapters. Test composition roots use in-memory stores, catalogs, file providers, and named shared-memory SQLite only.
- Built-in prompts, templates, workflow definitions, skills, and static assets enter tests as embedded/generated catalogs or explicit in-memory values, never through source/output paths.
- Pure lexical path operations are allowed with fixed virtual roots. Host-derived paths (`GetTempPath`, current directory, HOME, `AppContext.BaseDirectory`, single-argument `GetFullPath`) are forbidden.
- `MemoryStream`, `StringReader` / `StringWriter`, in-memory `IFileProvider`, and in-memory SQLite are not filesystem access.

Inject fakes (see cheat sheet).

### 2. No real time

Fake clock not advancing = time logic never fires.

- Inject `TimeProvider` (C#) or `vi.useFakeTimers` / `now` param (TS).
- New or modified C# product time behavior reads through injected `TimeProvider`; composition roots may register `TimeProvider.System`. The compiler rejects local wall-clock APIs (`DateTime.Now` / `Today`, `DateTimeOffset.Now`) and scheduler-based waits. Existing direct UTC reads are migration debt; expand the product deny list to `UtcNow` only after their domain/store APIs accept an injected time source.
- C# tests use fixed timestamp constants or `FakeTimeProvider`. Direct wall-clock reads are forbidden even for seed data and tolerance assertions.
- No wall-clock waits: no `while(now<deadline)`, no `Delay`/`setTimeout`/`Sleep`, no `elapsed < N` asserts.
- C# tests never call `Task.Delay`, `Thread.Sleep`, `Task.Yield`, `SpinWait` or timer APIs. `Task.Yield` is a scheduler hint, not an awaitable completion condition or duration.
- Wait for an awaitable signal (`TaskCompletionSource` with `RunContinuationsAsynchronously`, callback, observer, channel) or advance fake time. If no signal exists, add one at the async boundary instead of polling harder.

### 3. Deterministic (no flaky)

Any order, any parallelism, 1000 reruns = same result.

- Timestamps: fixed constants (`2026-01-01T00:00:00Z`).
- No array/Object.keys/scheduling order dependencies.
- Parallel silos: random ports (TestClusterPortAllocator). Never hardcoded 11111/30000.
- Restore stubs: vitest auto-restore; fake timers close in afterEach.
- Never `it.skip` to hide flaky tests. Fix or delete.

Web tests run with `isolate: false`: test files share a worker module registry and must be order-independent.

- `vi.mock` is forbidden in web tests. HTTP uses MSW; the only module replacements are the `sonner` and `@microsoft/signalr` fakes declared by the Vitest config alias allowlist.
- MSW rejects and records every unhandled request; `afterEach` fails the owning test even when application code catches the request error. Every request must be handled deliberately.
- Unexpected `console.error` / `console.warn` output fails the test. Tests that intentionally exercise an error boundary or warning path must capture and assert that output locally.
- Mutable module singletons and test control ports must expose a reset seam. Register the reset in `afterEach`; never rely on another file's imports, handlers, fake state, timers, globals, storage, or execution order.
- Web tests must pass both the normal suite and shuffled execution. A shuffle failure is a state leak, not a seed-specific exception.

### 4. Fast and concise

| Track | Per test | Per file |
|---|---|---|
| Unit | < 50ms | < 300 LOC |
| Spec | < 500ms (hard cap 5s); collection ≤ 2min | < 800 LOC (C# 24KB enforced) |
| Browser | separate `npm run test:browser`; never in default `npm test` | |

Extract shared setup. One product ability = one test file. Migration splits: delete old file once equivalent coverage exists.

The lowest useful layer owns the behavior matrix. API/integration specs assert route, binding, status code, JSON shape, parameter parsing, and one success path per endpoint; state and calculation permutations belong to the querier/grain/domain specs below. Never repeat the lower layer's scenario matrix through HTTP — one behavior change must touch one test file, not two layers.

## Spec parallelism (server)

xUnit collection = scheduling unit; classes inside a collection run serially, so wall time = the longest class chain.

- Parallel by default. `DisableParallelization` only for true process-global state (today only OtelTracing: shared `Microsoft.AspNetCore` ActivitySource). Cluster-scoped state (`RunnerRegistryKeys.Global`, `ForceActivationCollection`, fixture `FakeTimeProvider`) is per-fixture, never a reason to serialize.
- Ports: WebApplicationFactory fixtures must allocate via TestClusterPortAllocator. InProcessTestCluster is in-memory transport — no ports, safe anywhere.
- Collections express shared fixture lifetime and isolation needs only — never speed or cost. No custom test orderer and no `xunit.runner.json` thread tuning: both were measured to sit within run-to-run noise and were removed. No runtime traits either; the track is expressed by naming + directory alone.
- Legacy debt: five numbered load shards (`WorkflowGrain2/3`, `MohistIntegration2`, `IntegrationIssue2/3`) predate this rule and are being replaced by semantic collections.
- Schema: tests never run `Migrate()` / `EnsureCreated()` from empty — clone via `MigratedSqliteTemplate.CopyTo` / `CopyTo(target)` / `CopyModelSchemaTo`. Sole exception: DatabaseInitializationSpecs (its subject is the chain itself).

## Guards (automated)

Existing:
- ArchTests: layer deps, spec naming, namespace, public, and analyzer wiring backstops.
- `BannedApiAnalyzers`: compile-time enforcement of product and test API deny lists; test projects additionally ban wall-clock, scheduler-based waiting, host paths, physical adapters, and real filesystem APIs.
- EnvironmentAbstractions BannedApiAnalyzer: compile-time ban on direct env reads.
- vitest: `isolate: false`; restoreMocks, unstubGlobals, unstubEnvs auto; projects by suffix.
- web boundary guards: `vi.mock` ratchet locked at zero; MSW unhandled requests fail; weekly shuffled suite records a reproducible seed.

Planned:
- UnitTests csproj backstop: ban heavy fixtures (WebApplicationFactory, Orleans.TestingHost).
- C# product deny-list expansion after direct `DateTime.UtcNow` / `DateTimeOffset.UtcNow` reads are migrated to `TimeProvider`.
- ESLint: ban `child_process`, real `@microsoft/signalr` import in tests.

## Fake quick reference

| Dependency | server | runner | web | cli |
|---|---|---|---|---|
| time | FakeTimeProvider | vi.useFakeTimers | same as runner | seam missing |
| HTTP | WebApplicationFactory + TestServer | vi.stubGlobal('fetch') | MSW | RecordingHttpHandler |
| SignalR | RecordingRunnerHubContext | vi.mock('@microsoft/signalr') | config alias → tests/support/signalr-fake.ts | — |
| notification | — | — | config alias → tests/support/sonner-fake.ts | — |
| process | — | fake OpenCodeRuntime / SDK server factory | — | FakeCommandExecutor |
| DB | in-memory SQLite, clone from MigratedSqliteTemplate.CopyTo (no Migrate()) | — | — | fake IOtelQueryExecutor |
| grain | InProcessTestCluster, ControllableReminderTable | — | — | — |
| render | — | — | customRender (tests/test-utils.tsx) | — |
| file/data | Support/TestData/* | tests/support/* | tests/support/* | FakeFileSystem |

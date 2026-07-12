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

No real network, processes, git, shell, agent binaries, DB files, system services, env vars.

Inject fakes (see cheat sheet). Temp dirs allowed.

### 2. No real time

Fake clock not advancing = time logic never fires.

- Inject `TimeProvider` (C#) or `vi.useFakeTimers` / `now` param (TS).
- No wall-clock waits: no `while(now<deadline)`, no `Delay`/`setTimeout`/`Sleep`, no `elapsed < N` asserts.
- Use awaitable signals or fake timer advance.

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

## Spec parallelism (server)

xUnit collection = scheduling unit; classes inside a collection run serially, so wall time = the longest class chain.

- Parallel by default. `DisableParallelization` only for true process-global state (today only OtelTracing: shared `Microsoft.AspNetCore` ActivitySource). Cluster-scoped state (`RunnerRegistryKeys.Global`, `ForceActivationCollection`, fixture `FakeTimeProvider`) is per-fixture, never a reason to serialize.
- Ports: WebApplicationFactory fixtures must allocate via TestClusterPortAllocator. InProcessTestCluster is in-memory transport — no ports, safe anywhere.
- Sharding: big collections split into numbered partitions (`Name` / `Name2` / …, same fixture type, same semantics). A chain longer than ~10 classes gets split.
- Scheduling: CostDescendingCollectionOrderer runs named (fixture-backed) collections first; `xunit.runner.json` sets `maxParallelThreads: 8` (wait-heavy load, oversubscribe cores).
- Schema: tests never run `Migrate()` / `EnsureCreated()` from empty — clone via `MigratedSqliteTemplate.CopyTo` / `CopyTo(target)` / `CopyModelSchemaTo`. Sole exception: DatabaseInitializationSpecs (its subject is the chain itself).

## Guards (automated)

Existing:
- ArchTests: layer deps, spec naming, 24KB budget, namespace, public.
- BannedApiAnalyzer: compile-time ban on direct env reads.
- vitest: `isolate: false`; restoreMocks, unstubGlobals, unstubEnvs auto; projects by suffix.
- web boundary guards: `vi.mock` ratchet locked at zero; MSW unhandled requests fail; weekly shuffled suite records a reproducible seed.

Planned:
- C# BannedSymbols: `DateTime.UtcNow`, `Task.Delay`, `Thread.Sleep`, bare `new HttpClient()`, `Process.Start`, test `Migrate()`.
- UnitTests csproj backstop: ban heavy fixtures (WebApplicationFactory, Orleans.TestingHost).
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

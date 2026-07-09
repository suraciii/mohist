# Testing

All packages (server, runner, web, cli).

## Tracks

| Track | Verifies | Integration | Placement |
|---|---|---|---|
| Spec | product behavior / user flow | high: through product entry point | near product surface |
| Unit | single module / class / function | low: all collaborators faked | near code under test |

Architecture tests (ArchTests) are a third category: verify structure, not behavior.

Track expressed by naming + directory (not runtime trait):

| Side | unit | spec |
|---|---|---|
| C# | `*Tests.cs`, UnitTests project | `*Specs.cs`, SpecTests by context dir |
| runner | `*.test.ts`, near src/ | `*.spec.ts` |
| web | `.test` = src/ collocated | `.spec` = tests/ dir |

One file = one subject under test.

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

### 4. Fast and concise

| Track | Per test | Per file |
|---|---|---|
| Unit | < 50ms | < 300 LOC |
| Spec | < 500ms (hard cap 5s); collection ≤ 2min | < 800 LOC (C# 24KB enforced) |
| E2E/a11y | separate `npm run test:e2e` / `test:a11y`; never in default `npm test` | |

Extract shared setup. One product ability = one test file. Migration splits: delete old file once equivalent coverage exists.

## Guards (automated)

Existing:
- ArchTests: layer deps, spec naming, 24KB budget, namespace, public.
- BannedApiAnalyzer: compile-time ban on direct env reads.
- vitest: restoreMocks, unstubGlobals, unstubEnvs auto; projects by suffix.

Planned:
- C# BannedSymbols: `DateTime.UtcNow`, `Task.Delay`, `Thread.Sleep`, bare `new HttpClient()`, `Process.Start`, test `Migrate()`.
- UnitTests csproj backstop: ban heavy fixtures (WebApplicationFactory, Orleans.TestingHost).
- ESLint: ban `child_process`, real `@microsoft/signalr` import in tests.
- MSW `onUnhandledRequest: 'error'`.

## Fake quick reference

| Dependency | server | runner | web | cli |
|---|---|---|---|---|
| time | FakeTimeProvider | vi.useFakeTimers | same as runner | seam missing |
| HTTP | WebApplicationFactory + TestServer | vi.stubGlobal('fetch') | MSW | RecordingHttpHandler |
| SignalR | RecordingRunnerHubContext | vi.mock('@microsoft/signalr') | same as runner | — |
| process | — | setAcpProcessFactoryForTest | — | FakeCommandExecutor |
| DB | in-memory SQLite, clone from MigratedSqliteTemplate.CopyTo (no Migrate()) | — | — | fake IOtelQueryExecutor |
| grain | InProcessTestCluster, ControllableReminderTable | — | — | — |
| render | — | — | customRender (tests/test-utils.tsx) | — |
| file/data | Support/TestData/* | tests/support/* | tests/support/* | FakeFileSystem |

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
- Wait for an awaitable signal (`TaskCompletionSource` with `RunContinuationsAsynchronously`, callback, observer, channel). Advance fake time only to drive product time semantics such as backoff retries, timeouts, and expiry; do not use it to release product-side polling waits. Those waits must use a signal at the async boundary. `TestWait` is migration debt for existing code, not an option for new code.

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

The duration budget is enforced by the test-duration guard
(`scripts/test-duration/`, `npm run test:budget`) locally; CI does not run the
guard, it runs the suites directly. The guard is two hard constraints, both
FAIL — never a warning:

| Constraint | What it proves | Threshold |
|---|---|---|
| p95 (population) | the vast majority of tests are millisecond-fast | unit p95 ≤ 50ms; spec p95 ≤ 500ms |
| absolute (single test) | no real slow test slips through | unit/arch ≤ 500ms; spec ≤ 5s (hard cap) |

Architecture tests are a structural category (Roslyn-based): they get the 500ms
absolute cap only, no p95 population budget. A test above its absolute cap
fails unless it is in the version-controlled allowlist
(`test-duration.config.jsonc`) with identity + observed baseline + reason +
owner + removal deadline. p95 cannot be allowlisted away: if the population
drifts over budget, the track fails regardless of any allowlist. The 50ms unit
target is this population guard, not a per-test cliff, so normal scheduling
flutter around 50ms is tolerated as long as p95 holds; the per-test absolute
cap catches real slow tests regardless of population. The whole suite runs
under a five-minute hard deadline.

Tracks without a seeded baseline run deadline-governed only
(`enforce: false`, `status: baseline-pending`): explicit governance status, not
a silent warning. Baselines expand incrementally.

| Track | Per-file budget |
|---|---|
| Unit | < 300 LOC |
| Spec | < 800 LOC (C# ratchet: 24,000 bytes, ≈540 lines at this repo's density) |
| Node unit (`.test`) | < 500 LOC |
| Node spec (`.spec`) | < 850 LOC |
| Browser | separate `npm run test:browser`; never in default `npm test` and never in the guard | |

Extract shared setup. One product ability = one test file. Migration splits: delete old file once equivalent coverage exists.

The C# ratchet freezes files that are already over budget. Each carries an
allowance in `spec-file-size-baseline.json` equal to its size rounded up to the
next 1,000 bytes, so ordinary edits stay inside a bucket and a file that shrinks
hands its slack back. Crossing a bucket needs a baseline edit in the same commit
— that edit is the review gate. The way past the ratchet is to split the file
along the behavior it specifies, never to compress formatting to fit.

The Node test ratchet applies the same principle with a smaller maintenance
buffer: an existing baseline allowance covers the recorded size plus 100 lines.
New over-budget files still cannot add a baseline entry, and a file that returns
under its absolute limit must drop its allowance. Keep test formatting readable;
do not compress statements onto one line to satisfy the counter.

### Repository CI time budget

The whole suite must finish within five minutes. The test-duration guard
enforces a hard five-minute deadline (cross-platform Node-spawned kill; never
the Linux `timeout` binary as the only executor) plus the per-track deadlines
above. CI and local run the same guard, so the budget is identical in both.
Reaching any deadline is an abnormal condition to diagnose, not a normal way to
finish a test run.

The lowest useful layer owns the behavior matrix. API/integration specs assert route, binding, status code, JSON shape, parameter parsing, and one success path per endpoint; state and calculation permutations belong to the querier/grain/domain specs below. Never repeat the lower layer's scenario matrix through HTTP — one behavior change must touch one test file, not two layers.

### 5. Cost Grows Only with Relevant Data

Test execution cost whenever polling, heartbeat, status, dashboard, or cleanup
paths read storage or call other components.

- Hold the current work constant and compare fixtures with a small and a large
  amount of unrelated historical data.
- Use test interceptors or fakes to count database commands, deserialized
  records, or downstream calls.
- Increasing unrelated history must not increase work that depends only on the
  current state.
- Assert operation counts or an explicit upper bound, not wall-clock duration.
- When correctness and cost tests share fixture data, keep them in the same
  test file.

## Spec parallelism (server)

xUnit collection = scheduling unit; classes inside a collection run serially, so wall time = the longest class chain.

- Parallel by default. `DisableParallelization` only for true process-global state (today `OtelTracing` for shared `Microsoft.AspNetCore` ActivitySource, and `Dispatcher` for shared instrumentation capture). Cluster-scoped state (`RunnerRegistryKeys.Global`, `ForceActivationCollection`, fixture `FakeTimeProvider`) is per-fixture, never a reason to serialize.
- Ports: WebApplicationFactory fixtures must allocate via TestClusterPortAllocator. InProcessTestCluster is in-memory transport — no ports, safe anywhere.
- Collections express shared fixture lifetime and isolation needs only — never speed or cost. No custom test orderer: it was measured to sit within run-to-run noise. No runtime traits either; the track is expressed by naming + directory alone.
- `maxParallelThreads` is a memory bound, not a speed knob. Every concurrent collection lights up its own silo + WebApplicationFactory (~59 MB) on top of a ~1150 MB fixed floor, and there are more collections than any machine should hold at once. Cap it in `xunit.runner.json`; leave it alone for wall time, which is flat across settings.
- Schema: tests never run `Migrate()` / `EnsureCreated()` from empty — clone via `MigratedSqliteTemplate.CopyTo` / `CopyTo(target)` / `CopyModelSchemaTo`. Sole exception: DatabaseInitializationSpecs (its subject is the chain itself).

## Guards (automated)

Existing:
- test-duration guard (`scripts/test-duration/`, `npm run test:budget`): enforces the suite 5-minute deadline, per-track deadlines, p95 population budgets (unit ≤ 50ms / spec ≤ 500ms), and the 500ms absolute single-test cap; parses real vitest JSON and xUnit TRX; FAILs on timeout, over-budget, p95 breach, and stale or deadline-expired allowlist entries. `test-duration.config.jsonc` holds thresholds and the per-item governed allowlist. Deterministic unit tests cover parsing, budget, deadline, focused-flow, and config validation (no real network/process/wall-clock).
- ArchTests: layer deps, spec naming, namespace, public, and analyzer wiring backstops.
- `BannedApiAnalyzers`: compile-time enforcement of product and test API deny lists; test projects additionally ban wall-clock, scheduler-based waiting, host paths, physical adapters, and real filesystem APIs.
- EnvironmentAbstractions BannedApiAnalyzer: compile-time ban on direct env reads.
- vitest: `isolate: false`; restoreMocks, unstubGlobals, unstubEnvs auto; projects by suffix.
- web boundary guards: `vi.mock` ratchet locked at zero; MSW unhandled requests fail; weekly shuffled suite records a reproducible seed.

Planned:
- UnitTests csproj backstop: ban heavy fixtures (WebApplicationFactory, Orleans.TestingHost).
- C# product deny-list expansion after direct `DateTime.UtcNow` / `DateTimeOffset.UtcNow` reads are migrated to `TimeProvider`.
- ESLint: ban `child_process`, real `@microsoft/signalr` import in tests.
- Migrate the remaining `TestWait` polling points (currently 28) to boundary signals incrementally.
- Measure C# test file size in lines rather than bytes (below).

### Planned: measure test file size in lines

Bytes are a proxy for lines, and the proxy leaks. It taxes the descriptive test
names this repo asks for, charges triple for non-ASCII comments, and lands ~35%
stricter than the 800-LOC budget it stands in for. Renaming a method can break
the build while the file gets no longer.

Target: budget physical lines, threshold 550. That holds the guarded set where
it is (40 files exceed 550 lines; 36 exceed 24,000 bytes) instead of loosening
it — the documented 800 was never what ran, and only 8 files reach it.

A maximum line length ships in the same change, not as a follow-up. Line count
is easier to game than byte count: collapsing statements onto one line lowers it
directly, so the budget is unsound without a companion cap. At 200 characters
163 of the repo's 153,363 test lines need splitting; at 250, 48 do.

Bucket-and-equality allowances carry over unchanged; only the unit and the
threshold move.

Out of scope: per-track thresholds. The table above sets Unit < 300 LOC and Spec
< 800 LOC, but one threshold covers every test project. Separating them moves
~30 UnitTests files at once and is its own decision.

## C# focused tests

C# test projects use Microsoft Testing Platform + xUnit v3. Do not treat VSTest filters as focused tests:

- Do not use `dotnet test <csproj> --filter "FullyQualifiedName~..."`. It currently reports `MTP0001` (`VSTestTestCaseFilter` ignored) and may run the whole test assembly.
- The pass-through `dotnet test <csproj> --no-restore --no-build -- -class <FQCN>` is not reliable today: MTP turns `-class` into `--class` and reports an unknown option.
- The correct way is to run the compiled xUnit v3 apphost directly. Confirm `-class` exists via the apphost `--help` first, then run:

```bash
dotnet build packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore
packages/cli/tests/Mohist.Cli.Tests/bin/Debug/net11.0/Mohist.Cli.Tests \
  -list classes -noColor -noLogo \
  -class Mohist.Cli.Tests.Skills.SkillsContentTests
packages/cli/tests/Mohist.Cli.Tests/bin/Debug/net11.0/Mohist.Cli.Tests \
  -noColor -noLogo -class Mohist.Cli.Tests.Skills.SkillsContentTests
```

- Focused runs use the compiled apphost above; never fall back to `dotnet --filter`.
- In a new worktree, run `npm ci` explicitly first; run `dotnet restore <csproj>` explicitly when `obj/project.assets.json` is missing; afterwards build/test with explicit `--no-restore` / `--no-build`; no implicit installs or lockfile rewrites.

## Fake quick reference

| Dependency | server | runner | web | cli |
|---|---|---|---|---|
| time | FakeTimeProvider | vi.useFakeTimers | same as runner | seam missing |
| HTTP | WebApplicationFactory + TestServer | vi.stubGlobal('fetch') | MSW | RecordingHttpHandler |
| SignalR | RecordingRunnerHubContext | vi.mock('@microsoft/signalr') | config alias -> tests/support/signalr-fake.ts | n/a |
| notification | n/a | n/a | config alias -> tests/support/sonner-fake.ts | n/a |
| process | n/a | fake OpenCodeRuntime / SDK server factory | n/a | FakeCommandExecutor |
| DB | in-memory SQLite, clone from MigratedSqliteTemplate.CopyTo (no Migrate()) | n/a | n/a | fake IOtelQueryExecutor |
| grain | InProcessTestCluster, ControllableReminderTable | n/a | n/a | n/a |
| render | n/a | n/a | customRender (tests/test-utils.tsx) | n/a |
| file/data | Support/TestData/* | tests/support/* | tests/support/* | FakeFileSystem |

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
- WebApplicationFactory and InProcessTestCluster fixtures use Orleans in-memory
  transport and never bind host ports. Tests which explicitly exercise the
  production TCP transport may allocate isolated ports with
  TestClusterPortAllocator; never hardcode 11111/30000 for those tests.
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
(`scripts/test-duration/`, `npm run test:budget`) through the canonical gate in
both local and CI execution. CI never substitutes independent suite jobs plus a
later report-only check for the scheduler. The guard is two hard constraints,
both FAIL — never a warning:

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

The CLI duration track runs xUnit collections with the conservative scheduler
and at most four workers. This keeps independent tests parallel while bounding
concurrent cold command-tree construction and runtime initialization cost. The
checked-in configuration test owns this capacity; raising it requires repeated
same-input evidence that both population and single-test budgets remain stable.

Tracks without a seeded baseline run deadline-governed only
(`enforce: false`, `status: baseline-pending`): explicit governance status, not
a silent warning. Baselines expand incrementally.

| Track | Per-file budget |
|---|---|
| Unit | < 300 LOC |
| Spec | < 800 LOC |
| Browser | separate `npm run test:browser`; never in default `npm test` and never in the guard |

Extract shared setup. One product ability = one test file. Migration splits: delete old file once equivalent coverage exists.

These per-track budgets are review guidance, not gates. The gate is the
repository file-size ratchet (`scripts/check-file-sizes.ts`,
`npm run check:filesizes`): first-party source under `packages/` (`.cs`,
`.ts`, `.tsx`) is capped at 1000 lines; a file already over the cap is frozen
at its exact base line count and may not grow. The baseline is the merge-base
against `origin/master`, so there is no baseline file to maintain: shrinking a
file below the cap frees it completely, and a new file over the cap fails
outright. The only exclusion is the EF `Migrations/` directory, whose snapshot
and designer files are regenerated wholesale with the schema. Keep formatting
readable; do not compress statements onto one line to satisfy the counter.

### Repository CI time budget

The whole CI job and the local canonical gate must each finish within five
minutes. The test-duration guard enforces one absolute five-minute deadline
(cross-platform Node-spawned kill; never the Linux `timeout` binary as the only
executor) plus the per-track deadlines above. CI and local run the same guard.
Their outer process walls reserve different setup and cleanup margins, but both
are stricter than the guard's deadline. Reaching any deadline is an abnormal
condition to diagnose, not a normal way to finish a test run.

### Canonical local gate

`npm run verify` is the one final local acceptance command. Its outer command
contract is:

```bash
timeout -k 10s 300s npm run verify
```

CI gives the job seven minutes for checkout, setup, install, the gate, cleanup,
and diagnostic upload. Its gate step uses `timeout -k 10s 300s npm run verify`,
matching the canonical absolute deadline while leaving the job-level wall for
bounded process-tree convergence and artifact upload.

It is not followed by `npm test` or `npm run test:budget -- --all`: the
canonical gate already covers their controlled work. Focused commands,
including direct compiled-apphost `-class` runs and `npm run test:budget --
--track <id>`, remain development tools and do not claim final acceptance.

The gate owns one unique run directory in the operating system's temporary
directory, never below the repository. An explicit `--artifact-root` is an
absolute external parent directory, not a reusable run directory: every run
creates a unique child below it. The canonical entry passes its exact child as
an internal run root only after writing matching run metadata. A report-only
`--check` reads an existing root and never creates, removes, or refreshes a
report. These rules prevent concurrent invocations from deleting one another's
reports or accepting a build stamp from a different run.

The gate first creates its unique directory, then completes exactly one fresh
repository build and writes a build stamp carrying that run identity. Test
lanes may start only after that matching stamp exists. Every report,
stdout/stderr log, temporary directory, and Spec partition manifest is rooted
below the same run directory; a report from an older run therefore cannot
become evidence for the current source. Before a report-producing lane starts,
the gate creates the report parent and removes the declared report target. A
lane is successful only when its process exits zero and writes a fresh,
non-empty report at that exact path.

Canonical evidence is accepted only from a clean index and worktree, including
the absence of non-ignored untracked files that a build glob could consume.
The gate records the exact `HEAD` revision, checks that identity before the
build, after the build and script boundary phase, and after duration execution,
and fails if source inputs change at any boundary. Generated dependencies and
outputs remain governed by the lockfile and ignore policy rather than being
misrepresented as source changes.

The gate retains the temporary run directory for success, ordinary failure, and
deadline failure, and prints its absolute path before returning. It never
removes diagnostics automatically and never relies on `.gitignore` to hide
them. This keeps a failed run inspectable without leaving Git-visible generated
files in the worktree; callers may remove the printed directory after they have
collected the evidence.

The retained directory contains `run.json` and `build-stamp.json` provenance,
`plan.json` with the selected tracks and resource/dependency claims, raw lane
logs and reports, and `summary.json` with every lane result, every parsed track
count, cleanup status, deadline result, and the first failure. Failure to write
the plan or final summary is itself a gate failure.

The DAG is deliberately small. The build and read-only script boundary checks
start together after the docs check. They have separate logs and process-tree
ownership; failure or cancellation of either aborts the sibling. The build
stamp is written only after both phases succeed and the source identity is
revalidated:

```text diagram
docs check
    +-------------------+
    |                   |
    +--> fresh build    +--> read-only script/boundary checks
             |                   |
             +---------+---------+
                       |
                source revalidation
                and matching build stamp
                       |
                ordered duration-measurement lanes
                       |
                bounded throughput lanes
                       |
                Spec partition coverage check
                       |
                shared report and duration evaluation
```

The full 300-second absolute deadline starts before the build and is shared by
build, script checks, lane execution, process-tree cleanup, report parsing, and
summary formatting. It is never rebased as a later phase's relative timeout.
Duration logic receives a `now` seam and the canonical absolute deadline; only
the CLI composition adapter binds that seam to process-monotonic time. No guard,
scheduler, lane, or duration test reads `Date.now()` directly.
The scheduler stops new execution early enough to reserve two existing
kill-grace intervals for TERM/KILL tree termination and a final bounded report
window; it does not start a lane once that execution cutoff has passed. Every
child cleanup waits for process-tree termination only until the same absolute
deadline, so a killed build or lane cannot make the final command run beyond
the wall. On external `SIGTERM` or `SIGINT`, the same abort signal reaches the
current phase and scheduler; it uses one TERM grace plus the finalization
reserve, leaving margin inside the outer command's ten-second KILL window. Each
existing track deadline remains a separate hard cap.

The duration policy itself is unchanged: every configured track, including
`baseline-pending`, must produce `Total > 0`; failed, skipped, or not-run cases
fail the gate; enforced tracks retain their existing p95 and single-test caps.
`enforce: false` is valid only with the exact explicit
`status: baseline-pending` and a non-empty reason; it is a temporary baseline
state, never a way to silently downgrade a controlled track. No retry, sleep,
skip, allowlist, threshold change, timeout increase, or global serialization is
a gate recovery mechanism.

The scheduler has explicit lane ownership rather than opening every command at
once. `test-duration.config.jsonc` declares the reproducible host limits. The
default is four host lanes, with at most four .NET lanes and two Node lanes.
Partitioned tracks declare both their per-process thread count and an aggregate
execution capacity; configuration fails closed when outer partition concurrency
times inner test concurrency exceeds that capacity. A lane starts only when its
dependencies and all claimed resources are available, and an already-aborted
schedule admits none. Node duration commands place reporter arguments on their
terminal `vitest run` invocation, and execute TypeScript boundary checks through
`node --import tsx` rather than the `tsx` CLI IPC server. Each lane owns its
`TMPDIR`, `TEMP`, `TMP`, HOME, and runtime IPC directory. The four concurrent
Server Spec lanes additionally own their main SQLite path, OTel SQLite path, and
logical OTLP endpoint scope; unit lanes retain their product-default configuration
so their default-value assertions remain meaningful. The Spec lanes use a
Node-hosted deterministic partition executor on every platform. Each owns a
distinct report path, temporary directory, and manifest directory; the fixtures
use logical silo/gateway endpoint identities and Orleans in-memory transport,
so they never bind or probe host ports. xUnit v3 lanes use their compiled apphost
reporter; the legacy xUnit v2 workflow-definition lane reuses its build through
`dotnet test --no-build --no-restore` and its VSTest TRX reporter.
On Windows, canonical phases and Node lanes resolve the inherited npm CLI
through the current Node executable; they do not pass a `.cmd` file to
`CreateProcess` and never enable a shell. A missing npm CLI identity therefore
fails before a child is admitted instead of changing quoting semantics.

`canonical.durationMeasurementTracks` is an ordered, small set of tracks whose
per-test duration policy must not share CPU or I/O with another test executor.
Each single-lane member claims the `duration-measurement` resource and every
member depends on the previous member's terminal lane. A partitioned member
uses its coverage lane as the phase barrier. The optional
`canonical.durationIsolationTrack` is admitted
after that prefix and gates other Vitest lanes. The current measurement set is
the CLI track followed by Server Spec: four partition apphosts each run one
xUnit collection at a time, so aggregate Spec execution concurrency is four.
Server Unit, Server Arch, Workflow, and Node throughput lanes start after Spec
coverage completes; Runner remains the isolated Node track. This preserves
parallel partitions without changing duration thresholds. The phase is applied
only when the complete configured set is selected, so focused `--track`
execution has no hidden prerequisite work.

#### Host exclusivity for duration evidence

The canonical scheduler owns resources inside one gate invocation. It does not
claim to arbitrate arbitrary processes from another worktree on the same host:
a repository lock file cannot do that safely across platforms, and a crashed
owner can leave either a stale permanent block or an unsafe PID-reuse cleanup.
The gate neither scans for nor kills unrelated processes, and it never waits,
polls, retries, or silently loosens duration policy to recover from them.

Duration acceptance therefore requires a host lease supplied by the invoking
coordinator, outside the repository. The coordinator records the holder in its
own cross-platform worker session and invalidates the lease when that session
ends, rather than relying on a local stale-lock timeout. While that lease is
held, no other Mohist test apphost, Roslyn-heavy architecture test, or
comparable CPU/IO test executor may run on the host. If that condition is not
met, the run is marked contaminated and its duration numbers are not used for a
gate-regression conclusion or a baseline update.

On the first lane failure or deadline, the scheduler stops admitting queued
lanes, terminates active lane process trees, and waits for their cleanup through
the shared absolute cutoff. POSIX lanes use their detached process group;
Windows lanes use `taskkill /T /F` and wait for the launched process tree's
terminal event. A spawn failure or nonzero `taskkill` exit cannot establish
tree convergence, even if the root process has already exited. Neither path
waits without a bound. It never deletes completed evidence. The final report
includes each started or cancelled lane's command,
original exit status, elapsed time, raw-log paths, report state, and all
parseable real test totals. It reports the triggering failure separately from
lanes cancelled or not started by fail-fast; cancelled lanes are not recast as
independent report-production failures. A completed lane with a missing, stale,
empty, failed, skipped, or not-run report is a failure, not a green omission.

CI invokes `npm run verify` once in one job after one dependency install. That
job runs the same canonical scheduler, fresh build, resources, duration
measurement prefix, four Spec partitions, coverage lane, report parser, and
failure semantics as local execution. CI uploads that one external diagnostic
run directory after success or failure; it does not reconstruct a gate by
downloading reports from separately built jobs. The CI machine boundary supplies
the host-exclusive precondition for that invocation.

#### Host-exclusive performance measurements

The local gate owns only the child process trees it starts. Its lane resources
are deliberately per-run claims: they prevent this gate from oversubscribing
itself, but cannot reserve CPU, Orleans scheduling, or ports from an arbitrary
direct apphost, build, or test loop in another worktree. A local duration
acceptance run therefore has a host-exclusive precondition: before starting
`timeout -k 10s 270s npm run verify`, its operator obtains a host with no other
Mohist build or Server Spec host running. A result captured while that condition
is false remains useful raw failure evidence, but is not a valid performance
baseline or a basis for changing the p95 policy. The foreign process is stopped
by its owner; the canonical gate never waits for, polls, retries, or terminates
it. CI satisfies this condition through the job machine boundary.

The gate intentionally has neither an OS-wide process scanner nor a
cross-worktree lock. Process enumeration is not a reliable cross-platform
contract and cannot identify every descendant of an arbitrary apphost. A lock
would coordinate only cooperative callers while direct apphosts would still
evade it; it would also turn independent worktrees into a global serial queue
and threaten the five-minute deadline. If cooperative admission is ever needed
for a developer tool, it must be an opt-in, host-local lease implemented with
portable atomic directory creation, owner/run metadata, immediate conflict
failure (never waiting), `finally` release, and a bounded-expiry stale-lease
reclamation path. Such a lease must never signal a foreign PID, and it cannot
replace the host-exclusive precondition for non-cooperating commands.

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
- Ports: WebApplicationFactory fixtures use TestServer plus Orleans in-memory
  transport, so they do not allocate host ports. InProcessTestCluster is also
  in-memory transport. A test may allocate ports only when its subject is the
  production TCP transport itself.
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
- web boundary guards: `vi.mock` ban locked at zero; MSW unhandled requests fail; weekly shuffled suite records a reproducible seed.
- file-size ratchet (`scripts/check-file-sizes.ts`, `npm run check:filesizes`): new files over 1000 lines and any growth of an already-over-limit file fail against the merge-base with `origin/master`.
- formatter ratchet (`scripts/check-format.ts`, `npm run format:check`): every `.js`/`.mjs`/`.ts`/`.tsx`/`.css` file changed against the merge-base must be biome-clean (`biome.json`, formatter only). Legacy files stay as-is until touched, so the repo converges file by file instead of through a one-shot reformat.

Planned:
- UnitTests csproj backstop: ban heavy fixtures (WebApplicationFactory, Orleans.TestingHost).
- C# product deny-list expansion after direct `DateTime.UtcNow` / `DateTimeOffset.UtcNow` reads are migrated to `TimeProvider`.
- ESLint: ban `child_process`, real `@microsoft/signalr` import in tests.
- Migrate the remaining `TestWait` polling points (currently 28) to boundary signals incrementally.

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
| time | FakeTimeProvider | vi.useFakeTimers | same as runner | FakeTimeProvider + injected poll wait |
| HTTP | WebApplicationFactory + TestServer | vi.stubGlobal('fetch') | MSW | RecordingHttpHandler |
| SignalR | RecordingRunnerHubContext | vi.mock('@microsoft/signalr') | config alias -> tests/support/signalr-fake.ts | n/a |
| notification | n/a | n/a | config alias -> tests/support/sonner-fake.ts | n/a |
| process | n/a | fake OpenCodeRuntime / SDK server factory | n/a | FakeCommandExecutor |
| DB | in-memory SQLite, clone from MigratedSqliteTemplate.CopyTo (no Migrate()) | n/a | n/a | fake IOtelQueryExecutor |
| grain | InProcessTestCluster, ControllableReminderTable | n/a | n/a | n/a |
| render | n/a | n/a | customRender (tests/test-utils.tsx) | n/a |
| file/data | Support/TestData/* | tests/support/* | tests/support/* | FakeFileSystem |

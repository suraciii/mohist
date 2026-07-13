# Testing

All packages (server, runner, web, cli).

## Tracks

Mohist has three kinds of tests:

| Track | Verifies | Failure means |
|---|---|---|
| SpecTests | product promises and user-observable behavior | a product regression |
| UnitTests | modules, types and implementation invariants | a technical implementation regression |
| ArchTests | stable architectural and design constraints | the implementation drifted from the design |

Component, Integration, E2E, a11y, database, grain and full-host are runtime
mechanisms or execution profiles, not a fourth kind of test. TestSupport is a
normal class library, also not a test kind.

Classification looks at what a test proves, not at how it runs. A Spec may call
only a pure function; a Unit may use SQLite, DI or in-process Orleans.

### Browser tests

Browser tests are Web-only. They verify behavior that requires a real layout and
interaction engine (production Web build in Chromium; API and Hub responses
controlled). They do not exercise Server, persistence, or a real network, so they
are not end-to-end tests. They live in `packages/web/tests/browser/` and run via a
separate `npm run test:browser`; never in default `npm test`.

## Decide value before keeping a test

For every test, first ask:

1. What current regression does it alone catch?
2. Whose promise is this behavior — product, technical contract, or architecture?
3. Does a more natural test already protect the same risk?

If the answer is unclear, do not keep it for coverage. Delete tests that are
duplicates, stale, unreachable, tautological, only exercise mock/setup/framework,
do not drive a named subject, or have no current contract. A historical bugfix,
fast runtime, or higher coverage does not by itself justify a test.

One behavior has one primary owner. Other tests survive only when they protect a
distinct risk.

## How to classify

### SpecTests

- Use product language; verify product results, not internal structure.
- A product spec may live in `docs/`, or in a route, DTO, CLI command/output, a
  user-visible state transition, a real consumer, or an executable spec. The
  absence of a `docs/` entry does not demote a Spec to a Unit.
- Code evidence must state the product promise. A `public` symbol, a private
  branch, a database shape, a current algorithm, or the `Specs` suffix alone is
  not a product spec.
- Drive from HTTP, CLI, or a product entry point. Setup may seed a DB; the
  assertion must be an observable result.
- HTTP/full-host does not automatically make a test a Spec; each matrix case must
  represent a scenario the product intentionally distinguishes.
- When the internal implementation is fully rewritten but product behavior is
  unchanged, a Spec should still pass.

### UnitTests

- Verify one cohesive technical subject and its independent technical risk.
- Use the lowest natural seam; do not mock every collaborator.
- SQLite, DI, or in-process runtime are allowed, but no real external environment.
- Do not repeat scenarios a Spec already protects, and do not mechanically add a
  test for every class, branch, or mapper.
- A Unit may reasonably change during an internal refactor.

### ArchTests

- Executable architecture fitness functions: verify dependencies, layering,
  boundaries, placement, project references, naming, and banned APIs.
- Every rule must come from an explicit design decision and currently pass; a skip
  provides no protection.
- This repository only does deterministic assembly, source-tree, and project-graph
  checks; it starts no runtime. The source tree / project graph here is the
  declared static subject under test, not runtime external state.
- A ratchet is only an optional tool for existing architectural debt: it must
  tighten monotonically and have an exit condition.
- With no existing violation, assert the final invariant directly. Do not treat
  file size, test count, timing rank, or an accidental directory listing as
  architectural truth.

## Naming and placement

| Side | Spec | Unit | Arch |
|---|---|---|---|
| Server | `Mohist.Server.SpecTests`, `*Specs.cs` | `Mohist.Server.UnitTests`, `*Tests.cs` | `Mohist.Server.ArchTests` |
| CLI | `Mohist.Cli.SpecTests`, `*Specs.cs` | `Mohist.Cli.UnitTests`, `*Tests.cs` | repository ArchTests |
| runner | `*.spec.ts`, near src/ | `*.test.ts` | repository guard |
| web | `.spec` = tests/ dir | `.test` = src/ collocated | repository guard |

One file = one product ability, technical subject, or cohesive rule group. Split
only at a real ownership boundary.

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

TestSupport holds only small deterministic helpers genuinely shared by multiple
projects. It references no test SDK and contains no test or application host. Test
projects never reference another test project.

## Hard rules

### 1. No real external environment

Must pass in a container with no network, no git, no node, no opencode, empty HOME.

No real network, processes, git, shell, agent binaries, DB files, system services,
env vars. Inject fakes (see cheat sheet). Temp dirs allowed.

### 2. No real filesystem (.NET)

Server/CLI SpecTests, UnitTests and TestSupport do not touch the real filesystem;
they use in-memory ports or embedded resources. ArchTests only read source and
project graphs declared as static subjects under test.

### 3. No real time

Fake clock not advancing = time logic never fires.

- Inject `TimeProvider` (C#) or `vi.useFakeTimers` / `now` param (TS).
- No wall-clock waits: no `while(now<deadline)`, no `Delay`/`setTimeout`/`Sleep`, no `elapsed < N` asserts.
- Use awaitable signals or fake timer advance.

### 4. Deterministic (no flaky)

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

### 5. Fast and concise

Speed is managed by runtime mechanism, not by test kind:

| Mechanism | Target |
|---|---|
| pure / in-memory | per test < 50ms |
| SQLite / DI / in-process runtime | per test < 500ms |
| full host product flow | per test < 5s |
| E2E / a11y / browser | separate command, never in default `npm test` |

Shrink setup, delete duplicate scenarios, or pick a more natural driver before a
test gets slow. Do not add a custom orderer, a speed trait, a numbered shard, or a
new test kind.

Extract shared setup. One product ability = one test file. Migration splits: delete old file once equivalent coverage exists.

## Spec parallelism (server)

xUnit runs independent collections in parallel by default. A collection is a
declaration of a shared lifetime or a real isolation boundary, not a scheduling
or speed control.

- Use a collection only when its tests share a fixture lifetime or cannot safely run together. Its name describes that capability or lifetime; it carries no number, cost, duration, or thread count.
- Only real process-global state disables parallelization: today `OtelTracing` (shared `Microsoft.AspNetCore` `ActivitySource`) and `ConsoleOutput` (tests that replace process-global `Console.Out` / `Console.Error`). Cluster-scoped state is per-fixture, never a reason to serialize.
- No custom collection orderer. No numbered shard. Prefer xUnit's default scheduling.
- Ports: WebApplicationFactory fixtures must allocate via TestClusterPortAllocator. InProcessTestCluster is in-memory transport — no ports, safe anywhere.
- Schema: tests never run `Migrate()` / `EnsureCreated()` from empty — clone via `MigratedSqliteTemplate.CopyTo` / `CopyTo(target)` / `CopyModelSchemaTo`. Sole exception: DatabaseInitializationSpecs (its subject is the chain itself).

## Guards (automated)

Existing:
- ArchTests: layer dependencies and repository-root-validated test boundaries — test roots exist; spec classes are public and in the project namespace; test projects do not reference one another; only allowed track project names; no traits, no custom orderer; and project/package boundaries stay honest.
- BannedApiAnalyzer: compile-time ban on direct env reads.
- vitest: `isolate: false`; restoreMocks, unstubGlobals, unstubEnvs auto; projects by suffix.
- web boundary guards: `vi.mock` ratchet locked at zero; MSW unhandled requests fail; weekly shuffled suite records a reproducible seed.

Planned:
- C# BannedSymbols: `DateTime.UtcNow`, `Task.Delay`, `Thread.Sleep`, bare `new HttpClient()`, `Process.Start`, test `Migrate()`.
- ESLint: ban `child_process`, real `@microsoft/signalr` import in tests.

## Fake quick reference

| Dependency | server | runner | web | cli |
|---|---|---|---|---|
| time | FakeTimeProvider | vi.useFakeTimers | same as runner | FakeTimeProvider |
| HTTP | WebApplicationFactory + TestServer | vi.stubGlobal('fetch') | MSW | RecordingHttpHandler |
| SignalR | RecordingRunnerHubContext | vi.mock('@microsoft/signalr') | config alias → tests/support/signalr-fake.ts | — |
| notification | — | — | config alias → tests/support/sonner-fake.ts | — |
| process | — | fake OpenCodeRuntime / SDK server factory | — | FakeCommandExecutor |
| DB | in-memory SQLite, clone from MigratedSqliteTemplate.CopyTo (no Migrate()) | — | — | fake IOtelQueryExecutor |
| grain | InProcessTestCluster, ControllableReminderTable | — | — | — |
| render | — | — | customRender (tests/test-utils.tsx) | — |
| file/data | in-memory port / embedded resource | tests/support/* | tests/support/* | FakeFileSystem |

## Current gaps

Server and CLI already use the SpecTests / UnitTests project names.
`plans/dotnet-test-tracks.md` records the per-method value audit of full-host
direct-service tests; it does not duplicate host fixtures or add product
abstractions merely to move a test.

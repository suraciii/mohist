# Testing

Mohist uses Unit, Spec, Browser, and Architecture test tracks. Tests must be
hermetic, deterministic, fast, and owned by the lowest layer that can prove the
behavior.

## Tracks

- **Unit** tests one module, class, or function with all collaborators faked.
  C# files end in `*Tests.cs`; Runner and Web files use `.test` near source.
- **Spec** tests product behavior through a product entry point with higher
  integration. C# files end in `*Specs.cs`; Runner and Web files use `.spec`.
- **Browser** tests layout and interaction in Chromium against the production
  Web build with API and Hub responses controlled. Run it with
  `npm run test:browser -w packages/web`.
- **Architecture** tests structural rules with Roslyn. They do not test product
  behavior.

One file owns one subject. Active support names state their responsibility:
`Fixture` owns reusable data or lifetime, `TestHost` owns an application
boundary, `Probe` exposes state for assertions, and `TestFactory` constructs
collaborators. Do not use `Harness` in active test names.

## No External Environment

Tests must pass with no network, Git, Node, OpenCode, system service, environment
configuration, or host filesystem.

- Tests use fakes for network, processes, Git, shell, Agent binaries, databases,
  environment variables, and files.
- Tests never read or write host temporary directories, HOME, the checkout,
  build output, or assembly content roots.
- Test composition uses in-memory stores, catalogs, file providers, and named
  shared-memory SQLite. It never resolves a physical filesystem adapter.
- Built-in prompts, templates, Workflow Definitions, Skills, and assets enter
  tests as embedded catalogs or explicit in-memory values.
- Pure lexical path operations may use fixed virtual roots. Host-derived paths
  are forbidden.

`MemoryStream`, `StringReader`, in-memory `IFileProvider`, and in-memory SQLite
are memory boundaries rather than filesystem access.

## No Real Time

Product time must be injectable. C# uses `TimeProvider`; TypeScript uses fake
timers or an injected `now` function.

- Tests use fixed timestamps or fake time. They never read the wall clock for
  seed data, deadlines, or tolerance assertions.
- Tests do not wait with `Delay`, `setTimeout`, `Sleep`, `Task.Yield`, timers,
  spin waits, or a loop over elapsed time.
- Tests wait for an awaitable boundary signal such as a callback, observer,
  channel, or `TaskCompletionSource` with asynchronous continuations.
- Fake time advances only product semantics such as expiry, timeout, or retry
  backoff. It does not release product polling.
- Existing `TestWait` usage is migration debt and is not available to new code.

## Determinism

The result must not depend on order, parallelism, scheduling, locale, or a
previous test file.

- Fixed timestamp examples use values such as `2026-01-01T00:00:00Z`.
- Tests do not depend on array, object-key, database, or scheduling order unless
  the product contract defines that order.
- Web tests run with `isolate: false`. Mutable module state and control ports
  expose a reset seam that `afterEach` invokes.
- Web HTTP uses MSW. Unhandled requests fail the owning test even if product code
  catches the request error.
- Web SignalR and notification boundaries use the configured fakes. Web tests do
  not add local `vi.mock` calls.
- Unexpected `console.error` or `console.warn` output fails the test unless the
  test captures and asserts it.
- The normal Web suite and shuffled execution must both pass. A shuffle failure
  is a state leak.
- A flaky test is fixed or deleted. It is never skipped or retried into green.

## Speed And Size

The test-duration guard enforces two hard limits:

- Unit population p95 is at most 50 ms. Spec population p95 is at most 500 ms.
- One Unit or Architecture test is at most 500 ms. One Spec is at most 5 s.

Architecture tests use only the single-test limit. A slow-test exception must
be version controlled with its identity, observed baseline, reason, owner, and
removal deadline. Population p95 cannot be allowlisted. The complete local gate
has a five-minute deadline.

Unit files should remain below 300 lines and Spec files below 800 lines. Browser
tests run separately from the default test command. These are review guidelines;
the repository file-size ratchet is the automated source-file gate.

The CLI duration track uses at most four workers. Raising its capacity requires
repeated same-input evidence that population and single-test budgets remain
stable. A track without a seeded baseline is deadline-governed with explicit
`baseline-pending` status; this is not a warning mode.

## Canonical Local Gate

`npm run verify` is the only final local acceptance command. It owns one fresh
build, structural checks, test reports, duration evidence, and one absolute
five-minute deadline. It is not followed by another full test command.

Focused commands are development tools and do not claim final acceptance. The
C# focused apphost workflow is documented in
[`CONTRIBUTING.md`](../CONTRIBUTING.md#focused-c-tests). Gate implementation,
evidence, scheduling, and process-cleanup rules are documented beside the tool
in [`scripts/test-duration/README.md`](../scripts/test-duration/README.md).

CI keeps its regular required build-and-test jobs. It does not reconstruct local
duration evidence from separately restored jobs because that changes build and
resource ownership.

## Behavior Ownership

The lowest useful layer owns each behavior matrix. API and integration specs
assert route binding, status, JSON shape, parameter parsing, and one success path
per endpoint. State and calculation permutations belong to the domain or module
below. One behavior change should not require the same scenario matrix in two
layers.

A lower-owner test does not start a full application host solely to replace one
collaborator. When a consumer needs only a narrow read or decision capability,
that capability is an explicit port: production forwards it to the concrete
service, while Unit tests supply a fake and in-memory state. The HTTP Spec keeps
only the route and wire behavior that the lower layer cannot prove.

Polling, heartbeat, status, dashboard, and cleanup paths must cost only what
their current relevant data requires. Cost tests hold current work constant and
vary unrelated history. They assert database command, deserialization, or
downstream-call counts rather than elapsed time.

## Server Spec Parallelism

An xUnit collection is the scheduling unit. Classes inside one collection run
serially.

- Full-stack specs share one assembly fixture and therefore one apphost. Each
  test creates a unique project for business state and may run in its class's
  default collection.
- Product-global state such as clocks, runner discovery, instrumentation, or
  hosted dispatchers is not project-isolated. A full-stack class that mutates
  it belongs to a resource suite with one dedicated apphost. Classes in that
  suite use unique identities and run serially against the resource. A separate
  resource suite requires an incompatible host configuration or a disjoint
  global resource domain with its own reset boundary; a class boundary is never
  sufficient. Only truly process-static state uses a non-parallel collection.
  Behavior matrices still move below the full-stack boundary.
  Large resource suites are partitioned by global resource domain. Each domain
  owns one dedicated host, while independent domains run in parallel.
- Specs that deliberately rewind public-projection checkpoints use a dedicated
  apphost without the background projector and drive complete batches through
  the projection engine. Command specs project the affected Session directly;
  only drain-contract specs scan the collection's complete backlog. The
  hosted-loop contract stays in its own apphost.
- CI and the canonical local gate do not split classes across processes.
- Collections express shared fixture lifetime or real isolation needs, never
  speed or cost.
- A Spec that no longer needs a distinct clock, database, or cluster joins an
  existing compatible collection; it does not retain a dedicated host.
- Process-global instrumentation may disable parallelism. Cluster-scoped state
  is per fixture and is not a reason to serialize.
- `WebApplicationFactory` and `InProcessTestCluster` use in-memory transport and
  do not bind host ports. Only a test of production TCP transport may allocate
  an isolated port.
- `maxParallelThreads` is a memory bound, not a speed control.
- Database tests clone the migrated SQLite template. Only database
  initialization tests run the migration chain from empty.

## Automated Guards

- `scripts/test-duration/` enforces the suite deadline, track deadlines,
  population budgets, single-test limits, report freshness, and nonzero totals.
- Architecture tests enforce layer dependencies, naming, namespaces, public
  surfaces, and analyzer wiring.
- Banned API analyzers reject wall-clock, scheduler waits, host paths, physical
  adapters, real filesystem access, and direct environment reads.
- Vitest restores mocks, globals, and environment state and runs projects by
  suffix.
- Web guards reject local module mocks and unhandled MSW requests. A weekly
  shuffled suite records its seed.
- The file-size ratchet blocks new first-party source files above 1000 lines and
  growth of files already above that limit.
- The formatter ratchet requires changed JavaScript, TypeScript, TSX, and CSS
  files to satisfy Biome formatting.

## Fake Boundaries

- Time uses `FakeTimeProvider` in Server and CLI and fake timers in Runner and
  Web.
- HTTP uses `WebApplicationFactory` with `TestServer`, stubbed `fetch`, MSW, or a
  recording handler according to package.
- Web SignalR uses recording Server hub contexts or the configured Web fake.
- Runner control WebSocket uses fake sockets or the typed recording transport.
- Processes use fake Runtime or command executors.
- Server data uses in-memory SQLite and migrated templates.
- Files and static data use in-memory providers or package test-data catalogs.

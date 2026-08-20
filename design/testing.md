# Testing

Tests lose value when their purpose, ownership boundary, and execution
dependencies are one classification. That model pushes product behavior into
expensive suites, duplicates scenarios across layers, and makes parallelism the
remedy for tests that are intrinsically slow. Mohist instead treats every test
as an executable Spec. A Spec must state an intentional contract and prove it at
the narrowest boundary that fully owns its meaning.

## Design Drivers

- Most quality evidence must complete before a change merges. The complete
  local gate must finish within five minutes.
- Product meaning must survive a move to a cheaper execution boundary. Test
  purpose must therefore be separate from test resources and integration scope.
- One owner must prove each claim. A wider test must not repeat a scenario that
  a narrower owner already proves.
- Tests must produce the same result on every developer machine and in CI.
  Neither L0 nor L1 may depend on an external environment.
- Test code is product code. It must be readable, maintainable, and owned by the
  team that owns the contract.

Mohist does not use Unit, Integration, Browser, or deployment depth as semantic
test kinds. Those labels mix a claim with the mechanism used to prove it. Mohist
also does not target a fixed ratio of narrow and wide tests. Every claim chooses
its level from ownership. The resulting distribution is an outcome, not a
quota.

Process sharding is not a test design strategy. Parallel execution may improve
throughput after isolation and intrinsic cost are correct. It must not hide an
expensive Spec, fixture, application host, or framework runtime.

## Specification Model

Every test is an executable Spec. A behavior Spec has three independent
dimensions: Kind, Level, and Resources.

- **Kind** states whether the claim is a product promise or an internal design
  decision.
- **Level** states which code ownership boundary owns the claim.
- **Resources** state which controlled execution mechanisms the proof needs.

Architecture Specs are Design Specs with a structural execution track instead
of a behavior Level. None of these dimensions permits access to an external
environment. The complete Spec portfolio contains every behavior track and
every Architecture track exactly once.

### Kind

- A **Product Spec** states behavior that Mohist promises to a product actor
  through a supported product surface. Product actors include users, CLI users,
  public API consumers, and operators. The Spec uses product language and makes
  the intended product shape readable without implementation knowledge.
- A **Design Spec** states an intentional internal contract. It may define a
  component API, algorithm, invariant, collaboration, lifecycle, failure rule,
  private application protocol, cost bound, or structural constraint. It uses
  stable design language instead of incidental implementation steps.

External observability is necessary but not sufficient for a Product Spec. A
fact observed only by another internal application remains a Design Spec unless
Mohist exposes that fact as a supported product contract.

A Product Spec may call a pure function, an in-memory domain object, an HTTP
endpoint, or a browser. A Design Spec may use the same entry points. The claim,
not the entry point, determines the Kind.

An Architecture Spec is a Design Spec that proves structural constraints. A
Browser-backed Spec is normally a Product Spec whose claim depends on browser
layout or interaction. Browser is a Resource, not a Kind.

### Level

- **L0** owns one component or module inside one application. It may compose
  production objects owned by that boundary. Every component, module, and
  application outside the boundary is a controlled collaborator.
- **L1** composes one complete application boundary in one hermetic test
  environment. Web, Server, CLI, Runner, and other independently built
  applications have separate L1 tracks. Every boundary outside the owning
  application is fake or in-memory.

An L0 owner must be an existing named component or module with a stable
contract. A Spec must not invent a temporary component boundary around
unrelated classes. A claim that spans peer modules must split into independent
port contracts or move to L1 when complete application composition is part of
the claim.

| Kind | L0: component or module | L1: application |
| --- | --- | --- |
| Product Spec | Supported product behavior owned by one internal component or module. | Supported product behavior that requires one complete application. |
| Design Spec | Internal contract owned by one component or module. | Internal application composition or lifecycle contract. |

Every cell has the same hermetic boundary. Moving right adds in-process
application scope only. It never adds an external host or service.

### Resources

Resources include controlled SQLite, HTTP, Orleans, Browser, and similar
mechanisms. A Resource may be used at L0 or L1. It controls fixture ownership,
scheduling, and cost accounting. It does not determine Kind or Level, create a
new test category, permit a real service, or join ownership boundaries.

A test class declares the Resource set that its Specs require. The empty set
uses the owning track's default lane. The canonical test plan declares each
available lane, its Resource set, and capacity. Every declared Resource set must
map to exactly one lane. An unknown or ambiguous mapping is an invalid
configuration. A fixture implements Resource setup, reset, and lifetime; it does
not declare classification metadata.

The language runtime and controlled browser used by a test runner are test
infrastructure. A product subprocess or an external service is a product
dependency and is forbidden.

### Identity And Metadata

A behavior execution track is identified by owning application and Level. Kind
is declared and reported across tracks. Resources may be used within either Level.
An Architecture track instead declares an application or repository structural
scope and has no behavior Level. File and project names do not define these
dimensions.

A file or test class declares one Kind and must not mix Product and Design
Specs. A test class declares its required Resource set. A behavior track supplies
the application and Level. An Architecture track supplies its structural scope.
An individual test method must not override Kind, Level, structural scope, or
Resource requirements.

Existing `*Tests`, `*Specs`, `.test`, and `.spec` names may remain. They must not
act as semantic or execution classifications.

One file owns one subject. Active support names state their responsibility:
`Fixture` owns reusable data or lifetime, `TestHost` owns an application
boundary, `Probe` exposes state for assertions, and `TestFactory` constructs
collaborators. Active test names must not use `Harness`.

## Placement And Ownership

### Shift Left

A Spec must run at the narrowest ownership boundary that fully owns its claim.
This is the Mohist shift-left rule. It moves proof earlier and makes it cheaper
without changing what the Spec means.

Moving a Product Spec to a narrower boundary does not turn it into a Design
Spec. Removing HTTP, SQLite, Orleans, Browser, or an application host does not
change Kind. Adding or removing a Resource does not by itself move a Spec
between L0 and L1.

An L1 Spec is valid only when the complete owning application contributes
behavior to the claim. Application routing, composition, framework lifecycle,
and application-wide policy can require L1. A module state transition,
calculation, or invariant is L0 when one module fully owns it, even when its
proof uses a controlled database or framework runtime.

A Spec must not start a complete application only to replace one collaborator.
When a consumer needs one narrow read or decision capability, production code
must expose that stable capability as a port. The production adapter implements
the port. The Spec supplies controlled state when the adapter is not part of the
claim. Testability must reinforce a real production boundary; it must not create
interfaces for incidental private details.

### Claim Ownership

Each product behavior has one canonical Product Spec. It owns the
representative examples that define the product contract. Design Specs may add
exhaustive cases for an algorithm, internal invariant, adapter contract,
lifecycle, or cost bound. They must not repeat the product scenario matrix.

One claim must not exist at two Levels. Coverage is a measurement, not a claim.
A test that exists only to increase coverage, preserves no intentional
contract, or fails after an unrelated refactor must be deleted or rewritten.

There is no target count or percentage for L0 and L1. Every L1 Spec must explain
which application behavior would be absent from a narrower proof. Every L0 Spec
must name the stable component or module that owns the claim.

### Cross-Application Contracts

An L1 Spec has exactly one owning application. A Web L1 Spec must not start or
call Server, CLI, or Runner. The same rule applies to every application. Other
applications are controlled interfaces, fakes, or in-memory protocol peers.

Cross-application behavior must split into independently executable contract
Specs at each application boundary. Producer and consumer Specs may share one
canonical schema or protocol fixture. They must not share the production
adapter implementation that they are meant to verify independently. L1 must not
run multiple applications together to prove the contract.

### Spec Language

A Product Spec name, setup, action, and assertion must use product terms and
observable outcomes. Its body must expose the precondition, product action, and
expected product outcome without requiring the reader to open fixture code.

A Design Spec must expose the intended internal contract. Its body must name the
owned boundary, stimulus, and invariant. It must not preserve an incidental call
sequence, storage layout, or private method result unless that fact is an
intentional collaboration, compatibility, or cost contract.

Every Spec must state one claim, its owner, and its expected outcome or
invariant. Fixtures may hide transport, persistence, and host mechanics that are
not part of the claim. Helpers must not hide the complete scenario behind an
opaque assertion.

Polling, heartbeat, status, dashboard, and cleanup paths must cost only what
their current relevant data requires. Internal cost bounds are Design Specs.
Product-facing service levels are Product Specs. A cost Spec must hold current
work constant, vary unrelated history, and assert command, deserialization, or
downstream-call counts instead of elapsed time.

## Isolation And Determinism

### External Environment

L0 and L1 Specs must pass without an external network, Git process, Node
product process, OpenCode, system service, environment configuration, host
filesystem, or external database. A wider ownership boundary never permits less
isolation.

- Specs use controlled boundaries for network, processes, Git, shell, Agent
  binaries, databases, environment variables, and files.
- Specs must not read or write host temporary directories, `HOME`, the
  checkout, build output, or assembly content roots.
- Composition uses in-memory stores, catalogs, file providers, and named
  shared-memory SQLite. It must not resolve a physical filesystem adapter.
- Web event WebSocket uses recording Server sockets or the configured Web fake.
  Runner control WebSocket uses fake sockets or the typed recording transport.
- Built-in prompts, templates, Workflow Definitions, Skills, and assets enter a
  Spec as embedded catalogs or explicit in-memory values.
- Pure lexical path operations may use fixed virtual roots. Host-derived paths
  are forbidden.

`MemoryStream`, `StringReader`, in-memory `IFileProvider`, and in-memory SQLite
are memory boundaries rather than filesystem access.

Production health checks, telemetry, and progressive deployment evidence are
operational verification. They do not weaken or replace the hermetic Spec gate.

### Time And Coordination

Product time must be injectable. C# uses `TimeProvider`. TypeScript uses fake
timers or an injected `now` function.

- Specs use fixed timestamps or fake time. They must not read the wall clock
  for seed data, deadlines, or tolerance assertions.
- Specs must not wait with `Delay`, `setTimeout`, `Sleep`, `Task.Yield`, timers,
  spin waits, polling, or a loop over elapsed time.
- Specs wait for an awaitable boundary signal such as a callback, observer,
  channel, or `TaskCompletionSource` with asynchronous continuations.
- Fake time advances only product semantics such as expiry, timeout, or retry
  backoff. It must not release product polling.
- Existing `TestWait` usage is migration debt and is forbidden in new code.

Concurrent behavior must use an explicit state machine. Tests must coordinate
with queues, events, or boundary signals. Scheduler timing must not decide the
result.

### State And Order

A result must not depend on execution order, parallelism, scheduling, locale,
or a previous test.

- Fixed timestamps use values such as `2026-01-01T00:00:00Z`.
- A Spec must not depend on array, object-key, database, or scheduling order
  unless the product contract defines that order.
- Mutable shared state must have an explicit reset or per-Spec identity.
- A flaky Spec must be fixed or deleted. It must not be skipped or retried into
  green.

Web Specs run with `isolate: false`. Mutable module state and control ports must
provide a reset seam that `afterEach` invokes. Web HTTP uses MSW. An unhandled
request fails its owning Spec even when product code catches the request error.
Web event WebSocket and notification boundaries use configured fakes. Web Specs must
not add local `vi.mock` calls. Unexpected `console.error` or `console.warn`
output fails unless the Spec captures and asserts it. The normal Web suite and
shuffled execution must both pass. A shuffle failure is a state leak.

## Feedback And Verification

### Public Command Surface

The root package exposes four validation intents. These commands are the stable
developer interface. CI uses the same plan and executor through closed
application and repository scopes. Test frameworks, project files, apphosts,
track IDs, worker counts, report paths, and scheduling flags are internal gate
details.

| Command | Scope | Intended use | Final local acceptance |
| --- | --- | --- | --- |
| `npm run test:fast` | Every L0 and Architecture Spec plus fast static checks | Broad inner-loop feedback after a local change | No |
| `npm run test:app -- <application>` | Every L0, L1, application-scoped Architecture Spec, and static check owned by one application | Complete feedback for one application boundary | No |
| `npm test` | The complete hermetic Spec portfolio with test-plan validation, reports, and duration policy | Changes to shared contracts or test infrastructure | No |
| `npm run verify` | Fresh application builds, all non-Spec repository checks, and the complete hermetic Spec portfolio | Final local handoff; CI applies the same contract through Gate | Yes |

`test:fast` is broad and shallow. `test:app` is narrow and deep. `npm test`
is broad and deep. `verify` adds the repository-wide acceptance checks. The
name `verify` applies only to the complete local action. CI execution jobs use
their evidence owner as the name. The final CI aggregation job is `Gate`.

`test:fast` excludes L1 by definition. It may use normal incremental
compilation. `test:app` accepts exactly one application ID declared by the
canonical test plan and may compile that application incrementally. An unknown
or missing application fails before any test starts.
`npm run test:app -- --help` derives the valid IDs and their owned tracks from
that plan without running tests. `npm test` may also use incremental compilation.
It runs no general formatting, documentation, or build check. It does not claim
that documentation, a fresh whole-repository build, or source identity passed.

During development, a developer may still run a framework-native focused Spec
for the changed owner. Focused execution is a debugging tool, not a root command
contract or acceptance claim. Mohist must not add a generic public filter DSL
until more than one test framework has a concrete need that the four intents
cannot express.

`test:canonical`, `test:budget`, `test:budget:all`, framework-specific workspace
commands, and raw track selection are not part of the target public surface.
Their required mechanics belong behind the four commands. Obsolete aliases must
be removed rather than kept as alternative gate paths.

### Command Semantics

One checked-in test plan declares application and Level for behavior tracks and
structural scope for Architecture tracks. It also declares Resource lanes,
non-Spec checks, compilation policy, reports, and deadlines. Each public command
selects from this same plan. Application IDs are values in this plan, not a
second registry. `verify` must reuse the full plan directly under its own
absolute deadline; it must not shell out to a second public command with a fresh
deadline.

Each command owns all compilation required by its scope. `test:fast`,
`test:app`, and `npm test` may reuse valid incremental outputs. `verify` owns one
fresh build for each application scope. Every track in one application scope
uses that application's exact build without a hidden rebuild. Application
scopes may build concurrently only when their output paths and claimed
Resources are isolated. `verify` requires a clean index and worktree and fails
if the revision or source state changes during the run. A CI application job
uses the same build rule for its application and must not consume an output
from a different source revision.

Every command must print one uniform result summary containing:

- selected applications, behavior Levels, and Architecture scopes;
- nonzero total, passed, failed, skipped, and not-run counts for every selected
  Spec track;
- passed or failed status for every selected non-Spec check;
- track and suite wall time and budget results; and
- the first failure.

`npm test` and `verify` must also persist the full test plan, source revision and
dirty state, selected Kinds and Resource lanes, reports, startup counts, cleanup
state, first failure, and artifact directory. Each CI application or Repository
job persists the same evidence for its scope so that Gate can validate the
complete candidate. `test:fast` and `test:app` must not pay this full evidence
cost.

A selected Spec track that is missing, empty, skipped, or not run fails the
command. A selected non-Spec check that does not run or pass also fails. A
required report that is stale or missing fails. A track or check outside the
declared command scope is unselected, not skipped. When work runs concurrently,
the first failure is the earliest failed item in canonical plan order, not the
first process to finish. Unknown arguments and selections fail closed. No
environment variable, including a CI marker, may silently change selection,
budgets, isolation, retry policy, or pass/fail semantics.

Exit code zero means the complete declared scope passed. Exit code one means a
Spec, check, budget, infrastructure operation, cleanup, or deadline failed. Exit
code two means the invocation or checked-in command configuration is invalid.

### Local And CI Parity

`npm run verify` is the final local handoff command. It owns all application and
Repository scopes, their evidence, source identity, cleanup, and one absolute
five-minute deadline. It must not shell out to another public command with a
fresh deadline or be followed by another full test command.

CI decomposes the same acceptance plan by ownership. It has one execution job
for each independently built application, one `Repository` job, and one final
`Gate` job. The canonical plan declares the application IDs `server`, `web`,
`cli`, `runner`, and `slack`. Their CI display names are `Server`, `Web`, `CLI`,
`Runner`, and `Slack`.

- An application job owns its application's fresh build, L0 and L1 behavior
  tracks, application-scoped Architecture tracks, owned static checks, reports,
  budgets, and cleanup.
- `Repository` owns plan validation, cross-application and repository
  Architecture tracks, documentation, formatting, and repository-wide static
  checks that no application owns.
- `Gate` depends on every application job and `Repository`. It validates that
  all evidence has the same source revision and plan identity and that the
  complete plan appears exactly once. It does not build or run a Spec.

Only `Gate` is the required branch-protection check. A failed, cancelled, or
missing producer job makes Gate fail. Changed-path detection must not waive an
application, Repository, or Gate for final acceptance.

The CI job set is an explicit projection of the applications in the canonical
plan. Automated enforcement must fail when the workflow and plan application
sets differ. The workflow may name an application or the Repository scope. It
must not select projects, tracks, Kinds, Levels, Resource lanes, or test cases.
CI uses a closed executor interface for these evidence scopes. That interface
is not a fifth public validation command or a generic filter DSL.

The closed executor uses the same summary fields, failure ordering, and exit
codes as the public commands. Scope selection is an explicit invocation input.
No CI environment marker may change selection or pass/fail semantics.

Workflow YAML owns checkout, toolchain installation, dependency caches, outer
job failsafes, job dependencies, and artifact transfer. It must not own test
project lists, apphost arguments, class filters, worker counts, retries, report
validation, or scope deadlines. An outer CI timeout may terminate a stuck
scope, but it must exceed that scope's own absolute deadline and cannot define
normal failure semantics.

Application and Repository jobs for one candidate may run concurrently. Within
one application job, the plan owns all concurrency. A semantic track is not a
shard. CI and local commands must not split one application or track by project,
class, hash, or process merely to reduce wall time. The plan may run independent
tracks or isolated Resource lanes concurrently inside their owning application.
Parallel execution is valid only for independently owned scopes, tracks, or
Specs with proven isolation. Worker capacity is a memory and host-resource
bound, not a speed control. Retry, sleep, skip, allowlist, threshold increase,
timeout increase, and global serialization are not recovery mechanisms.

Local and CI parity means the same plan, ownership, budgets, evidence protocol,
and pass/fail rules. It does not require the local process graph and the CI job
graph to be identical. Local `verify` may execute application scopes on one
host. CI executes them on separate runners and Gate aggregates their evidence.

Gate implementation, evidence, scheduling, and process-cleanup rules live in
[`scripts/test-duration/README.md`](../scripts/test-duration/README.md).

### Duration Budgets

The duration gate must enforce these hard limits for every complete selected
track:

- Each application-and-Level behavior track combines both Kinds and all of its
  Resource lanes into one population. Its L0 p95 is at most 50 ms. Its L1 p95
  is at most 500 ms.
- p95 uses nearest rank: sort the population by duration and select the one-based
  item at `ceil(0.95 * count)`.
- One L0 or Architecture Spec is at most 500 ms. One L1 Spec is at most 5 s.
- Each application-and-Level track has an enforced wall-time deadline.
- Each Architecture track has an enforced wall-time deadline.
- The complete local gate is at most five minutes.

The L1 single-Spec limit is a failure ceiling, not a target. A budget breach is
a test defect and must fail acceptance. No slow-Spec exception may turn the
gate green. A track without a seeded baseline is an incomplete migration state;
it must not waive the final gate.

Duration evidence must report population p50, p95, and maximum, track wall time,
fixture setup time, and application, Orleans, or Browser startup counts. Shared
fixture setup remains part of track cost even when a test runner does not
attribute it to an individual Spec. Stable duration evidence requires the host
isolation defined by the duration gate. Setup time and startup counts are
diagnostics until this document defines a hard limit for them. The gate must not
fail or waive another budget based on an undeclared limit.

L0 Spec files should remain below 300 lines. L1 Spec files should remain below
800 lines. Browser-backed Specs run in a separate Resource lane. These are
review guidelines. The source-file ratchet is the automated size gate.

## Server L1 Resource Ownership

This section applies only when a claim requires the complete Server application
boundary. In-memory SQLite, an in-process Orleans runtime, or another controlled
Resource does not by itself make a Spec L1.

An xUnit collection is a scheduling unit. Classes in one collection run
serially.

- Server L1 Specs share one assembly fixture and one default application host.
  Each Spec creates a unique Project for business state.
- Project identity does not isolate product-global clocks, runner discovery,
  instrumentation, hosted dispatchers, or other application-global state. A
  suite that mutates one such Resource owns one dedicated host and serializes
  access to that Resource.
- A separate Resource suite requires an incompatible host configuration or a
  disjoint application-global Resource domain with its own reset boundary. A
  test class alone is not a Resource boundary. Independent Resource suites may
  run concurrently.
- Only truly process-static state requires a non-parallel collection.
- Specs that rewind public-projection checkpoints use a dedicated host without
  the background projector. Command Specs project the affected Session
  directly. Only drain-contract Specs scan the complete backlog. The hosted-loop
  contract owns its own host.
- An L1 Spec must not retain a framework runtime, host, or collection that its
  application-level claim does not require. Removing that Resource does not by
  itself move the Spec to L0.
- `WebApplicationFactory` and `InProcessTestCluster` use in-memory transport and
  must not bind host ports. TCP behavior uses an in-process transport contract
  or fake. No Spec allocates a network port.
- Database Specs clone the migrated in-memory SQLite template. Only database
  initialization Specs run the migration chain from an empty in-memory store.

The canonical local gate and CI must not split Server L1 classes across
processes. `maxParallelThreads` limits memory pressure. It must not compensate
for expensive Specs or shared Resources.

## Automated Enforcement

- Behavior tracks must select Specs by application and Level. Architecture
  tracks must select Specs by structural scope. Kind must be declared, validated,
  and reported without requiring an independent selector. Resource metadata must
  map each test class to exactly one plan lane.
- `scripts/test-duration/` must enforce suite and track deadlines, population
  budgets, single-Spec limits, report freshness, and nonzero totals. It must
  report setup and startup cost until explicit limits exist.
- Architecture Specs must enforce layer dependencies, naming, namespaces,
  public surfaces, and analyzer wiring.
- Banned API analyzers must reject wall-clock time, scheduler waits, host paths,
  physical adapters, real filesystem access, and direct environment reads.
- Vitest must restore mocks, globals, and environment state and select projects
  by application and Level.
- Web guards must reject local module mocks and unhandled MSW requests. A
  regular shuffled suite records its seed.
- The file-size ratchet must block new first-party source files above 1000 lines
  and growth of files that already exceed that limit.
- The formatter ratchet must require changed JavaScript, TypeScript, TSX, and
  CSS files to satisfy Biome formatting.

## Status

The current Server projects and duration gate still use Unit and Spec as
execution tracks. The Spec-to-Unit migration inventory still classifies tests
by runtime dependencies. Root scripts expose overlapping `test`, `test:fast`,
`test:canonical`, and `test:budget` paths. They select project and track names
instead of the target behavior-track model. The canonical test plan does not
declare application, Level, or Architecture scope metadata, and there is no
`test:app` command.

The current CI workflow groups work by runtime and project instead of
application ownership. It owns direct `dotnet test`, compiled apphost, and
workspace Vitest invocations together with project lists, worker counts,
per-step timeouts, and report checks. It has no independent application jobs,
Repository evidence producer, or final Gate aggregation. The duration gate
still has a slow-test allowlist mechanism and does not enforce separate fixture
startup and runtime-start counts or the complete acceptance evidence.

These are migration gaps. The target is application-and-Level behavior tracks
plus structurally scoped Architecture tracks, with Product and Design as
orthogonal declared Kinds and controlled Resources as independent scheduling
and cost metadata. Existing file suffixes may remain, but they must stop acting
as classifications. New Specs must follow the target model before the migration
is complete.

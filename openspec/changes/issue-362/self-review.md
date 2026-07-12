# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: poison settlement
  Resolution: Exhausted handler rows are collected and passed to `IDeadLetterStore.SettleAsync`, which upserts by `(Source, Id, FailingHandler)` and marks the source event in one SQLite transaction. A source-mark failure commits neither side. Tests cover rollback and conflict-safe retry.
  Status: resolved

- [ID: item-2]
  Severity: blocking
  Scope: Agent launch durability
  Resolution: `AgentJobGrain` persists input, candidate runner, stable work id, retry counters, timestamps, and terminal result. Submission and activation await a tracked dispatch attempt. Runner acceptance replay uses the same `(AgentJobId, WorkId)`, and pending prepared work is runnable so the acceptance/status crash window cannot drop it. `AgentLauncher` always replays the stable job instead of treating session labels as the launch claim.
  Status: resolved

- [ID: item-3]
  Severity: warning
  Scope: Hermes contract
  Resolution: Restored the documented background best-effort dispatcher. Webhook and state-load failures are logged and swallowed, so Hermes does not enter durable retry/dead-letter flow.
  Status: resolved

- [ID: item-4]
  Severity: test-gap
  Scope: reminder self-healing
  Resolution: The dispatcher fixture now has two silos backed by one shared reminder table. A deterministic test advances the injected `FakeTimeProvider` to fire a real reminder without `PulseAsync` or direct callback invocation, kills the hosting silo, waits for membership convergence and persisted reminder reload, then proves the next real tick delivers on the surviving silo. The fixture restores a second silo afterward; delivery and reload completion come from explicit `TaskCompletionSource` signals guarded by `TestWait`'s fixed attempt budget. Each attempt advances fake time and completes an unrelated read-only Orleans grain turn, with no wall-clock wait and no dispatcher activation or pulse.
  Status: resolved

- [ID: item-5]
  Severity: warning
  Scope: operator security
  Resolution: Superseded by item-14 after the next review proved that address/header checks cannot establish directness through a loopback proxy. Public-listener disablement and response redaction remain; caller identity now comes from the operator credential.
  Status: resolved

- [ID: item-6]
  Severity: minor
  Scope: DI lifetime
  Resolution: Singleton Epic handlers resolve `EpicQuerier` inside an async scope per delivery. Full-host integration startup and closed-generic handler specs pass with scope validation.
  Status: resolved

- [ID: item-7]
  Severity: info
  Scope: test determinism
  Resolution: Preserved the AI review repair replacing dispatcher test wall-clock values with fixed `EventTime`.
  Status: resolved

- [ID: item-8]
  Severity: blocking
  Scope: source-event settlement routing
  Resolution: `UndeliveredEvent.Origin` now flows through `IEventStore.MarkDispatchedAsync` and atomic dead-letter settlement, so the persisted table is authoritative. Custom/future CloudEvent sources that use the WorkflowRun fallback can be delivered and marked instead of failing after their handlers run. A real SQLite regression covers append → list undelivered → mark for a non-Mohist source URI.
  Status: resolved

- [ID: item-9]
  Severity: warning
  Scope: undelivered-query index metadata
  Resolution: The existing `AddEventDeliveryDispatchedAt` migration already created filtered indexes for WorkflowRun, Issue, and Epic event scans, so deployed databases were covered. `MohistDbContext`, the issue migrations' target models, and the latest snapshot now declare the same `(Source, Id, DispatchedAt)` indexes and filters. A migration spec pins both EF metadata and the migrated SQLite schema.
  Status: resolved

- [ID: item-10]
  Severity: test-flake
  Scope: process-global console capture
  Resolution: The two unit-test classes that replace `Console.Out` / `Console.Error` now share a non-parallel xUnit collection, preventing one test from restoring the process-global writers while the other is capturing them.
  Status: resolved

- [ID: item-11]
  Severity: blocking
  Scope: Agent subscription contract
  Resolution: Restored `AgentSubscriptionDispatchHandler` catch-and-log behavior while preserving cancellation propagation. A failing launcher is observable as a warning but completes the handler successfully; dispatcher coverage proves the source row settles without a dead letter.
  Status: resolved

- [ID: item-12]
  Severity: warning
  Scope: production service graph
  Resolution: Orleans and the Web host share one `IServiceCollection`, so application registrations now live only in `ConfigureMohistServices`; `ConfigureMohistSilo` contains Orleans infrastructure only. A regression pins one event bus, event store, dead-letter store, dispatcher, subscription set, handler, Hermes dispatcher, AgentJob observer, and TimeProvider registration.
  Status: resolved

- [ID: item-13]
  Severity: test-gap
  Scope: reminder test timing
  Resolution: Removed `WaitAsync(TimeSpan)` from the reminder/failover spec. It advances only the injected fake clock and probes the shared reminder-table and handler-delivery signals through `TestWait`'s fixed attempt budget; the between-attempt hook completes an unrelated read-only Orleans grain turn without sleeping or touching the dispatcher.
  Status: resolved

- [ID: item-14]
  Severity: blocking
  Scope: dead-letter operator authorization
  Resolution: Public listeners still do not map the routes. Loopback routes now require a high-entropy operator credential created in a user-only, non-symlink file (or supplied explicitly by environment/config); `mo` reads the same credential and sends a dedicated header. IP addresses and forwarding headers are no longer treated as identity. Missing credentials are rejected before store or handler access, and list responses are `no-store`.
  Status: resolved

- [ID: item-15]
  Severity: blocking
  Scope: Agent trigger correlation
  Resolution: Trigger event/subscription labels are merged into the first `OpenAsync` command, before `EnsureSubmittedAsync`. There is no post-submit metadata write that can fail after the event is settled. Stable session/job identities remain unchanged on replay.
  Status: resolved

- [ID: item-16]
  Severity: blocking
  Scope: Runner admission
  Resolution: Runner lifecycle changes and `AssignAgentJobAsync` share one serialized gate. Admission rejects offline runners, checks current capacity inside the Runner aggregate, and only then persists work. Concurrent one-slot submissions produce one assignment and one `capacity-exhausted` rejection; post-unregister submissions return `runner-offline`.
  Status: resolved

- [ID: item-17]
  Severity: warning
  Scope: diagnostics and CLI recovery visibility
  Resolution: Stored and returned operator errors are bounded to a stack-free first line with path/control redaction. The default CLI table renders `Pending`/`Redelivering` status and strips ANSI, carriage-return, newline, and other terminal controls from untrusted cells.
  Status: resolved

- [ID: item-18]
  Severity: test-gap
  Scope: registration, FIFO, workflow delivery, and AgentJob recovery
  Resolution: Subscription patterns are validated during reflection registration; FIFO asserts handler observation order directly; stage-lock specs now deliver persisted workflow events through DI-registered `EventDispatcherService` and `WorkflowStageLockReleaseHandler`; AgentJob persistence deactivates and replays a non-null AgentConfig. That test exposed nullable `JsonElement` persistence drift, so raw JSON is now the sole durable AgentConfig representation and is reconstructed on demand.
  Status: resolved

- [ID: item-19]
  Severity: blocking
  Scope: Agent poll recovery
  Resolution: Agent jobs now use the same stable reported-work key contract as workflow work. `ReconcileAgentJobsAsync` reoffers accepted running work while its key is absent from `inFlight` and `awaitingAck`, and stops reoffering once the runner reports it. A lost first poll response returns the same AgentJob/work pair on the next poll.
  Status: resolved

- [ID: item-20]
  Severity: blocking
  Scope: Runner poll concurrency and shared capacity
  Resolution: One transient Runner poll gate excludes overlapping reconciliation rounds. Agent admission is retried while that gate is held, and all active Agent works are subtracted before workflow claims. Tests cover one admitted overlapping poll, admission during reconciliation, and a one-slot runner receiving only its runnable Agent work instead of both Agent and workflow dispatches.
  Status: resolved

- [ID: item-21]
  Severity: blocking
  Scope: Inbox durable hint recovery
  Resolution: `InboxProjectionHandler` now writes the inbox row and `InboxItemPersisted` event through one caller-owned database transaction. A hint append failure rolls back the projection; replay then commits one row and one hint instead of returning early on `AlreadyExisted`.
  Status: resolved

- [ID: item-22]
  Severity: warning
  Scope: operator diagnostic redaction
  Resolution: Diagnostic input is bounded before processing, ANSI/control sequences are neutralized, embedded single-line method frames are replaced, and Unix, Windows, `file://`, and key/value path forms are redacted as substrings. Unit and API tests cover the reported examples.
  Status: resolved

- [ID: item-23]
  Severity: warning
  Scope: operator credential interoperability
  Resolution: Server and CLI share environment-before-config resolution for direct tokens and token paths. The CLI parses `~/.mohist/config.jsonc` with JSONC comment and trailing-comma support, so a server configured only with `Mohist:OperatorTokenPath` works without a second CLI override.
  Status: resolved

- [ID: item-24]
  Severity: test-gap
  Scope: settlement fakes and four-table pull
  Resolution: Unit and grain fakes snapshot and roll back both source and dead-letter state after an injected post-mark failure. A real SQLite spec inserts interleaved WorkflowRun, Issue, Epic, and AgentSession events, asserts global `(Source, Id)` order, marks one origin, and proves only that row leaves the pull set.
  Status: resolved

- [ID: item-25]
  Severity: minor
  Scope: CLI product spec
  Resolution: `docs/cli-reference.md` now includes the `event` root, dead-letter list/redeliver commands, status behavior, credential prerequisite, and the shared resolution order. CLI reference contract tests pass.
  Status: resolved

## Verification

- Dispatcher/Hermes unit slice: 30 passed.
- AgentJob persistence + AgentLauncher specs: 30 passed.
- Dead-letter, reminder/failover, API, Agent, and Epic focused server specs: 81 passed.
- Origin-aware settlement regression slices: 42 server specs and 36 server unit tests passed.
- Repaired dispatcher suite: 7 passed, plus the failover case passed in three concurrent processes; it uses real reminder ticks, shared persistence, fake time, and deterministic signals.
- Event-delivery index model + migration regression: 1 passed.
- Console-capture slice: 9 passed in five consecutive runs.
- Final review-repair slices: Agent poll/capacity 24; Inbox projection/hint 40; diagnostic unit/API 16; credential CLI/server 12; dispatcher fake + four-table SQLite 40; CLI docs/event 18. All passed.
- Full CI-equivalent validation: CLI 874; server unit 1373; architecture 24 with 3 pre-existing skips; server spec 2848 with 9 pre-existing skips; Web 4596; Runner 1007; Node test-boundary checks passed.
- `git diff --check` and `tasks.json` JSON validation pass.

## Follow-up Items

- Orleans 10.1 `RegisterOrUpdateReminder` exposes no activation cancellation-token overload, so the review's token-propagation suggestion is not applicable.
- Epic event publication atomicity remains the pre-existing out-of-scope producer issue recorded in the formal review.

<promise>PASS</promise>

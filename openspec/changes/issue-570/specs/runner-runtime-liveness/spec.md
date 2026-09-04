### Requirement: Control-plane continuity is independent of runtime health

The Runner's control-plane channels — poll, work report (including awaiting-ack retries), heartbeat, and the dispatch SignalR connection — SHALL continue operating while any runtime (OpenCode or Pi) is unhealthy, rebuilding, quarantined, or absent. Runtime unhealthiness MUST NOT pause the poll loop, MUST NOT suspend report retries for completed work, and MUST NOT allow Runner presence to lapse; a wedged or resource-runaway runtime MUST NOT convert into whole-Runner loss. Local durability gates that are unrelated to runtime health (task-log delivery store, runtime-event outbox) MAY still gate admission, but a runtime failure alone MUST NOT.

#### Scenario: OpenCode runtime is quarantined and rebuilding

- **WHEN** the OpenCode runtime exits or fails its health check and enters quarantine/rebuild while other work is in flight
- **THEN** the Runner SHALL keep polling, keep sending heartbeats, and keep retrying reports for works awaiting acknowledgement
- **AND** the server-side Runner presence MUST NOT time out solely because the OpenCode runtime is unhealthy

#### Scenario: Pi runtime is unavailable while OpenCode is healthy

- **WHEN** the Pi runtime fails to start or becomes unhealthy while the OpenCode runtime is ready
- **THEN** the Runner SHALL continue polling and SHALL claim work that does not require the Pi runtime
- **AND** no work bound to OpenCode and no runtime-independent work SHALL be blocked by the Pi runtime's unhealthiness

#### Scenario: Awaiting-ack report during runtime outage

- **WHEN** a work item has produced its result and is awaiting owner acknowledgement while the required runtime is unhealthy or rebuilding
- **THEN** the report retry loop SHALL keep attempting delivery on its bounded schedule
- **AND** the result MUST NOT be discarded or altered because of the runtime's health

### Requirement: Runtime-bound work defers with preserved identity and never synthesizes a failure

When the runtime a work item requires is unavailable, the Runner SHALL defer that work item instead of executing it against the unavailable runtime. Deferral MUST preserve the work's identity (`ownerKind:ownerId:workId`), its dispatch payload, and its report key. A deferred work item MUST NOT be reported as a failed (or completed) result while deferred — a `runtime-unavailable` condition is not an execution verdict and MUST NOT be synthesized into one. When the runtime recovers, the deferred work SHALL execute under the original work identity without allocating a replacement work id. When execution was interrupted before a durable verdict existed, the deferral or resume path MUST NOT replay an assumed outcome; it SHALL reconcile against persisted execution facts.

#### Scenario: Claimed work requires an unavailable runtime

- **WHEN** the Runner holds a work item whose runtime (OpenCode or Pi) is not ready
- **THEN** the work item SHALL be deferred with its identity and dispatch payload preserved
- **AND** the Runner MUST NOT emit a result report (failure or success) for that work while it is deferred
- **AND** the deferred work SHALL continue to occupy its execution slot in the Runner's poll report until it executes or is retired by the owner

#### Scenario: Runtime recovers with work deferred

- **WHEN** a deferred work item's runtime becomes ready again
- **THEN** the work SHALL execute under the original work identity and report key
- **AND** the owner SHALL observe at most one outcome for that identity

#### Scenario: Interruption with an unknown outcome

- **WHEN** a work item's execution is interrupted (runtime quarantine, transport failure, or process restart) before a final verdict is durably recorded
- **THEN** the Runner MUST NOT replay or synthesize an outcome for that execution
- **AND** the interruption SHALL be surfaced as an execution observation with a reason, not as a result report

### Requirement: Per-runtime readiness gating

The Runner's claiming gate SHALL evaluate readiness per runtime: a work item's claim and execution SHALL be gated only on the readiness of the specific runtime it requires. One runtime's unhealthiness MUST NOT gate claiming or execution of work bound to another runtime or of runtime-independent work. Readiness diagnostics SHALL identify the failing runtime with its actionable diagnostic (failure stage and recovery suggestion) and MUST NOT be reported as a whole-Runner stop without that context.

#### Scenario: Runtime-specific gating

- **WHEN** the OpenCode runtime is not ready and the Pi runtime is ready
- **THEN** the Runner SHALL claim and execute Pi-bound and runtime-independent work
- **AND** only OpenCode-bound work SHALL be deferred

#### Scenario: Actionable diagnostic while deferred

- **WHEN** claiming of runtime-bound work is deferred because its runtime is unhealthy
- **THEN** the Runner SHALL emit the runtime's diagnostic identifying the runtime and its failure stage (for example `server-spawn-failed`, `health-failed`, or `server-exit`) with a recovery suggestion
- **AND** the diagnostic MUST NOT be presented as a failure of the deferred work itself

### Requirement: Bounded quarantine drain for wedged runtime generations

A quarantined OpenCode generation SHALL drain within a bounded deadline. The deadline SHALL be configurable and injectable (a test seam) so the expiry path is deterministically testable. When the deadline expires with turns still unresolved, the Runner MUST destroy the quarantined generation — terminate the OpenCode server process tree and destroy its hung transports — and the replacement generation build SHALL proceed without waiting for the wedged turns. A quarantined generation MUST NOT block its replacement indefinitely, and the runtime MUST return to a ready (or explicitly failed) state within the bound.

#### Scenario: Turn hangs in a quarantined generation

- **WHEN** a generation is quarantined while one of its turns never settles
- **THEN** the drain deadline SHALL expire and the generation SHALL be destroyed (process tree terminated, transports destroyed)
- **AND** the replacement generation build SHALL proceed and the runtime SHALL become ready again within the bound

#### Scenario: Orderly drain before the deadline

- **WHEN** a quarantined generation's active turns settle before the drain deadline
- **THEN** the generation SHALL be released and the replacement built without forced destruction

### Requirement: Bounded runtime shutdown and transport teardown

All runtime shutdown paths — process-tree termination and HTTP transport (undici dispatcher) close — SHALL complete within a bounded time. Transport close MUST NOT wait on hung in-flight requests: when the bound is reached the dispatcher SHALL be destroyed rather than awaited. Process-tree termination SHALL escalate from graceful signaling to forceful kill within a bounded grace period. A hung process or transport MUST NOT block a replacement runtime generation or Runner shutdown.

#### Scenario: Dispatcher close with a hung request

- **WHEN** the shared OpenCode dispatcher is closed while a request it issued never completes
- **THEN** the close SHALL complete within its bound by destroying the dispatcher
- **AND** the shutdown or rebuild path SHALL NOT hang waiting for the hung request

#### Scenario: Server process tree ignores graceful termination

- **WHEN** an OpenCode server process (or its descendants) does not exit after graceful signaling
- **THEN** termination SHALL escalate to a forceful kill within a bounded grace period
- **AND** the shutdown or replacement build SHALL complete without waiting on the wedged process tree

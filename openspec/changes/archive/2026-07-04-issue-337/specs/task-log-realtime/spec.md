### Requirement: An independent best-effort distribution rail fans task-log increments out to the Web

The server SHALL provide a dedicated task-log distribution publisher that fans each persisted increment out to connected Web clients over a dedicated hub method on `MohistHub`/`IEventsClient`. This rail SHALL be physically separate from the agent-session transcript channel: it SHALL use a distinct envelope type, a distinct hub method, and a distinct subscription filter. Task-log traffic SHALL NOT appear on the agent-session transcript channel, and transcript traffic SHALL NOT appear on the task-log channel. The publisher SHALL reuse the transcript publisher's architecture pattern (per-connection subscription-gated fan-out with per-send failure isolation), not its channel.

#### Scenario: A persisted increment is pushed on the dedicated task-log method

- **WHEN** the server persists a task-log increment for a work item and a Web client is subscribed to that task's increments
- **THEN** the server SHALL deliver the increment to that client over the dedicated task-log hub method
- **AND** the increment SHALL NOT be delivered over the agent-session transcript method

#### Scenario: Agent-session transcript traffic does not leak onto the task-log channel

- **WHEN** the existing agent-session transcript publisher fans out a transcript envelope
- **THEN** it SHALL be delivered only on the transcript channel
- **AND** no task-log client SHALL receive it as a task-log increment

### Requirement: Fan-out is on-demand and skipped when no client wants the task

The distribution rail SHALL push a task's increments only to clients that have indicated they want that task's live log (e.g. the task is expanded in the UI). When no connected client has indicated interest in a given task, the server SHALL skip fan-out entirely and SHALL NOT produce invalid pushes. This task-scoped, on-demand filtering SHALL be in addition to the existing per-connection type-subscription filter.

#### Scenario: No fan-out occurs when the task is not expanded by any client

- **WHEN** a task-log increment is persisted and no Web client has indicated interest in that task
- **THEN** the server SHALL skip fan-out for that increment
- **AND** no client SHALL receive it in real time, while it remains in the authoritative store for later query

#### Scenario: Only interested clients receive a task's increments

- **WHEN** two clients are connected and only one has indicated interest in the task
- **THEN** only that client SHALL receive the task's live increments
- **AND** the other client SHALL NOT receive them

### Requirement: Fan-out failure is isolated and never blocks persistence or execution

The task-log distribution rail is best-effort. A fan-out failure — no subscribers, a per-connection send that throws, or a network drop — SHALL be logged and swallowed. It SHALL NEVER block or fail the persistence of the batch, and SHALL NEVER block or fail the task's execution. Persistence SHALL have already completed before fan-out is attempted, so a dropped increment on the real-time rail is always recoverable from the authoritative store on terminal reconciliation.

#### Scenario: A per-connection send that throws does not abort fan-out or persistence

- **WHEN** the publisher sends an increment to a subscribed client and that send throws
- **THEN** the failure SHALL be logged and swallowed
- **AND** the increment SHALL already be persisted, and the publisher SHALL continue attempting remaining subscribed clients

#### Scenario: Fan-out with no subscribers does not throw

- **WHEN** an increment is persisted but no client is subscribed to the task
- **THEN** the publisher SHALL complete without throwing
- **AND** the batch's persistence SHALL be unaffected

#### Scenario: Best-effort failure isolation is demonstrable under simulated distribution failure

- **WHEN** a test simulates a fan-out failure (publish throws or has no subscribers) for a batch
- **THEN** the authoritative store SHALL still contain the complete batch
- **AND** the simulated failure SHALL be observable only as a logged, swallowed event

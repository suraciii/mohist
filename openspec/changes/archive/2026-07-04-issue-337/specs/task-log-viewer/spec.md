### Requirement: The panel live-appends increments while the task runs

While a task is executing and its expand area is open, the Web's task log panel SHALL subscribe to the task-log real-time hub channel and append each received increment to the displayed log, so the user sees the log refresh (near-)real-time without waiting for the task to reach a terminal state. The panel SHALL continue to render each appended line with its source label and timestamp. The user SHALL perceive a continuously refreshing log, with second-level latency acceptable and no need to wait for task completion.

#### Scenario: Lines appear as the task executes

- **WHEN** a task is still running and the user has expanded it
- **THEN** the panel SHALL append newly-arrived increments to the displayed log as they arrive
- **AND** the user SHALL see the log refresh before the task reaches a terminal state

#### Scenario: The panel does not wait for terminal state to show progress

- **WHEN** a long-running task emits output over time
- **THEN** the panel SHALL show that output incrementally during execution
- **AND** it SHALL NOT remain empty or static until the task finishes

### Requirement: The panel reconciles to the authoritative store on terminal state

When the task reaches a terminal state, the panel SHALL re-query the authoritative store (invalidating/refetching the existing issue-path query) so that any lines the best-effort real-time channel dropped are backfilled. After reconciliation, the panel's displayed log SHALL match the authoritative complete log for the task. The real-time channel is best-effort; the authoritative store is the source of truth the panel converges to.

#### Scenario: Dropped real-time lines are backfilled on terminal

- **WHEN** the real-time channel dropped some lines during execution and the task reaches a terminal state
- **THEN** the panel SHALL re-query the authoritative store
- **AND** the final displayed log SHALL include the previously-dropped lines, matching the authoritative complete log

#### Scenario: The displayed log converges to the authoritative log on terminal

- **WHEN** a task reaches a terminal state after live-appended increments
- **THEN** the panel SHALL reconcile against the authoritative store
- **AND** the resulting log SHALL equal the authoritative complete set of non-discarded lines

### Requirement: Phase 1 terminal-state rendering, truncation indicator, and empty state are preserved

The real-time live-append and terminal reconciliation SHALL layer onto the existing Phase 1 panel without changing its terminal-state line-by-line rendering, its truncation indicator, its empty-state message, or its source/timestamp display. The existing single issue-path query SHALL remain the authoritative source that the panel reconciles to.

#### Scenario: Terminal-state rendering is unchanged

- **WHEN** a task that has reached a terminal state is rendered
- **THEN** the panel SHALL render the authoritative log line-by-line with source and timestamp exactly as in Phase 1
- **AND** the truncation indicator and empty-state message SHALL behave as before

#### Scenario: A task with no live subscribers still shows its complete log on expand

- **WHEN** a task executed with no client watching in real time and the user expands it after the fact
- **THEN** the panel SHALL show the authoritative complete log from the issue-path query
- **AND** it SHALL NOT depend on having received real-time increments

### Requirement: The task-log real-time wiring is separate from the agent-session live channel

The panel's subscription to the task-log real-time hub method SHALL NOT alter or couple to the existing agent-session live channel wiring (`useEventsConnection` / `LiveTaskProvider`). The two live channels SHALL operate independently; subscribing to task-log increments SHALL NOT affect agent-session transcript delivery, and vice versa.

#### Scenario: Subscribing to task-log increments leaves the agent-session channel untouched

- **WHEN** the task log panel subscribes to task-log increments
- **THEN** the agent-session live channel SHALL continue to operate unchanged
- **AND** agent-session transcript delivery SHALL NOT be affected by the task-log subscription

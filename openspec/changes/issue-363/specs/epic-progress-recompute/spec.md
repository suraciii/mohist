### Requirement: Epic progress recomputation is named "recompute progress"

The epic grain method that recomputes epic progress from member state on a terminal member event SHALL be named `RecomputeProgressAsync` on the `IEpicGrain` contract. The shared core logic SHALL be named `RecomputeProgressInternalAsync`. The terminal-event dispatcher type SHALL be named `EpicProgressRecomputeDispatcher`. The cancelled-event handler SHALL NOT carry the word "reconcile" in its type name and SHALL be style-aligned with `EpicAutoDoneHandler`. All XML doc comments and `<see cref>` references across the call path SHALL describe the behavior as "recompute progress" and SHALL NOT use the word "reconcile" for this epic-domain action. The workflow-scheduling domain's separate `DispatchService` reconcile is a distinct mechanism and SHALL NOT be conflated with this rename.

#### Scenario: Grain contract uses the recompute-progress name

- **WHEN** `IEpicGrain` is inspected
- **THEN** it SHALL expose `RecomputeProgressAsync`
- **AND** it SHALL NOT expose any method whose name contains "Reconcile"

#### Scenario: Shared core logic and dispatcher use the recompute-progress name

- **WHEN** `EpicGrain`, the terminal-event dispatcher, and the cancelled-event handler are inspected
- **THEN** the shared core logic SHALL be named `RecomputeProgressInternalAsync`
- **AND** the dispatcher SHALL be named `EpicProgressRecomputeDispatcher`
- **AND** the cancelled-event handler type name SHALL NOT contain "Reconcile"

#### Scenario: Documentation uses recompute-progress terminology

- **WHEN** XML doc comments and `<see cref>` references across `IEpicGrain`, `EpicGrain`, `EpicAutoDoneHandler`, the renamed cancelled handler, and the dispatcher are inspected
- **THEN** they SHALL describe the behavior as recompute progress
- **AND** they SHALL NOT use "reconcile" to describe this epic-domain action

### Requirement: Recompute progress retains its domain behavior

`RecomputeProgressAsync` SHALL remain the epic's self-driving domain action: it recomputes epic progress from linked-issue member state. It SHALL skip terminal (done/closed) and paused epics without advancing. When all linked issues are complete, it SHALL `MarkDone` the epic and release active memberships. For a `running` epic, it SHALL call `TryStartNext` to advance the next startable issue; an `idle` epic SHALL NOT auto-advance. The method SHALL be idempotent — safe to call repeatedly with the same member state — so durable at-least-once redelivery of a terminal event does not cause a double advance or a double `MarkDone`.

#### Scenario: All linked issues complete marks the epic done

- **WHEN** `RecomputeProgressAsync` runs on an epic whose linked issues are all complete
- **THEN** the epic SHALL be marked done
- **AND** active memberships SHALL be released

#### Scenario: Running epic advances the next startable issue

- **WHEN** `RecomputeProgressAsync` runs on a running epic with a startable linked issue and a free in-progress slot
- **THEN** the next startable issue SHALL be advanced via `TryStartNext`

#### Scenario: Idle epic does not auto-advance

- **WHEN** `RecomputeProgressAsync` runs on an idle epic
- **THEN** the epic SHALL NOT advance to a new issue
- **AND** the epic SHALL remain idle

#### Scenario: Paused and terminal epics are no-ops

- **WHEN** `RecomputeProgressAsync` runs on a paused, done, or closed epic
- **THEN** the epic SHALL NOT change state and SHALL NOT advance

#### Scenario: Idempotent under durable redelivery

- **WHEN** a terminal member event is redelivered and `RecomputeProgressAsync` runs again on an epic that already advanced
- **THEN** the epic SHALL NOT advance a second issue
- **AND** the epic SHALL NOT be marked done twice

### Requirement: Recompute progress is invoked from exactly three call sites

`RecomputeProgressAsync` (grain entry) and `RecomputeProgressInternalAsync` (shared core) SHALL be invoked from exactly three call sites: the completed-event handler (`EpicAutoDoneHandler`) on `com.mohist.issue.completed`, the renamed cancelled-event handler on `com.mohist.issue.cancelled`, and `ResumeAsync`'s post-resume re-evaluation when a paused epic resumes into running. No other call site SHALL invoke the recompute path.

#### Scenario: Completed terminal event triggers recompute

- **WHEN** a `com.mohist.issue.completed` event is delivered
- **THEN** `EpicAutoDoneHandler` SHALL dispatch `RecomputeProgressAsync` on the owning epic

#### Scenario: Cancelled terminal event triggers recompute

- **WHEN** a `com.mohist.issue.cancelled` event is delivered
- **THEN** the renamed cancelled-event handler SHALL dispatch `RecomputeProgressAsync` on the owning epic

#### Scenario: Resume into running triggers recompute

- **WHEN** `ResumeAsync` transitions a paused epic into running
- **THEN** the post-resume re-evaluation SHALL invoke `RecomputeProgressInternalAsync`
- **AND** the epic SHALL advance the next startable issue if one exists

### Requirement: Durable event delivery is the sole trigger for terminal recompute

The poll-driven safety-net sweep for epic terminal recompute SHALL NOT exist. `EpicReconciliationService` (the `BackgroundService`), `EpicReconciliationOptions`, and the `AddHostedService<EpicReconciliationService>()` DI registration SHALL be removed. Durable at-least-once delivery of `com.mohist.issue.completed` and `com.mohist.issue.cancelled` SHALL be the sole trigger for recompute on member terminal events. Removing the sweep SHALL NOT introduce a stuck-epic regression: because delivery is durable, a dead-lettered terminal event remains in the dead-letter queue for operator re-delivery rather than being silently dropped.

#### Scenario: No hosted sweep service is registered

- **WHEN** the server's DI registrations are inspected
- **THEN** no `EpicReconciliationService` hosted service SHALL be registered
- **AND** no `EpicReconciliationOptions` configuration type SHALL exist

#### Scenario: Missed terminal event is not silently dropped

- **WHEN** a `com.mohist.issue.completed` or `com.mohist.issue.cancelled` event cannot be delivered to the epic handler
- **THEN** the durable dispatcher SHALL retry the handler
- **AND** on retry exhaustion the event SHALL be dead-lettered for operator re-delivery
- **AND** the event SHALL NOT be silently absorbed by a periodic sweep

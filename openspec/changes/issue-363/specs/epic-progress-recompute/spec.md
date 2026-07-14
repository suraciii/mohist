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

`RecomputeProgressAsync` SHALL remain the epic's self-driving domain action: it recomputes epic progress from linked-issue member state. It SHALL skip terminal (done/closed) and paused epics without advancing. When all linked issues are complete, it SHALL `MarkDone` the epic and release active memberships. For a `running` epic, it SHALL call `TryStartNext` to advance the next startable issue; an `idle` epic SHALL NOT auto-advance. The method SHALL be idempotent - safe to call repeatedly with the same member state - so durable at-least-once redelivery of a terminal event does not cause a double advance or a double `MarkDone`.

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

### Requirement: Recompute progress retains all three required semantic triggers

The recompute path SHALL retain the completed-event handler (`EpicAutoDoneHandler`) on `com.mohist.issue.completed`, the renamed cancelled-event handler on `com.mohist.issue.cancelled`, and `ResumeAsync`'s post-resume re-evaluation when a paused epic resumes into running. The two handlers SHALL share `EpicProgressRecomputeDispatcher`, which contains their direct call to the `RecomputeProgressAsync` grain entry; `ResumeAsync` SHALL call `RecomputeProgressInternalAsync` directly. This requirement does not forbid an additional event-driven readiness trigger if one is required to preserve progress behavior that the removed sweep currently provides.

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

### Requirement: Link-time recompute preserves sweep readiness behavior

When an issue is linked to a non-terminal epic without waking from done, the epic SHALL recompute progress after the link is committed by calling `RecomputeProgressInternalAsync` with `PreserveRunning` failure mode. This preserves the readiness behavior previously supplied by the poll-driven sweep: a startable issue linked to a running epic SHALL be advanced via `TryStartNext`, and an epic whose members are all complete SHALL be marked done. This trigger is a command-path operation within the epic grain, not a cross-aggregate event->command change. Wake-from-done links SHALL continue to call `TryStartNext` directly.

#### Scenario: Startable issue linked to running epic advances

- **GIVEN** a running epic with a free in-progress slot
- **WHEN** a startable issue is linked to the epic without waking from done
- **THEN** the epic SHALL recompute progress after the link is committed
- **AND** the newly linked startable issue SHALL be advanced via `TryStartNext`

#### Scenario: All-complete membership at link time marks epic done

- **GIVEN** an epic whose linked issues are all complete
- **WHEN** an additional already-complete issue is linked to the epic
- **THEN** the epic SHALL recompute progress after the link is committed
- **AND** the epic SHALL be marked done if all members are complete

#### Scenario: Wake-from-done link retains direct TryStartNext

- **GIVEN** a done epic
- **WHEN** an open issue is linked, waking the epic to running
- **THEN** the epic SHALL call `TryStartNext` directly (not the full recompute path)

### Requirement: Durable event-driven recompute triggers replace every sweep readiness transition

The poll-driven sweep reevaluated readiness transitions beyond missed terminal events. Each transition the sweep covered SHALL have a durable, event-driven recompute trigger so convergence does not depend on a periodic scan:

- **External prerequisite completes**: `EpicAutoDoneHandler` SHALL reverse-look-up epics whose members depend on the completed event issue as an external prerequisite via `EpicQuerier.GetEpicIdsDependentOnPrerequisiteAsync`, and dispatch `RecomputeProgressAsync` to those epics in addition to the direct-member epic. Cancellation only recomputes the direct-member epic because prerequisites require completion.
- **Draft undraft**: `EpicDraftChangedHandler` subscribing `com.mohist.issue.draft-changed` SHALL dispatch `RecomputeProgressAsync` to the owning epic when a member transitions from draft to ready (`NewIsDraft == false`).
- **Prerequisite removed**: `EpicPrerequisiteRemovedHandler` subscribing `com.mohist.issue.prerequisite-removed` SHALL dispatch `RecomputeProgressAsync` to the owning epic.
- **Link commit/recompute crash**: `EpicIssueLinkedHandler` subscribing `com.mohist.epic.issue-linked` SHALL dispatch `RecomputeProgressAsync` to the epic that linked the issue. This is the durable convergence path for a crash between link commit and inline recompute.
- **Command-path start failure**: `TryStartNextAsync` with `PreserveRunning` SHALL emit `EpicStartAttemptFailed` (persisted atomically with the epic state). `EpicStartRetryHandler` subscribing `com.mohist.epic.start-attempt-failed` SHALL re-drive `RecomputeProgressAsync` via the durable dispatcher with backoff, replacing the sweep's running-but-idle recovery loop. Permanent start failures SHALL be dead-lettered by the dispatcher.

Recovery-critical events (`EpicIssueLinked`, `EpicStatusChanged` transitions to running, and `EpicStartAttemptFailed`) SHALL be persisted atomically with the epic state transition via `EventStore.AppendAsync(db, envelope)` into the caller's DbContext, so a crash or failed append between commit and event persistence cannot lose the only convergence signal.

#### Scenario: External prerequisite completes recomputes dependent epic

- **GIVEN** a running epic with a member whose external prerequisite (not an epic member) is not yet complete
- **WHEN** the external prerequisite issue completes and `com.mohist.issue.completed` is delivered
- **THEN** `EpicAutoDoneHandler` SHALL reverse-look-up the dependent epic
- **AND** SHALL dispatch `RecomputeProgressAsync` to the dependent epic
- **AND** the dependent member SHALL be advanced via `TryStartNext` if it is now startable

#### Scenario: Draft undraft recomputes owning epic

- **GIVEN** a running epic with a linked member that was a draft
- **WHEN** the member is undrafted and `com.mohist.issue.draft-changed` with `NewIsDraft == false` is delivered
- **THEN** `EpicDraftChangedHandler` SHALL dispatch `RecomputeProgressAsync` to the owning epic
- **AND** the member SHALL be advanced via `TryStartNext` if it is now startable

#### Scenario: Prerequisite removed recomputes owning epic

- **GIVEN** a running epic with a linked member blocked by a prerequisite
- **WHEN** the prerequisite is removed and `com.mohist.issue.prerequisite-removed` is delivered
- **THEN** `EpicPrerequisiteRemovedHandler` SHALL dispatch `RecomputeProgressAsync` to the owning epic
- **AND** the member SHALL be advanced via `TryStartNext` if it is now startable

#### Scenario: Link event drives durable recompute convergence

- **GIVEN** a running epic with a newly linked open issue
- **WHEN** `com.mohist.epic.issue-linked` is delivered (including after a crash that prevented the inline recompute)
- **THEN** `EpicIssueLinkedHandler` SHALL dispatch `RecomputeProgressAsync` to the epic
- **AND** the epic SHALL converge to the correct progress state

#### Scenario: Command-path start failure triggers durable retry

- **GIVEN** a running epic whose `TryStartNextAsync` catches a transient `StartWorkAsync` failure under `PreserveRunning`
- **THEN** the epic SHALL emit `EpicStartAttemptFailed` persisted atomically with its state
- **WHEN** `com.mohist.epic.start-attempt-failed` is delivered
- **THEN** `EpicStartRetryHandler` SHALL dispatch `RecomputeProgressAsync` to the epic
- **AND** the dispatcher SHALL retry with backoff until success or dead-letter

### Requirement: The poll-driven terminal-recompute sweep is removed

The poll-driven safety-net sweep for epic terminal recompute SHALL NOT exist. `EpicReconciliationService` (the `BackgroundService`), `EpicReconciliationOptions`, and the `AddHostedService<EpicReconciliationService>()` DI registration SHALL be removed. Durable at-least-once delivery of terminal events plus the event-driven readiness triggers specified above SHALL be the reliable automatic triggers for recompute; `ResumeAsync` and link-time recompute remain explicit non-event triggers. The sweep's readiness behavior is fully replaced by the link-time recompute trigger and the durable event-driven triggers (external-prerequisite completion, draft undraft, prerequisite removal, link convergence, start-failure retry). A terminal-event processing failure that escapes the handler remains retryable and, on exhaustion, dead-lettered for operator re-delivery rather than silently dropped.

#### Scenario: No hosted sweep service is registered

- **WHEN** the server's DI registrations are inspected
- **THEN** no `EpicReconciliationService` hosted service SHALL be registered
- **AND** no `EpicReconciliationOptions` configuration type SHALL exist

#### Scenario: Missed terminal event is not silently dropped

- **WHEN** a `com.mohist.issue.completed` or `com.mohist.issue.cancelled` event cannot be delivered to the epic handler
- **THEN** the durable dispatcher SHALL retry the handler
- **AND** on retry exhaustion the event SHALL be dead-lettered for operator re-delivery
- **AND** the event SHALL NOT be silently absorbed by a periodic sweep

#### Scenario: Next-issue start failure is not acknowledged as successful delivery

- **WHEN** terminal-event recompute selects a next issue and `IIssueGrain.StartWorkAsync` fails
- **THEN** the terminal-event processing failure SHALL reach the durable dispatcher
- **AND** the dispatcher SHALL retry and, on exhaustion, dead-letter the handler delivery

#### Scenario: Command-path start failure emits durable retry trigger

- **WHEN** `StartAsync`, a link operation, or `ResumeAsync` attempts to start the next issue and `IIssueGrain.StartWorkAsync` fails under `PreserveRunning`
- **THEN** the epic SHALL emit `EpicStartAttemptFailed` persisted atomically with its state
- **AND** `EpicStartRetryHandler` SHALL re-drive `RecomputeProgressAsync` via the durable dispatcher with backoff
- **AND** on permanent failure the delivery SHALL be dead-lettered

### Requirement: Runner presence loss records a recoverable interruption instead of terminal failure

When the control plane concludes a Runner is lost (presence timeout, unregister, or abnormal restart), closeout for each of that Runner's active works SHALL record a recoverable interruption: a reason code, the affected work identity, and the time of the interruption, in a nonterminal state. Closeout MUST NOT terminal-fail active workflow work as `runner-lost`. Workflow Agent tasks with an unresolved result settlement SHALL retain their unknown settlement and deadline semantics; ordinary tasks and stage checks SHALL be marked recoverable-interrupted, not failed. Retained AgentJob ledgers for the lost Runner SHALL project an explicit recovery state with the recorded reason instead of silently remaining `Running`.

#### Scenario: Presence timeout with an ordinary task running

- **WHEN** a Runner's presence expires while it is executing a workflow task that has no Agent-result settlement
- **THEN** the workflow SHALL record a recoverable interruption for that task's work identity with a reason code
- **AND** the task and the workflow run MUST NOT be recorded as failed

#### Scenario: Presence timeout with an Agent task awaiting its result

- **WHEN** a Runner's presence expires while an Agent-result-settlement task is awaiting its authoritative result
- **THEN** the workflow SHALL preserve the existing unknown settlement with the disconnect reason and its existing settlement deadline
- **AND** no `TaskFailed` or completion SHALL be recorded from the presence loss alone

#### Scenario: AgentJob running on the lost Runner

- **WHEN** a Runner with a running AgentJob ledger is declared lost
- **THEN** the AgentJob SHALL project an explicit recovery state carrying the recorded reason
- **AND** the job MUST NOT be terminal-failed or silently left in `Running` without a recovery projection

### Requirement: Runner-side execution facts survive process death

Work identity and execution facts for in-flight work SHALL be durably persisted by the Runner so they survive Runner process death (OOM kill, abnormal restart). After a Runner process restarts, it SHALL reload those facts and re-declare the surviving works in its poll report under the original work keys, so server-side reconciliation is factual. Recovery MUST NOT rely on re-executing work whose persisted facts already establish execution state or a recorded verdict.

#### Scenario: Runner restarts after an OOM kill

- **WHEN** a Runner process is killed and restarted while works were in flight or awaiting acknowledgement
- **THEN** the new process SHALL reload the persisted execution facts and report the surviving work keys in its first poll
- **AND** the server SHALL reconcile against those facts instead of treating the works as unclaimed

#### Scenario: Work whose verdict was already persisted locally

- **WHEN** recovery encounters a work whose terminal facts (for example a persisted terminal task-log record or journalled operation outcome) were durably recorded before the process died
- **THEN** the Runner SHALL reconcile from those facts rather than execute the work again
- **AND** the owner SHALL receive at most one outcome for that work identity

### Requirement: Reconnect re-attaches or re-delivers under the original identity

When a Runner reconnects (registers or resumes polling) after being lost, each recoverable-interrupted work SHALL re-attach to a surviving execution or be re-delivered from persisted facts under the original work identity and idempotency boundary — the same `ownerKind:ownerId:workId`, the same dispatch payload, and the same report key. Redelivery MUST NOT create a second work item, a second task attempt, or a second outcome for the same identity. A Runner process that still holds a re-delivered work (in flight or awaiting acknowledgement) SHALL skip re-execution and keep its existing entry. A recoverable-interrupted AgentJob SHALL return to execution under its original work id when a Runner claims it again. No work SHALL execute twice as a result of loss and recovery.

#### Scenario: Same Runner reconnects and still holds the work

- **WHEN** the server re-delivers a work the Runner process already holds in its reported set
- **THEN** the Runner SHALL skip re-execution and retain its existing in-flight or awaiting-ack entry
- **AND** no duplicate execution SHALL start

#### Scenario: Restarted Runner receives redelivery

- **WHEN** a restarted Runner polls and the server re-delivers recoverable-interrupted work
- **THEN** the dispatch SHALL carry the original work identity and dispatch payload
- **AND** the Runner SHALL reconcile against its persisted execution facts before executing, re-attaching to a surviving runtime session where the facts support it
- **AND** the owner SHALL observe at most one outcome for that identity

#### Scenario: Recoverable-interrupted AgentJob is reclaimed

- **WHEN** a Runner reconnects or another eligible Runner claims an AgentJob in the recovery state
- **THEN** the job SHALL execute again only under its original work id and existing ledger identity
- **AND** its ledger SHALL return to a running projection without a new work id or a second ledger row

### Requirement: Bounded terminal fallback when no Runner returns

Every recoverable interruption SHALL carry a bounded recovery deadline. If no Runner accepts or reports the affected work before the deadline expires, the system SHALL resolve the work to exactly one clear terminal state that names the recorded interruption reason; it MUST NOT silently drop the work and MUST NOT invent success. Before the deadline, the work SHALL remain nonterminal and user-visible as recoverable-interrupted or recovering. A work that recovers before the deadline SHALL NOT receive the terminal fallback.

#### Scenario: No Runner returns before the deadline

- **WHEN** a recoverable-interrupted work reaches its recovery deadline with no Runner having accepted or reported it
- **THEN** the system SHALL record one terminal state for that work whose reason identifies the interruption and the expired recovery
- **AND** the work SHALL NOT remain silently pending beyond the deadline

#### Scenario: Runner returns before the deadline

- **WHEN** a Runner re-attaches or reports the interrupted work before the recovery deadline expires
- **THEN** the work SHALL resume under its original identity
- **AND** the terminal fallback MUST NOT be applied

### Requirement: Late reports and observations reconcile idempotently

Reports and observations arriving from a previous Runner process generation (or a superseded execution) for a preserved work identity SHALL be reconciled idempotently: accepted at most once, with the first authoritative result winning, or explicitly acknowledged as stale. Both acknowledgement kinds are terminal for Runner retry purposes so the reporting Runner retires its entry. The report path MUST NOT return an untracked or not-found dead end for work whose identity is preserved; a duplicate, stale, or superseded report MUST NOT overwrite an already-authoritative result or produce a second outcome.

#### Scenario: Late result after the work was reassigned or settled

- **WHEN** a report from the previous Runner generation arrives after the work's outcome was already authoritatively recorded
- **THEN** the server SHALL acknowledge the report as stale
- **AND** the recorded outcome MUST remain unchanged with no duplicate result event

#### Scenario: Report for recoverable-interrupted work that has not settled

- **WHEN** a late report from the original execution arrives while the work is still recoverable-interrupted and unsettled
- **THEN** the server SHALL accept it at most once under the preserved identity and resolve the interruption
- **AND** the reporting Runner SHALL receive an acknowledgement that retires its awaiting-ack entry

#### Scenario: Duplicate report delivery

- **WHEN** the same result report is delivered more than once for the same work identity
- **THEN** the owner SHALL apply it idempotently
- **AND** the task history MUST contain no duplicate terminal transition

### Requirement: User-visible recovery status for AgentJobs and workflow work

Status surfaces — Web workflow and run views, the CLI (`mo run`, `mo runner`), and issue attention/inbox projections — SHALL expose the recoverable-interrupted and recovering states with their recorded reason codes. These surfaces MUST NOT render the affected work as a generic failure (notably `runner-lost` or a context-free `session.abort fetch failed`). Consumers MUST be able to distinguish a recoverable interruption (Runner lost, awaiting recovery) from a terminal failure and from a blocked unknown Agent-result settlement.

#### Scenario: Web renders an interrupted workflow task

- **WHEN** a workflow task is recoverable-interrupted because its Runner was lost
- **THEN** the workflow run view SHALL present the task as interrupted with its recorded reason
- **AND** it MUST NOT be presented as a `runner-lost` terminal failure

#### Scenario: CLI shows a recovering AgentJob

- **WHEN** an AgentJob is in the recovery state awaiting a Runner
- **THEN** `mo run` / `mo runner` output SHALL show the job as recovering with its reason code
- **AND** the presentation MUST distinguish it from a failed job

#### Scenario: Issue attention projects an actionable reason

- **WHEN** work attached to an issue becomes recoverable-interrupted
- **THEN** the issue attention/inbox projection SHALL surface the interruption with its actionable reason
- **AND** it MUST NOT surface a context-free failure instead

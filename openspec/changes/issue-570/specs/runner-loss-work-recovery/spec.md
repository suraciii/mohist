## ADDED Requirements

### Requirement: Recoverable interruption marking on runner presence loss

When the server detects runner loss (presence timeout, unregister, or abnormal
disconnection), presence-loss closeout MUST record a recoverable interruption
for every affected active work item consisting of a reason code, the affected
work identity (owner and work id), and a timestamp. Ordinary workflow tasks
and stage checks MUST enter a recoverable-interrupted, non-terminal state and
MUST NOT be terminal-failed as `runner-lost` by this path. Agent
result-settlement work MUST retain its existing unknown/deadline arbitration;
the recoverable-interruption recording MUST NOT bypass or replace that
arbitration.

#### Scenario: Runner is OOM-killed while a workflow task is active

- **WHEN** the runner process running an ordinary workflow task is killed and the server's presence timeout fires
- **THEN** the server records a recoverable interruption with a reason code, the affected work identity, and a timestamp
- **AND** the task is not terminal-failed as `runner-lost` and remains non-terminal awaiting runner return

#### Scenario: Runner is lost while stage checks are running

- **WHEN** the runner assigned to a workflow's running stage checks goes offline abnormally
- **THEN** the checks enter a recoverable-interrupted state carrying the recorded interruption reason
- **AND** the checks are not terminal-failed with a `runner-lost` message

#### Scenario: Agent result-settlement work keeps its arbitration

- **WHEN** the runner is lost while a task with an agent result settlement is awaiting its result
- **THEN** the existing unknown/deadline settlement arbitration still applies, including its blocked outcome at the deadline
- **AND** the presence-loss closeout does not terminal-fail that work as `runner-lost`

#### Scenario: Recoverable interruption resolves on completion

- **WHEN** a work item is in the recoverable-interrupted state and the returning runner delivers a terminal report for the original work identity
- **THEN** the report is accepted under the original identity and the interruption is cleared

### Requirement: Recovering projection for agent jobs whose runner is lost

An AgentJob whose assigned runner is lost MUST project an explicit
recovering state carrying the recorded interruption reason, extending the
existing non-terminal `Unknown` semantics. The job MUST NOT silently remain
`Running` during the outage, and MUST NOT strand in a non-dispatchable
`Unknown` after the report timeout without a recorded reason. The recovering
projection MUST be exposed through the agent job status and launch-observation
surfaces together with the recorded reason.

#### Scenario: AgentJob observes runner loss

- **WHEN** the runner assigned to a Running AgentJob is detected as lost
- **THEN** the job's status surface projects a recovering state carrying the recorded interruption reason
- **AND** the launch-observation surface exposes that recovering state and reason instead of a bare running status

#### Scenario: Report timeout strikes while the runner is away

- **WHEN** an AgentJob's report timeout elapses while its runner is lost
- **THEN** the job enters the recovering projection carrying the recorded reason rather than stranding silently in a non-dispatchable Unknown
- **AND** the recovering job remains recoverable by a returning runner within its bounded deadline

#### Scenario: Recovering job resumes on reconnect

- **WHEN** a runner reconnects while an AgentJob is in the recovering state and the bounded deadline has not elapsed
- **THEN** the job re-attaches or is re-delivered under the original work identity
- **AND** the recovering projection clears when the job settles

### Requirement: Identity-preserving re-attachment and re-delivery on reconnect

On runner reconnect, every work item affected by a recorded recoverable
interruption MUST either re-attach to a surviving execution or be re-delivered
from persisted facts (workflow run state, the AgentJob dispatch ledger, the
runner work-result journal, and runtime bindings) under the original work
identity and its idempotency boundary. Recovery MUST NOT mint a new work
identity for interrupted work, and no work item MUST be physically executed
twice as a result of re-delivery.

#### Scenario: Reconnect with a surviving runtime execution

- **WHEN** a runner reconnects and its persisted runtime bindings show that an interrupted work's execution is still alive
- **THEN** the runner re-attaches to that execution and completes reporting under the original work identity
- **AND** the work is not executed a second time

#### Scenario: Reconnect with a durable unacknowledged result

- **WHEN** a runner restarts holding a completed-but-unacknowledged work-result journal entry for interrupted work
- **THEN** the runner reports the recorded result under the original work identity
- **AND** the server accepts it at most once under the existing acknowledgement contract

#### Scenario: Reconnect with no surviving execution

- **WHEN** a runner reconnects and the persisted facts show no surviving execution and no durable result for interrupted work
- **THEN** the work is re-delivered to a ready runner under the original work identity and idempotency boundary
- **AND** physical execution happens at most once across the loss and recovery

### Requirement: Re-delivery of unresolved agent work targets the recorded runner

A Workflow run with an unresolved agent result settlement (unknown or
blocked) whose settlement task is still `Running` MUST be included in the
desired re-delivery set for the runner identity recorded on that attempt,
once that runner is connected, provided the recorded settlement carries a
full runtime binding. The re-delivered dispatch MUST carry the recorded
binding (runtime and runtime session id) so the runner can reconcile the
work instead of executing it again. The re-delivery MUST NOT be rendered for
any runner identity other than the one recorded on the attempt, and MUST NOT
count against the runner's dispatch slots: recovery probes are not new
executions.

#### Scenario: Runner reconnects with a blocked settlement

- **WHEN** the recorded runner polls while the settlement task is `Running` and the settlement holds a full runtime binding
- **THEN** the poll returns a re-delivered dispatch for the still-Running task under its original work identity carrying the recorded binding
- **AND** the dispatch is re-rendered from persisted workflow facts when the dispatch snapshot was deleted by settlement reconciliation

#### Scenario: Runner already holds the work

- **WHEN** the recorded runner's poll report includes the work key in its reported set
- **THEN** no dispatch is rendered for that work

#### Scenario: Settlement lacks a full runtime binding

- **WHEN** the settlement has no recorded runtime and runtime session id
- **THEN** the work is not re-delivered and the existing settlement arbitration and explicit-stop paths remain the only outcomes

#### Scenario: Late authoritative result resolves the blocked run

- **WHEN** a re-delivered recovery dispatch leads the runner to report an authoritative terminal result under the original attempt identity while the settlement is blocked
- **THEN** the server applies the result to the original task attempt exactly once
- **AND** the settlement is cleared and the run leaves the blocked presentation

### Requirement: Started-fence reconciliation after abnormal restart

A re-delivered dispatch carrying a recorded agent binding MUST NOT submit a
new prompt to the bound runtime session: the work already has a physical
execution under this identity. The runner MUST reconcile the dispatch against
the recorded binding facts:

- For runtime-backed work whose runtime exposes terminal-turn facts, the
  runner adopts the terminal turn — deriving the action result from the
  terminal turn's recorded outcome, persisting it in the work-result journal,
  and reporting it under the original identity — so the normal executor
  pipeline (completion contract, artifacts, worktree, set-vars) runs unchanged
over the adopted facts.
- Otherwise the runner surfaces the definite interruption through the wire
  `unknown` report under the original identity, which the server routes into
  the existing settlement arbitration. A `started` entry alone never supplies
  a terminal result.

The `started` journal fence MUST NOT open: `begin()` never admits a
`started` identity as a fresh execution unless the dispatch itself carries a
recorded recovery binding, in which case the execution is a reconciliation
that never submits a new prompt. Reconciliation MUST be idempotent: repeated
deliveries of the same recovery dispatch produce at most one authoritative
outcome and otherwise repeat the same side-effect-free observation. A
binding-less dispatch that hits a durable `started` entry remains refused,
exactly as before; a valid recovery dispatch MUST produce the recorded
non-terminal observation rather than being silently stranded behind the fence.

#### Scenario: Terminal turn is adopted after a runner restart

- **WHEN** a recovery dispatch for a runtime-backed task probes the bound session and the session's recorded turn is terminal
- **THEN** the runner derives the action result from the terminal turn and reports it under the original identity
- **AND** no second prompt is submitted to the session

#### Scenario: Bound session is gone

- **WHEN** a recovery dispatch probes a bound session that no longer exists
- **THEN** the runner reports `unknown` under the original identity instead of executing the dispatch
- **AND** the work is not executed a second time

#### Scenario: Journal fence survives binding-less admission

- **WHEN** a dispatch without a recovery binding hits a durable `started` journal entry
- **THEN** the runner refuses to execute the dispatch, exactly as before

### Requirement: Bounded terminal fallback when no runner returns

Every recorded recoverable interruption MUST carry a bounded deadline. When no
runner returns and no authoritative outcome arrives before the deadline
elapses, the affected work MUST reach a definite terminal state that carries
the recorded interruption reason. Work MUST NOT remain indefinitely in a
non-terminal, non-dispatchable state after runner loss.

#### Scenario: Runner never returns

- **WHEN** the bounded recovery deadline elapses with no runner return and no authoritative outcome for interrupted work
- **THEN** the workflow task or AgentJob reaches a definite terminal state carrying the recorded interruption reason

#### Scenario: Runner returns before the deadline

- **WHEN** a runner reconnects and reports under the original identity before the bounded recovery deadline elapses
- **THEN** the work settles from the reported outcome and is not terminal-failed by the deadline path

### Requirement: Late-report idempotency across runner process generations

Reports and observations arriving from a previous runner process generation
MUST reconcile idempotently against the preserved work identity: each outcome
is accepted at most once, and every duplicate or superseded delivery is
acknowledged as stale rather than dead-ending the reporter. Duplicate or stale
deliveries MUST NOT produce a second terminal outcome, duplicate workflow
events, or corruption of already-settled work.

#### Scenario: Late report after the work already settled

- **WHEN** a report from the previous runner process generation arrives after the work identity has already settled
- **THEN** the server acknowledges it as stale
- **AND** no second terminal outcome or duplicate workflow event is produced

#### Scenario: Competing reports from two generations

- **WHEN** reports for the same work identity arrive from both the previous runner process and a re-delivery execution
- **THEN** the first authoritative report is accepted and the loser is acknowledged stale
- **AND** the settled outcome remains singular and consistent

#### Scenario: Late observation does not regress state

- **WHEN** a late execution observation from a previous runner process generation arrives after recovery has progressed
- **THEN** the observation is applied at most once or acknowledged stale
- **AND** the recovering work is not rolled back or duplicated by the observation

### Requirement: Status surfaces render recovery states with the recorded reason

Web workflow and agent-session views, the CLI (`mo run` and agent
observation), and issue-attention projections MUST render the
recoverable-interrupted and recovering states together with the recorded
interruption reason, instead of a bare failure or a bare running/unknown
status. `runner-lost` MUST NOT be presented as a terminal failure of active
workflow work.

#### Scenario: Workflow view during runner loss

- **WHEN** a workflow task or stage checks are in the recoverable-interrupted state
- **THEN** the Web workflow view and the CLI render a recoverable-interrupted presentation carrying the recorded reason
- **AND** the issue-attention projection surfaces the interrupted state rather than a terminal failure

#### Scenario: Agent session view during recovery

- **WHEN** an AgentJob is in the recovering projection
- **THEN** the Web agent-session view and the CLI agent observation render the recovering state with the recorded reason
- **AND** the presentation distinguishes it from both a healthy running job and a terminal failure

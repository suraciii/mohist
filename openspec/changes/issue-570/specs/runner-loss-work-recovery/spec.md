## ADDED Requirements

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

### Requirement: A re-delivered started agent work reconciles against recorded facts

A re-delivered dispatch carrying a recorded agent binding MUST NOT submit a
new prompt to the bound runtime session: the work already has a physical
execution under this identity. The runner MUST reconcile the dispatch
against the recorded binding facts:

- For runtime-backed work whose runtime exposes terminal-turn facts, the
  runner adopts the terminal turn — deriving the action result from the
  terminal turn's recorded outcome, persisting it in the work-result
  journal, and reporting it under the original identity — so the normal
  executor pipeline (completion contract, artifacts, worktree, set-vars)
  runs unchanged over the adopted facts.
- Otherwise the runner surfaces the definite interruption through the wire
  `unknown` report under the original identity, which the server routes
  into the existing settlement arbitration.

The `started` journal fence MUST NOT open: `begin()` never admits a
`started` identity as a fresh execution unless the dispatch itself carries a
recorded recovery binding, in which case the execution is a reconciliation
that never submits a new prompt. Reconciliation MUST be idempotent: repeated
deliveries of the same recovery dispatch produce at most one authoritative
outcome and otherwise repeat the same side-effect-free observation.

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

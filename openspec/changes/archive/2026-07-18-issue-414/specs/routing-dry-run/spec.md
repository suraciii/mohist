### Requirement: Read-only replay with no side effects

`mo routing test` SHALL replay events through the routing table without launching any Agent and without creating any AgentJob or AgentSession. The dry-run SHALL NOT consume launch idempotency keys, SHALL NOT record trigger labels, and SHALL NOT alter any table, event, or job state. The command SHALL be safe to run repeatedly.

#### Scenario: Dry-run creates no jobs

- **WHEN** an operator runs `mo routing test` against a project whose table would match several recent events
- **THEN** no Agent SHALL be launched
- **AND** no AgentJob or AgentSession SHALL be created
- **AND** subsequent real dispatch of those same events SHALL still produce jobs as if the dry-run had not run

### Requirement: Project-scoped recent-event selection

`mo routing test` SHALL replay only events stamped with the selected project's id. `--last <N>` SHALL select the most recent N project events. Events stamped with a different project id, and events carrying no project id, SHALL NOT be replayed. When `--last` is omitted, the command SHALL replay a fixed nonzero default number of the most recent project events rather than none.

#### Scenario: Only project events are replayed

- **WHEN** an operator runs `mo routing test --last 5` against project P and the store contains events from project P, events from project Q, and unprojected events
- **THEN** the dry-run SHALL replay only the 5 most recent events stamped with project P
- **AND** SHALL NOT replay events from project Q or unprojected events

#### Scenario: Omitting --last still replays events

- **WHEN** an operator runs `mo routing test` without `--last` against a project that has recent project events
- **THEN** the command SHALL replay a fixed nonzero default number of the most recent project events

### Requirement: Per-event evaluation trace

For each replayed event, the dry-run SHALL output a trace that shows, for that event: the rules compared in order, which rule or rules matched, whether evaluation continued after each match, and which Agent each matched rule would trigger. The trace SHALL reflect the same hit, continue, and stop decisions that real dispatch would make.

#### Scenario: Trace shows hit, continue, and would-trigger

- **WHEN** a replayed event matches a `continue` rule R1 whose Agent is A, and then matches a non-`continue` rule R2 whose Agent is B
- **THEN** the trace for that event SHALL show R1 matched and evaluation continued
- **AND** SHALL show R2 matched and evaluation stopped
- **AND** SHALL name A as R1's would-trigger Agent and B as R2's would-trigger Agent

#### Scenario: Trace shows a compared non-match

- **WHEN** a replayed event is compared against rule R0 whose expression does not match
- **THEN** the trace SHALL show R0 was compared and did not match before any subsequent rule

### Requirement: Conclusions match real dispatch

For the same project table state and the same event sequence, the dry-run's per-event match, continue, stop, and would-trigger conclusions SHALL be identical to those produced by real dispatch. The dry-run SHALL use the same evaluator as real dispatch.

#### Scenario: Same result as a real dispatch

- **WHEN** an event sequence is replayed through dry-run and also delivered through real dispatch against identical table state
- **THEN** the set of would-trigger Agent and rule pairs reported by dry-run SHALL equal the set of AgentJobs created by real dispatch

### Requirement: Explicit empty-state messaging

When the dry-run has nothing to replay or nothing to evaluate against, the command SHALL print a clear, human-readable message identifying the cause. The command SHALL NOT print silent empty output.

#### Scenario: No rules in the project

- **WHEN** an operator runs `mo routing test` against a project whose routing table has no active rules
- **THEN** the command SHALL print a message stating that no rules are configured
- **AND** SHALL NOT launch or create anything

#### Scenario: No replayable events

- **WHEN** an operator runs `mo routing test` against a project that has active rules but no recent events stamped with that project's id
- **THEN** the command SHALL print a message stating that no events are available to replay
- **AND** SHALL NOT launch or create anything

### Requirement: Project resolution

`mo routing test` SHALL resolve the project from the active project or from `--project` / `--project-id`. When no project can be resolved, the command SHALL fail locally without contacting the server.

#### Scenario: No project resolves to a local failure

- **WHEN** an operator runs `mo routing test` and no active project is set and no `--project` / `--project-id` is supplied
- **THEN** the command SHALL fail without contacting the server
- **AND** SHALL report that no project was selected

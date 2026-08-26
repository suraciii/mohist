### Requirement: Report arbitration returns one explicit verdict

For every authenticated, structurally valid Workflow or AgentJob terminal report that reaches owner arbitration, the Server MUST return exactly one verdict: `accepted`, `refused`, or `outstanding`. The owner MUST NOT express an arbitration decision through an interpretable HTTP error status, a boolean tracking flag, or a free-form reason that the Runner must classify.

#### Scenario: A valid report reaches its owner

- **WHEN** the Server can route a structurally valid report to its Workflow or AgentJob owner
- **THEN** the response SHALL contain exactly one of `accepted`, `refused`, or `outstanding`
- **AND** the Runner MUST NOT need to interpret an HTTP status or reason string to decide whether to retire the report

#### Scenario: The owner cannot decide the report now

- **WHEN** owner arbitration cannot durably determine acceptance or permanent refusal
- **THEN** the Server SHALL return `outstanding` rather than an arbitration-specific transport error

### Requirement: Accepted means the reported fact is durably recorded

The Server SHALL return `accepted` only after the report's terminal execution fact has been durably applied to the matching owner, work, attempt, Runner, and execution binding. The Runner MUST retire an `accepted` report. Replaying the same report after response loss MUST return `accepted` without duplicating owner transitions, events, artifacts, output, follow-up work, or Workflow advancement.

#### Scenario: A matching report commits

- **WHEN** a terminal report matches the active owner, work, attempt, Runner, and execution binding and the owner durably commits its result
- **THEN** the Server SHALL return `accepted`
- **AND** the Runner SHALL retire the report

#### Scenario: The accepted response is lost

- **WHEN** the owner commits a report but the `accepted` response is lost before the Runner receives it
- **THEN** replay of the same terminal result for the same report identity SHALL return `accepted`
- **AND** the replay MUST NOT apply any result side effect more than once

### Requirement: Refused means the report can never be applied

The Server SHALL return `refused` when the named work no longer exists in a reportable state, including work that is missing, terminal through another result, superseded, stopped, closed out for Runner loss, or rejected by an immutable identity fence. A refused report MUST have no result side effects, and the Runner MUST retire it rather than retrying.

#### Scenario: A late report follows generation closeout

- **WHEN** older-generation work has already been closed out as `FAILED("runner-lost")` and a terminal report later arrives for that work identity
- **THEN** the Server SHALL return `refused`
- **AND** the late report MUST NOT change the failure, bind artifacts, append follow-up work, or advance the owner

#### Scenario: A report names terminal or superseded work

- **WHEN** a report names work that is already terminal through a different result or whose attempt identity has been superseded
- **THEN** the Server SHALL return `refused`
- **AND** the Runner SHALL retire the report

#### Scenario: A refused report is delivered again

- **WHEN** the same permanently inapplicable report is replayed
- **THEN** the Server SHALL return `refused` again without side effects

### Requirement: Outstanding reports remain retryable

The Server SHALL return `outstanding` whenever the report has not been durably accepted and has not been proven permanently inapplicable. The Runner MUST retain and retry every report whose explicit verdict is `outstanding`. The Runner MUST also retain a report when no valid verdict is received because of timeout, connection failure, response loss, malformed response, or an unknown verdict value.

#### Scenario: Durable settlement is temporarily unavailable

- **WHEN** a valid report cannot currently be committed and permanent refusal has not been established
- **THEN** the Server SHALL return `outstanding`
- **AND** the Runner MUST retain the report for retry

#### Scenario: The report request has no decidable response

- **WHEN** a report attempt times out, loses its connection, receives an unreadable response, or receives a value other than `accepted`, `refused`, or `outstanding`
- **THEN** the Runner MUST keep the report outstanding and retry it while the process remains alive

#### Scenario: An outstanding report later becomes decidable

- **WHEN** a retained report is retried after the owner can durably accept it or prove it permanently inapplicable
- **THEN** the Server SHALL return `accepted` or `refused` respectively
- **AND** the Runner SHALL retire the report only after receiving that terminal verdict

### Requirement: Report and closeout races settle once

Concurrent terminal reports and Runner-loss closeout MUST arbitrate through the owning work ledger so exactly one terminal owner transition wins. If the report commits first, generation or presence closeout MUST preserve the accepted result. If closeout commits first, the report MUST be refused. Reconciliation MUST NOT leave the work Running after either terminal transition commits.

#### Scenario: The report commits before generation closeout

- **WHEN** a matching terminal report is durably accepted before replacement-generation closeout reaches the same work
- **THEN** the accepted terminal result SHALL remain authoritative
- **AND** closeout MUST NOT overwrite it with `runner-lost`

#### Scenario: Generation closeout commits before the report

- **WHEN** replacement-generation closeout durably records `FAILED("runner-lost")` before a report reaches the owner
- **THEN** the later report SHALL receive `refused`
- **AND** the work MUST retain exactly one terminal outcome

### Requirement: Verdict semantics are identical for both owner types

Workflow and AgentJob report paths MUST use the same meanings for `accepted`, `refused`, and `outstanding`, and the Runner MUST apply the same retire-or-retry rule regardless of owner type.

#### Scenario: Equivalent Workflow and AgentJob reports are arbitrated

- **WHEN** equivalent report states occur for Workflow-owned and AgentJob-owned work
- **THEN** each owner SHALL select its verdict using the same durable-recorded, permanently-inapplicable, or not-yet-decidable meanings
- **AND** the Runner SHALL retire only `accepted` and `refused` reports for both owner types

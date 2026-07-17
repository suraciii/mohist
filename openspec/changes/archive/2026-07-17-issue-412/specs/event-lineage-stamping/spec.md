### Requirement: Every producer stamps its locally committed business context

Every event production path SHALL stamp the canonical context its producer holds at append time:

- WorkflowRun: `projectid`, `workflowrunid`; optional `issue`, `epic`; structural `stage`.
- Issue: `projectid`, `issue`; optional `epic`.
- Epic: `projectid`, `epic`.
- AgentSession: `projectid`, `sessionid`; optional `agentid`, `issue`, `epic`, `workflowrunid`, `stage`.
- Runner: `runnerid`; optional `projectid`.
- `inbox.item-persisted`: `projectid`, `issue`; inherited optional `epic`, `workflowrunid`, `stage`.

#### Scenario: Workflow event carries its run and Issue context

- **WHEN** a WorkflowRun associated with Project P, Issue 42, and Epic 7 emits an event
- **THEN** the envelope contains `projectid=P`, `workflowrunid`, `issue=42`, and `epic=7`

#### Scenario: Issue event carries canonical identity and affiliation

- **WHEN** Issue 42 with `EpicNumber = 7` emits an event
- **THEN** its envelope contains `projectid`, `issue=42`, and `epic=7`

#### Scenario: Epic event carries canonical identity

- **WHEN** Epic 7 emits an event
- **THEN** its envelope contains `projectid` and `epic=7`

### Requirement: Stage is derived from event structure

Any Workflow domain event variant that structurally carries Stage SHALL stamp `stage`, including
feedback-requested. A variant without Stage SHALL omit it regardless of the event type prefix.

#### Scenario: Stage-bearing events stamp stage

- **WHEN** stage, task, check, approval, or feedback-requested events are appended
- **THEN** each envelope's `stage` equals the Stage value on the domain event

#### Scenario: Non-stage event omits stage

- **WHEN** a Workflow artifact or run-lifecycle event with no Stage member is appended
- **THEN** the envelope has no `stage` extension

### Requirement: Missing optional context is omitted

An absent affiliation or origin SHALL result in no corresponding extension key. Producers SHALL NOT
stamp null, empty string, or placeholder context.

#### Scenario: Issue has no Epic

- **WHEN** an unaffiliated Issue emits an event
- **THEN** the envelope contains no `epic` key

#### Scenario: Session has no Workflow origin

- **WHEN** an AgentSession has no Issue or WorkflowRun origin metadata
- **THEN** its envelope omits `issue`, `epic`, `workflowrunid`, and `stage`

### Requirement: Stamping never queries another aggregate

Envelope extensions SHALL be derived only from the producing aggregate's state, the emitted domain
event, or metadata already attached to that producer. Event append SHALL NOT load or lock Issue,
Epic, WorkflowRun, or another aggregate to gather context.

#### Scenario: Workflow append occurs during delayed affiliation propagation

- **WHEN** WorkflowRun emits before it has applied a newer Issue context
- **THEN** it stamps the context currently committed in WorkflowRun
- **AND** it does not query Issue to fabricate globally atomic affiliation

### Requirement: Historical lineage is immutable

Context SHALL be a producer-local production-time snapshot. Later affiliation changes SHALL NOT
rewrite historical envelopes.

#### Scenario: Issue moves to another Epic

- **WHEN** Issue events were emitted under Epic 7 and the Issue later moves to Epic 9
- **THEN** old envelopes retain `epic=7`
- **AND** events after the local Issue/Workflow context update carry `epic=9`

### Requirement: Issue and Epic have no legacy lineage aliases

New producers and current consumers SHALL use `issue` and `epic`. They SHALL NOT produce or depend
on `issueid`, `epicid`, `issueno`, or `epicno`. Server routing and Web timeline selection SHALL prefer
envelope context over payload identity; payload SHALL remain display data.

#### Scenario: Payload and envelope disagree

- **GIVEN** payload display data names Issue 42 but envelope context contains `issue=99`
- **WHEN** a timeline route is selected
- **THEN** the event routes to Issue 99 from the envelope
- **AND** the original payload remains unchanged for display/audit

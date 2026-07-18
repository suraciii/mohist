### Requirement: Project-scoped table entry

A CloudEvent SHALL enter a project's routing table only when its envelope carries a project id. An event whose envelope carries no project id SHALL NOT enter any routing table. An event SHALL be evaluated only against the table of the project stamped on its envelope.

#### Scenario: Event without project id is skipped

- **WHEN** a CloudEvent whose envelope carries no project id occurs
- **THEN** no project's routing table SHALL evaluate it
- **AND** no Agent SHALL be launched for it

#### Scenario: Event with project id enters that project's table

- **WHEN** a CloudEvent stamped with project id P occurs
- **THEN** the event SHALL be evaluated against project P's active rules
- **AND** SHALL NOT be evaluated against any other project's table

### Requirement: Ordered, first-match-stops evaluation

For an event entering a table, the active rules SHALL be evaluated in ascending Position order. A rule whose expression matches the envelope SHALL trigger its response Agent. When the matched rule's `continue` flag is `false`, evaluation SHALL stop immediately after that match and no lower-positioned rule SHALL be evaluated for that event. A rule whose expression does not match SHALL NOT stop evaluation.

#### Scenario: Targeted rule above fallback wins

- **WHEN** a table contains rule R1 at position 1 matching `event.type == "com.mohist.workflow.stage.approval-requested" && event.issue == "42"` and rule R2 at position 2 matching `event.type == "com.mohist.workflow.stage.approval-requested"`, and an approval event for issue 42 occurs
- **THEN** R1 SHALL match and its Agent SHALL be launched
- **AND** evaluation SHALL stop before R2
- **AND** R2's Agent SHALL NOT be launched for that event

#### Scenario: Non-matching issue falls through to fallback

- **WHEN** the same table receives an approval event for issue 99
- **THEN** R1 SHALL NOT match
- **AND** R2 SHALL match and its Agent SHALL be launched

#### Scenario: Non-match does not stop evaluation

- **WHEN** a table contains three rules in order and the first two do not match an event
- **THEN** evaluation SHALL continue to the third rule

### Requirement: Continue flag enables fanout

A matched rule whose `continue` flag is `true` SHALL trigger its response Agent AND SHALL allow evaluation to proceed to the next lower-positioned rule. Multiple matched rules marked `continue` SHALL each trigger their respective Agents in Position order. The first matched rule whose `continue` flag is `false` SHALL stop evaluation.

#### Scenario: Multiple continue rules each trigger

- **WHEN** a table contains rule R1 (`continue`), rule R2 (`continue`), and rule R3 (not `continue`), all matching an event in that order
- **THEN** R1's Agent SHALL be launched
- **AND** R2's Agent SHALL be launched
- **AND** R3's Agent SHALL be launched
- **AND** evaluation SHALL stop after R3

#### Scenario: Non-continue rule stops further fanout

- **WHEN** a matched rule whose `continue` flag is `false` is reached
- **THEN** its Agent SHALL be launched
- **AND** no lower-positioned rule SHALL be evaluated for that event

### Requirement: Hit-but-not-executable rules are logged non-matches

A rule that matches the envelope but cannot execute SHALL be treated as a non-match and SHALL NOT stop evaluation. A rule is not executable when its referenced Agent is no longer `active` at dispatch time, or when its rendered response prompt is empty. For each such rule the system SHALL emit a structured log record naming the rule and the cause, and evaluation SHALL proceed to the next rule as if the rule had not matched.

#### Scenario: Agent archived after save skips the rule

- **WHEN** a rule's Agent was `active` at save time but has since been archived, and the rule matches an event
- **THEN** the rule SHALL be treated as a non-match
- **AND** a structured log record naming the rule and the Agent SHALL be emitted
- **AND** evaluation SHALL continue to the next rule

#### Scenario: Rendered-empty prompt skips the rule

- **WHEN** a rule matches an event but its rendered response prompt is empty
- **THEN** the rule SHALL be treated as a non-match
- **AND** a structured log record naming the rule SHALL be emitted
- **AND** evaluation SHALL continue to the next rule

### Requirement: Runtime expression errors are non-matches

When a rule's match expression raises a runtime error during evaluation, the rule SHALL be treated as a non-match, a structured log record SHALL be emitted, and evaluation SHALL continue to the next rule. Table evaluation SHALL NOT abort on a runtime expression error.

#### Scenario: Runtime error does not abort the table

- **WHEN** a rule whose expression raises a runtime error, such as a regex match exceeding its bounded timeout, is evaluated against an event
- **THEN** that rule SHALL be treated as a non-match
- **AND** evaluation SHALL continue to any subsequent rules

### Requirement: Envelope-only matching and rendering

Matching and response-prompt rendering SHALL read only the CloudEvent envelope, that is the core fields and the context extensions. The dispatch path SHALL NOT reverse-query any business domain aggregate (Issue, Epic, Workflow, Session) to resolve a match or render a prompt.

#### Scenario: No domain reverse-query during dispatch

- **WHEN** an event is dispatched through the routing table
- **THEN** match decisions and prompt rendering SHALL be computed solely from the envelope
- **AND** SHALL NOT issue reads against Issue, Epic, Workflow, or Session aggregates

### Requirement: Response prompt rendering

The response prompt template SHALL be rendered by substituting `{{event.<attr>}}` placeholders from the same envelope attribute namespace as the match expression (core fields and context extensions). A placeholder for an attribute that is present on the envelope SHALL be replaced with that attribute's value, including when the value is empty. A placeholder for an attribute that is absent from the envelope SHALL be left verbatim. The legacy tokens `{{workflow_run_id}}`, `{{stage}}`, and `{{event_type}}` SHALL be substituted as aliases of `{{event.workflowrunid}}`, `{{event.stage}}`, and `{{event.type}}` respectively.

#### Scenario: Event placeholder is substituted

- **WHEN** a rule's prompt is `Approve issue {{event.issue}} on stage {{event.stage}}` and an event carries `issue = "42"` and `stage = "plan"`
- **THEN** the rendered prompt SHALL contain `Approve issue 42 on stage plan`

#### Scenario: Absent-attribute placeholder left verbatim

- **WHEN** a rule's prompt is `Epic {{event.epic}} review` and the event envelope carries no `epic` attribute
- **THEN** the rendered prompt SHALL contain `Epic {{event.epic}} review`

#### Scenario: Legacy token still works

- **WHEN** a rule's prompt uses `{{event_type}}` and the event type is `com.mohist.workflow.run.failed`
- **THEN** the rendered prompt SHALL contain `com.mohist.workflow.run.failed`

### Requirement: Shared evaluator between dispatch and dry-run

Real dispatch and the read-only dry-run SHALL evaluate a given event against a given table using the same evaluation path. For the same project table state and the same event sequence, the set of rules that match, the continue and stop decisions, and the Agents that are or would be triggered SHALL be identical between real dispatch and dry-run.

#### Scenario: Dispatch and dry-run agree

- **WHEN** the same event is processed once through real dispatch and once through dry-run against the same table state
- **THEN** the sequence of matched rules and triggered or would-trigger Agents SHALL be identical

### Requirement: Idempotent launch keyed by event and rule

Each hit SHALL launch its response Agent through an idempotent launch keyed by the project id, the event id, and the rule id. Repeated dispatch of the same event against the same rule SHALL NOT create a duplicate AgentJob or AgentSession. A different rule hitting the same event SHALL create its own AgentJob, and the same rule hitting a different event SHALL create its own AgentJob.

#### Scenario: Same event and rule is not duplicated

- **WHEN** the same event is dispatched twice against the same rule and both evaluate to a hit
- **THEN** exactly one AgentJob SHALL be created for that event-rule pair

#### Scenario: Same event, different rules each launch

- **WHEN** a single event matches two distinct rules and the first is marked `continue`
- **THEN** two distinct AgentJobs SHALL be created, one per rule

### Requirement: Bidirectional event-rule-AgentJob visibility

For every hit, the resulting AgentSession SHALL carry trigger labels recording the triggering event id and the triggering rule id. The system SHALL support lookup from an event to the rules and AgentJobs it triggered, and from an AgentJob to the event and rule that triggered it.

#### Scenario: Trigger labels are recorded on hit

- **WHEN** a rule matches an event and launches an Agent
- **THEN** the resulting AgentSession SHALL carry a trigger label naming the event id
- **AND** SHALL carry a trigger label naming the rule id

#### Scenario: Event-to-jobs and job-to-event lookup

- **WHEN** an operator queries by the triggering event id
- **THEN** the AgentJobs triggered by that event SHALL be enumerable together with their rule ids
- **AND** when querying one of those AgentJobs
- **THEN** the triggering event id and rule id SHALL be retrievable

### Requirement: Legacy priority-based dispatch removed

The prior priority-arbitrated subscription dispatch SHALL NOT exist after this change. The dispatch path SHALL NOT carry a priority field, SHALL NOT perform event-level single-winner arbitration, and SHALL NOT apply a three-field subscription filter. The only event-to-Agent dispatch path SHALL be the ordered routing table.

#### Scenario: No priority arbitration

- **WHEN** an event matches multiple rules in a table
- **THEN** the responders SHALL be determined solely by ordered evaluation and the `continue` flags
- **AND** SHALL NOT be determined by any priority value or arbitration algorithm

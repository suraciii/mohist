### Requirement: Dispatch response assembly must not match concrete stage or action names
The HTTP-layer code that assembles a work-dispatch response SHALL NOT decide whether to attach parent issue context by matching specific stage ids or Action `uses` names. The dispatch response assembly SHALL be free of knowledge about any particular builtin workflow's stage names or Action identifiers.

#### Scenario: Parent context decision does not inspect stage or uses
- **WHEN** the server assembles a work-dispatch response for any workflow task
- **THEN** the assembly logic SHALL NOT compare the task's stage or `uses` value against the literals `plan` or `mohist/opencode`

### Requirement: Parent issue context availability is preserved
Parent issue context SHALL remain available to workflow tasks that consume it. After this change, the set of dispatches that carry parent issue context SHALL be a superset of those that carried it before; no task that previously received parent issue context SHALL stop receiving it.

#### Scenario: Plan-stage opencode task still receives parent context
- **WHEN** a workflow task in the `plan` stage using `mohist/opencode` is dispatched for an issue that has a parent
- **THEN** the dispatch response SHALL include the parent issue context
- **AND** the context SHALL contain the parent issue's title and body

#### Scenario: Non-plan task does not regress
- **WHEN** the attachment strategy attaches parent context unconditionally or by Action input contract
- **THEN** any task that received parent issue context before this change SHALL continue to receive it

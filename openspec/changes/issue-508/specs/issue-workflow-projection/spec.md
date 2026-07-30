### Requirement: Profile descriptive face reuses the WorkflowProfile type

The descriptive face of a Profile — its id, name, description, and definition — SHALL be expressed through the `WorkflowProfile` domain record (or the collection entry built from it), not by a separate interface that duplicates these fields. A Profile's "what it is" is a single source of truth.

#### Scenario: profile description sourced from WorkflowProfile

- **WHEN** an Issue or read path needs the id, name, description, or definition of a Profile
- **THEN** those values SHALL come from the `WorkflowProfile` record, and SHALL NOT be independently re-declared on a projection interface

### Requirement: Workflow-state projection is an independent concern

Projecting an Issue's workflow state (runtime status, attention, blocked reason, stage approval, change directory, completed flag) from the Issue status and the WorkflowRun status view is a distinct concern from describing a Profile. The projection SHALL live behind its own abstraction and MUST NOT be coupled to the Profile descriptive face. The two concerns have no common reason to change.

#### Scenario: projection does not depend on profile selection

- **WHEN** the workflow-state projection is computed for an issue with a given status and workflow status view
- **THEN** the projected runtime status, attention, blocked reason, and completed flag SHALL be a function of the issue status and workflow status view alone, independent of which Profile is selected

#### Scenario: projection is not on the profile interface

- **WHEN** the descriptive Profile type is inspected
- **THEN** it MUST NOT carry a workflow-state projection method; projection is reached through its own abstraction

### Requirement: Runtime status derivation

The projected runtime status SHALL be derived from the issue status, any workflow attention, and the workflow status. A done issue yields `done`; a cancelled issue yields `cancelled`; a blocked or failed attention yields `blocked`; another non-blocked attention yields `attention`; otherwise the workflow status maps to `queued` (running, unassigned), `paused`, `blocked` (failed), or `active`.

#### Scenario: done issue projects to done

- **WHEN** an issue is done, regardless of workflow status
- **THEN** the projected runtime status SHALL be `done`

#### Scenario: awaiting-approval projects to attention

- **WHEN** an in-progress issue has a workflow status of `awaiting-approval`
- **THEN** the projected runtime status SHALL be `attention` and the attention SHALL indicate review required

#### Scenario: failed workflow projects to blocked

- **WHEN** an in-progress issue has a workflow status of `failed`
- **THEN** the projected runtime status SHALL be `blocked`, and the blocked reason SHALL carry the workflow failure message

### Requirement: Stage approval and change directory projection

The projection SHALL surface the last stage approval (stage, status, requested/responded timestamps) from the workflow status view, and SHALL derive the change directory from the issue number as `openspec/changes/issue-<number>`. When no workflow exists, the approval SHALL be absent and the completed flag SHALL reflect whether the issue stage is `done`.

#### Scenario: change directory derived from issue number

- **WHEN** the projection is computed for issue number 508
- **THEN** the change directory SHALL be `openspec/changes/issue-508`

#### Scenario: no workflow yields no approval

- **WHEN** the projection is computed with no workflow status view for an in-progress issue
- **THEN** the stage approval SHALL be absent and the completed flag SHALL be false

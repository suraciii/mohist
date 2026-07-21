### Requirement: Task activity names its task subject

Activity entries for task lifecycle events SHALL identify the task they concern in the entry's visible summary. When a task title is available, the summary SHALL use that title; when no title can be resolved, it SHALL use the task's stable identifier rather than an anonymous task label.

#### Scenario: Named task starts

- **WHEN** Activity renders a task-started event whose task title is available
- **THEN** the visible entry summary SHALL identify that task by title
- **AND** it SHALL communicate that the identified task started
- **AND** it SHALL NOT render only the generic label `Task Started`

#### Scenario: Named task completes or fails

- **WHEN** Activity renders a task-completed or task-failed event whose task title is available
- **THEN** the visible entry summary SHALL identify that task by title
- **AND** it SHALL communicate the applicable completion or failure outcome

#### Scenario: Task title cannot be resolved

- **WHEN** Activity renders a task lifecycle event with a task identifier but no resolvable title
- **THEN** the visible entry summary SHALL identify the task by its stable identifier
- **AND** it SHALL NOT collapse to an anonymous repeated task label

### Requirement: Artifact activity names its artifact subject

Activity entries for artifact-recorded events SHALL identify the recorded artifact in the entry's visible summary using its display name or recorded path. The artifact identity SHALL be visible without expanding raw event detail.

#### Scenario: Artifact is recorded

- **WHEN** Activity renders an artifact-recorded event with a recorded path or display name
- **THEN** the visible entry summary SHALL name that artifact
- **AND** it SHALL communicate that the identified artifact was recorded
- **AND** the user SHALL NOT need to expand event detail to discover which artifact the event concerns

### Requirement: New comments record a declared author

Every new issue comment SHALL include a nonblank author label declared by the caller. The comment command SHALL trim, validate, persist, and return that label with the comment. Web and CLI comment submission SHALL require the author and SHALL NOT infer it from the current viewer, request channel, client type, or an unauthenticated role.

#### Scenario: Web user submits a comment

- **WHEN** a user submits a comment with a nonblank author label and body in the issue detail page
- **THEN** the created comment SHALL persist the trimmed author label
- **AND** the returned comment SHALL contain that author
- **AND** the comment row SHALL display the author with its body and creation time

#### Scenario: CLI caller submits a comment

- **WHEN** a caller runs the comment-add command with a nonblank author label and body
- **THEN** the command SHALL send both values to the comment API
- **AND** the created comment SHALL return and display that recorded author in subsequent reads

#### Scenario: New comment omits author

- **WHEN** a new comment request has a missing or blank author
- **THEN** the comment SHALL NOT be created
- **AND** the caller SHALL receive an actionable validation error

### Requirement: Comment rows show recorded or historical attribution

Every issue comment row SHALL display its recorded author alongside its creation time. A historical comment whose persisted author is absent SHALL display the exact fallback `Unknown author`. Attribution SHALL remain visible on desktop and phone-width viewports and SHALL be visually associated with the corresponding comment body.

#### Scenario: Comment has a recorded author

- **WHEN** a comment with a recorded author renders in the Comments section
- **THEN** the comment row SHALL visibly display that author
- **AND** it SHALL display the comment's creation time
- **AND** both values SHALL be associated with the same comment body

#### Scenario: Comment author is unavailable

- **WHEN** a historical comment has no available author identity
- **THEN** the comment row SHALL display `Unknown author`
- **AND** it SHALL NOT leave the attribution area blank or show only a timestamp

#### Scenario: Comment renders at phone width

- **WHEN** a comment renders at a phone-width viewport
- **THEN** the recorded author or `Unknown author` attribution SHALL remain visible without horizontal page scrolling
- **AND** the comment body and timestamp SHALL remain readable without obscuring the attribution

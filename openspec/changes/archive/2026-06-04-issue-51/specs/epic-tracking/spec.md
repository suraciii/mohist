## ADDED Requirements

### Requirement: User-Readable Epic Identification

System SHALL assign each Epic a project-scoped, user-readable number and SHALL use that number as the primary user-facing Epic identifier while preserving ID-based lookup compatibility.

#### Scenario: Create numbered Epic

- **WHEN** a user creates an Epic in a project
- **THEN** the system assigns the Epic the next available Epic number for that project
- **AND** Epic list, detail, and issue primary Epic responses include the assigned number

#### Scenario: Display Epic number

- **WHEN** the Web UI renders an Epic reference in the Epic list, Epic detail page, or issue primary Epic label
- **THEN** it displays the Epic as `#N` using the Epic number
- **AND** it MUST NOT use a truncated UUID as the visible primary label when a number is available

#### Scenario: Resolve Epic by number

- **WHEN** a client requests an Epic by its project-scoped number
- **THEN** the API returns the same Epic detail data as ID-based lookup
- **AND** `GET /api/epics/by-number/{number}` resolves the Epic by number
- **AND** `GET /api/epics/{id}` resolves both UUID Epic IDs and numeric Epic references
- **AND** existing ID-based Epic lookup continues to resolve stored references and existing URLs

### Requirement: Searchable Epic Issue Selection

System SHALL provide searchable issue selection for adding issues to an Epic and SHALL make unavailable candidates understandable before submission.

#### Scenario: Filter Add Issue candidates by search text

- **WHEN** a user types search text in the Add Issue control on an Epic detail page
- **THEN** the candidate list is filtered by matching issue number or title
- **AND** already linked issues are excluded from selectable candidates

#### Scenario: Explain unavailable Add Issue candidates

- **WHEN** an issue candidate is closed, archived, or not startable because prerequisites are unmet
- **THEN** the Add Issue control shows the candidate as unavailable
- **AND** it displays the reason, including the blocking issue number when prerequisites prevent starting
- **AND** the unavailable candidate cannot be submitted for Epic membership

#### Scenario: Prevent empty Add Issue submission

- **WHEN** no issue candidate is selected or no selectable candidate exists
- **THEN** the Add Issue submission action is disabled

### Requirement: Editable Epic Metadata

System SHALL let users edit Epic title, description, and priority without changing issue membership or issue workflow state.

#### Scenario: Update Epic metadata

- **WHEN** a user updates an Epic title, description, or priority
- **THEN** the system persists the changed fields
- **AND** updates the Epic `updatedAt` timestamp
- **AND** preserves Epic status and linked issue membership

#### Scenario: Show updated Epic metadata

- **WHEN** an Epic metadata update succeeds
- **THEN** the Web UI refreshes Epic data and displays the updated title, description, and priority

## MODIFIED Requirements

### Requirement: Epic Domain Model

System SHALL model an Epic as a numbered, named, described, prioritized long-running goal container with `active`, `done`, and `closed` statuses.

#### Scenario: Create active Epic

- **WHEN** a user creates an Epic with title, description, and priority
- **THEN** the system persists the Epic with status `active`
- **AND** the Epic has a project-scoped number for user-facing identification
- **AND** the Epic has timestamps suitable for list and detail display

#### Scenario: Epic is not executable work

- **WHEN** the system stores or reads Epics
- **THEN** Epics are separate from issues
- **AND** Epics do not have workflow stage, run state, worktree, branch, task execution, or check execution fields

### Requirement: Projected Epic Progress

System SHALL project Epic progress from linked issue state at read time rather than storing progress as user-edited data, and SHALL compute delivered work from the issue status field rather than health or stage fields.

#### Scenario: Delivered and total counts

- **WHEN** an Epic is listed or shown
- **THEN** `totalIssueCount` equals the number of linked issues
- **AND** `deliveredCount` equals the number of linked issues whose current status represents delivered work, including `done` and `completed`
- **AND** issue health does not cause an issue to be counted as delivered

#### Scenario: Linked issue projection exposes status, stage, and health

- **WHEN** an Epic response includes linked issues
- **THEN** each linked issue includes its current status, stage, and health in distinct fields
- **AND** the status field contains issue lifecycle status rather than issue health

#### Scenario: Next issue recommendation

- **WHEN** an Epic has linked issues
- **THEN** `nextIssue` is the first blocked issue if any exists
- **AND** otherwise the first active issue if any exists
- **AND** otherwise the first backlog issue if any exists
- **AND** otherwise the response indicates the Epic is ready to mark done

#### Scenario: Empty Epic progress

- **WHEN** an Epic has no linked issues
- **THEN** progress reports zero delivered and zero total
- **AND** no issue workflow data is created or changed

### Requirement: Epic Lifecycle

System SHALL let users explicitly mark an Epic done or close it without automatically completing it from issue progress, and SHALL guard terminal lifecycle actions against unsafe or repeated execution.

#### Scenario: Mark Epic done

- **WHEN** a user marks an Epic done and projected progress indicates the Epic is ready to mark done
- **THEN** only the Epic status changes to `done`
- **AND** linked issues are not modified

#### Scenario: Reject marking Epic done before linked issues are delivered

- **WHEN** a user attempts to mark an Epic done while projected progress indicates remaining undelivered linked issues
- **THEN** the system rejects the operation with a client-visible error
- **AND** the Epic status is unchanged
- **AND** the Web UI disables the Mark Done action and explains how many linked issues remain unfinished

#### Scenario: Close Epic with confirmation

- **WHEN** a user closes an Epic after confirming that linked issue membership will be removed
- **THEN** the Epic status changes to `closed`
- **AND** all linked issue memberships for that Epic are removed
- **AND** issue workflow state, prerequisite data, and worktree data are unchanged

#### Scenario: Prevent repeated terminal actions

- **WHEN** an Epic is already `done` or `closed`
- **THEN** the Web UI shows the terminal status
- **AND** the system does not offer or perform the same terminal action again

#### Scenario: No automatic completion

- **WHEN** all linked issues are delivered
- **THEN** the system does not automatically mark the Epic done
- **AND** the projected next state can indicate ready to mark done

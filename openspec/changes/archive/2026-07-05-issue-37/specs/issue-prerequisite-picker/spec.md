### Requirement: A reusable, searchable, project-scoped issue picker

A single reusable issue-picker component SHALL be introduced and consumed by both the New Issue dialog (`CreateIssueDialog.tsx`) and the backlog `Add Prerequisite` editor (`IssueConfigurationCard.tsx`). The picker SHALL let the user search and select existing issues scoped to the current project. Each candidate choice SHALL display enough context to avoid mistakes: at minimum the issue number, title, and status, plus the repository/project context the candidate belongs to. The picker SHALL retire the numeric-only `Issue #` input in the backlog detail editor.

#### Scenario: Picker is reused in both surfaces

- **WHEN** the New Issue dialog and the backlog prerequisite editor are rendered
- **THEN** both SHALL present the same picker component for selecting prerequisite issues

#### Scenario: Numeric-only editor is replaced in the backlog

- **WHEN** the backlog issue detail `Prerequisites` section is rendered
- **THEN** the bare numeric `Issue #` text input SHALL be replaced by the searchable picker
- **AND** the user SHALL NOT be required to type a raw issue number from memory

### Requirement: Search resolves candidates by number, title, and status

The picker SHALL surface a search input that filters candidate issues by matching the typed term against the issue number and/or title (case-insensitive), and SHALL display each candidate's status so the user can judge whether a prerequisite is still open or already completed. The candidate list SHALL be populated from the current project's issues.

#### Scenario: Typing a number finds the matching issue

- **WHEN** the project contains issue #42 titled "Wire up auth" and the user types `42`
- **THEN** issue #42 SHALL appear as a selectable candidate showing its number, title, and status

#### Scenario: Typing a title fragment finds matching issues

- **WHEN** the project contains "Wire up auth" and "Fix auth timeout" and the user types `auth`
- **THEN** both issues whose titles contain `auth` (case-insensitive) SHALL appear as candidates

#### Scenario: Each candidate shows its status

- **WHEN** the candidate list is rendered
- **THEN** each candidate SHALL display its current status (e.g. backlog, in_progress, done, cancelled) alongside its number and title

### Requirement: The picker excludes invalid choices

The picker SHALL exclude, from the candidate list, choices that cannot validly become prerequisites of the target issue: the target issue itself (self-reference), issues already selected as prerequisites of the target, and issues that do not belong to the current project (cross-project choices). These exclusions SHALL prevent the user from constructing a request the server would reject.

#### Scenario: The current issue is not offered

- **WHEN** the picker is opened for issue #10
- **THEN** issue #10 SHALL NOT appear among the candidates

#### Scenario: Already-selected prerequisites are not re-offered

- **WHEN** issue #5 is already a prerequisite of the target and the picker is reopened
- **THEN** issue #5 SHALL NOT appear among the candidates

#### Scenario: Cross-project issues are not offered

- **WHEN** the picker is scoped to project A and an issue exists only in project B
- **THEN** that issue SHALL NOT appear among the candidates

### Requirement: Selections are presented as removable chips

Selected prerequisites SHALL be presented as removable chips within both the create dialog and the backlog editor. Removing a chip SHALL immediately drop the corresponding selection. In the create dialog, removal updates the pending selection that will be sent on submit; in the backlog editor, removal SHALL invoke the existing `removePrerequisite` client (which calls `DELETE /issues/{number}/prerequisites/{prerequisiteNumber}`) behind the picker.

#### Scenario: Removing a chip in the create dialog drops a pending selection

- **WHEN** the user has selected #5 and #7 in the create dialog and removes the #5 chip
- **THEN** #5 SHALL be removed from the pending selection
- **AND** submitting SHALL send only `[7]`

#### Scenario: Removing a chip in the backlog editor removes the prerequisite

- **WHEN** the user removes the chip for prerequisite #5 on the backlog detail of issue #10
- **THEN** the existing remove-prerequisite endpoint SHALL be invoked for #5
- **AND** #5 SHALL no longer be listed as a prerequisite of #10

### Requirement: Adding a prerequisite from the backlog editor uses the existing add contract

When the user selects a candidate in the backlog editor and confirms the add, the picker SHALL invoke the existing `addPrerequisite` client (which calls `POST /issues/{number}/prerequisites` with `{ prerequisiteNumber }`) behind the picker. No new HTTP add endpoint SHALL be introduced for the backlog editor; the picker is purely a selection UI over the existing single-add contract.

#### Scenario: Selecting a candidate adds it via the existing endpoint

- **WHEN** the user selects candidate #5 in the backlog editor for issue #10 and confirms
- **THEN** a `POST /issues/10/prerequisites` request with body `{ prerequisiteNumber: 5 }` SHALL be issued
- **AND** on success #5 SHALL appear as a prerequisite of #10

#### Scenario: Server-side validation errors are surfaced

- **WHEN** the existing add endpoint returns an error (e.g. the candidate no longer exists or would form a cycle)
- **THEN** the backlog editor SHALL surface that error to the user rather than silently adding the prerequisite

### Requirement: The picker explains incomplete prerequisites and their effect on Start eligibility

The backlog editor SHALL explain, for each prerequisite, whether it is incomplete (not yet delivered) and how it affects the issue's Start eligibility, reusing the existing start-readiness read model (`canStart` / `blocker`) rather than introducing a new readiness model. The user SHALL be able to tell, from the rendered state, why a Start action is currently blocked and which prerequisite is being waited on.

#### Scenario: An incomplete prerequisite is flagged and Start is shown as blocked

- **WHEN** issue #10 has prerequisite #5 and #5 is not yet completed
- **THEN** the editor SHALL indicate that #5 is incomplete
- **AND** the Start affordance / board state for #10 SHALL reflect that it cannot start, using the existing start-readiness model

#### Scenario: A completed prerequisite clears the Start block

- **WHEN** all of #10's prerequisites are completed
- **THEN** the editor SHALL reflect that #10 can start
- **AND** no waiting/blocker indication SHALL be shown for prerequisites

### Requirement: Existing single-add/remove flows are not regressed

Replacing the numeric input with the picker SHALL NOT change the observable add/remove contract or the start-blocking semantics. The same validation errors that the numeric editor surfaced (nonexistent issue, self-reference, circular dependency) SHALL still be surfaced when applicable, and the existing prerequisite chip-removal behavior SHALL continue to work.

#### Scenario: Validation errors still surface after the picker replaces the numeric input

- **WHEN** a user attempts to add a prerequisite that the server rejects (nonexistent, self, or circular)
- **THEN** the editor SHALL surface a readable error message, as the numeric editor did before this change

#### Scenario: Start eligibility continues to use the existing model

- **WHEN** the picker is in use and prerequisites change
- **THEN** Start buttons and board cards SHALL continue to reflect waiting prerequisites via the same `canStart`/`blocker` read model used before this change

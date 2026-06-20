## MODIFIED Requirements

### Requirement: Epic List Web UI

Web UI SHALL list Epics with enough information for users to understand progress and next action quickly. Epics SHALL be grouped by lifecycle status, and a `paused` group SHALL appear as its own section ordered after `active` and before `done`.

#### Scenario: List Epics with progress

- **WHEN** a user opens the Epics page
- **THEN** each Epic shows status, title, priority, delivered/total progress, and backend-provided next issue or ready-to-mark-done state

#### Scenario: Distinguish lifecycle groups

- **WHEN** Epics have active, paused, done, or closed statuses
- **THEN** the list groups or clearly distinguishes those statuses
- **AND** the `paused` group is rendered as a distinct section ordered after `active` and before `done`
- **AND** a paused Epic renders an amber status badge that is visually distinct from the active (green), done (blue), and closed (grey) badges

#### Scenario: Paused Epics are de-emphasized

- **WHEN** an Epic is `paused`
- **THEN** it SHALL NOT receive the visual emphasis used for Epics that need advancing ("该推进")
- **AND** it remains visible in the `paused` section so the user can resume it

### Requirement: Epic Detail Web UI

Web UI SHALL show an Epic detail page centered on goal, progress, next issue, and linked issues. The detail page SHALL offer lifecycle actions including Pause and Resume alongside Edit, Mark Done, and Close.

#### Scenario: View Epic detail

- **WHEN** a user opens an Epic detail page
- **THEN** the page shows description, status, priority, delivered/total progress, next issue, and linked issues with current issue states
- **AND** when the Epic has a pause reason, the page displays that reason

#### Scenario: Add linked issue

- **WHEN** a user adds an existing issue from Epic detail
- **THEN** the issue is linked to the Epic
- **AND** duplicate membership errors are shown clearly

#### Scenario: Remove linked issue

- **WHEN** a user removes a linked issue from Epic detail
- **THEN** the issue disappears from that Epic's linked issue list

#### Scenario: Lifecycle actions

- **WHEN** a user marks an Epic done or closes it from the detail page
- **THEN** the page updates to show the new Epic status

#### Scenario: Pause an active Epic

- **WHEN** a user clicks the `Pause` action on an `active` Epic
- **THEN** a confirmation dialog opens that optionally accepts a pause reason
- **AND** on confirmation the Epic status becomes `paused` and the linked issues are unchanged

#### Scenario: Resume a paused Epic

- **WHEN** an Epic is `paused`
- **THEN** the lifecycle action that was `Pause` becomes `Resume`
- **AND** activating it changes the Epic status back to `active`

#### Scenario: Mark Done blocked while paused

- **WHEN** an Epic is `paused`
- **THEN** the Mark Done action SHALL guide the user to resume first rather than completing the Epic directly

## ADDED Requirements

### Requirement: Epic Detail Topbar Title

The app-shell topbar SHALL display the current Epic's sequential number in the page title when the user is on an Epic detail route, matching the `Issue #<number>` convention. The topbar SHALL NOT render a truncated raw Epic id.

#### Scenario: Title shows Epic number

- **WHEN** a user opens an Epic detail page
- **THEN** the topbar title displays `Epic #<number>` using the Epic's display number (e.g. `Epic #1`)
- **AND** the title does not show a raw id prefix such as `Epic #epic_313`

#### Scenario: Number resolves for id and number routes

- **WHEN** a user reaches an Epic detail page by a path segment that is either the raw Epic id or the Epic number
- **THEN** the topbar resolves and displays the corresponding Epic number in both cases

#### Scenario: Other route titles are unaffected

- **WHEN** a user is on the Epics list page, an issue detail page, or any non-Epic-detail route
- **THEN** the topbar title binding for those routes is unchanged by this requirement

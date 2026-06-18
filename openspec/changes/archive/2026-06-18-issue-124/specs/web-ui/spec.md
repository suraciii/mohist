## ADDED Requirements

### Requirement: Issue Detail contains repository metadata without horizontal overflow

Issue Detail SHALL keep its content within the viewport at common desktop widths and SHALL bound repository metadata (repository name, base branch, and git URL) within its column. A long git URL SHALL wrap, break, or truncate (with a tooltip or copy affordance that reveals the full value) rather than force page-level horizontal scrolling.

#### Scenario: No page-level horizontal scroll with a long git URL at desktop width

- **WHEN** a user opens Issue Detail for an issue whose repository git URL is long (for example `https://github.com/suraciii/mohist.git`) at a common desktop width (approximately 1280px)
- **THEN** the page SHALL NOT produce page-level horizontal scrolling
- **AND** the repository name and git URL SHALL remain contained within the Details column

#### Scenario: Long git URL is reachable without overflowing its column

- **WHEN** Issue Detail renders a repository git URL that is longer than the Details column width
- **THEN** the URL SHALL be contained by wrapping, word or character breaking, or truncation
- **AND** when truncated, a tooltip or copy affordance SHALL expose the full git URL

#### Scenario: Repository name and base branch remain readable

- **WHEN** Issue Detail renders repository metadata that includes a repository name and base branch
- **THEN** the repository name and base branch SHALL render within the Details column without being clipped or pushed off-screen by the git URL

### Requirement: Workflow stage navigation adapts to mobile widths

Issue Detail workflow stage navigation SHALL remain readable and operable at mobile widths. At narrow widths the stage control SHALL use a compact current-stage display or a horizontally scrollable stepper instead of compressing all stage labels into controls whose labels overflow or become unreadable.

#### Scenario: Stage labels stay readable on mobile

- **WHEN** a user views Issue Detail on a mobile viewport (approximately 390px)
- **THEN** stage labels such as Build, Check, Integrate, and Done SHALL remain readable
- **AND** each stage control SHALL remain operable rather than having its label clipped or overflowing its hit area

#### Scenario: Narrow viewport switches stage navigation mode

- **WHEN** the viewport is too narrow to render all five stage labels side by side legibly
- **THEN** the stage navigation SHALL switch to a compact current-stage display or a horizontally scrollable stepper
- **AND** it SHALL NOT render five squeezed labels into unusable widths

### Requirement: Issue Detail sidebar groups panels by user intent

Issue Detail SHALL group the sidebar / right-rail panels by user intent into visually distinct sections: metadata, latest artifacts, runtime/session summary, configuration controls, and workflow actions. Panels SHALL NOT be presented as a single undifferentiated list that mixes metadata, artifacts, configuration, and actions.

#### Scenario: Sidebar renders distinct intent groups

- **WHEN** a user views the Issue Detail right rail
- **THEN** metadata, latest artifacts, runtime/session summary, configuration controls, and workflow actions SHALL each appear as a distinct, visually separated group

#### Scenario: Configuration controls are grouped as configuration

- **WHEN** Issue Detail renders configuration controls such as the default model selector and per-stage model overrides
- **THEN** those controls SHALL be grouped under a configuration group
- **AND** they SHALL NOT be nested inside the workflow actions group

### Requirement: Issue Detail separates inspection links from state-changing actions

Issue Detail SHALL visually distinguish safe inspection links (latest artifacts, session transcripts, changed files, and commits) from state-changing workflow actions (start, stop, force stop, retry, rerun stage, and resume). State-changing actions SHALL be grouped under workflow actions, and safe inspection links SHALL be reachable without being interleaved with the primary mutating-action controls.

#### Scenario: Inspection links are visually distinct from mutating actions

- **WHEN** Issue Detail renders artifact, transcript, changed-files, or commit inspection links
- **THEN** those links SHALL be visually distinct from state-changing workflow actions

#### Scenario: State-changing actions are grouped under workflow actions

- **WHEN** Issue Detail renders state-changing actions
- **THEN** they SHALL be grouped under workflow actions
- **AND** they SHALL NOT be interleaved with safe inspection links in the same control group

### Requirement: Issue Detail icon-only controls are accessible with adequate hit targets

Icon-only controls on Issue Detail SHALL expose an accessible name (via `aria-label` or an equivalent accessible-name mechanism). Primary touch and click targets SHALL meet the project's minimum hit-target baseline for both desktop and mobile.

#### Scenario: Icon-only controls expose accessible names

- **WHEN** Issue Detail renders an icon-only control such as the edit issue button
- **THEN** the control SHALL expose an accessible name
- **AND** the control SHALL not rely on a visible icon alone to convey its purpose to assistive technology

#### Scenario: Primary hit targets meet the local baseline

- **WHEN** Issue Detail renders primary touch or click targets at desktop or mobile widths
- **THEN** each target SHALL meet the project's minimum hit-target baseline

### Requirement: Issue Detail layout has responsive and component test coverage

Issue Detail layout behavior SHALL be covered by responsive or component tests that assert desktop and mobile containment and operability.

#### Scenario: Desktop containment regression is caught

- **WHEN** the Issue Detail component tests run with a long repository git URL at a desktop width
- **THEN** they SHALL assert there is no page-level horizontal overflow and repository metadata stays within its column

#### Scenario: Mobile stage navigation regression is caught

- **WHEN** the Issue Detail component tests run at a mobile width
- **THEN** they SHALL assert workflow stage labels remain readable and operable

#### Scenario: Sidebar grouping and accessibility regression is caught

- **WHEN** the Issue Detail component tests run
- **THEN** they SHALL assert the sidebar renders intent-based groups and that icon-only controls expose accessible names

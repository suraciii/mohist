### Requirement: Epic list presentation groups active epics by actionable state

The Epics list page SHALL present active epics (lifecycle `idle` or `running`) in four presentation groups — `Running`, `Ready to start`, `Waiting / Blocked`, and `Idle / Empty` — derived as a first-match cascade over facts that the list endpoint already returns on `EpicWithProgress.progress`:

1. `Running` — the epic has at least one in-progress linked issue (`progress.activeIssues` is non-empty).
2. `Ready to start` — the epic has a startable next issue, signalled by a non-null `progress.nextIssue`, and no in-progress linked issue.
3. `Waiting / Blocked` — the epic has no in-progress linked issue, no startable next issue (`progress.nextIssue` is null), and a non-null `progress.nextIssueReason`.
4. `Idle / Empty` — none of the above (no in-progress linked issue, no startable next issue, and no `nextIssueReason`).

The grouping is a presentation concern only. It SHALL NOT introduce any new epic domain state, lifecycle transition, auto-advancement selection rule, list-query change, or dependency on the epic detail-enrichment path. Every group decision SHALL be computable from the existing list read model alone.

#### Scenario: Epic with an in-progress linked issue is grouped as Running

- **WHEN** an active epic has `progress.activeIssues` containing at least one in-progress linked issue
- **THEN** the epic SHALL appear in the `Running` presentation group
- **AND** SHALL NOT appear in `Ready to start`, `Waiting / Blocked`, or `Idle / Empty`

#### Scenario: Epic with a startable next issue and no in-progress issue is grouped as Ready to start

- **WHEN** an active epic has no in-progress linked issue
- **AND** `progress.nextIssue` is non-null
- **THEN** the epic SHALL appear in the `Ready to start` presentation group

#### Scenario: Epic whose next issue is not startable is grouped as Waiting / Blocked

- **WHEN** an active epic has no in-progress linked issue
- **AND** `progress.nextIssue` is null
- **AND** `progress.nextIssueReason` is non-null
- **THEN** the epic SHALL appear in the `Waiting / Blocked` presentation group

#### Scenario: Epic with no startable issue and no reason is grouped as Idle / Empty

- **WHEN** an active epic has no in-progress linked issue
- **AND** `progress.nextIssue` is null
- **AND** `progress.nextIssueReason` is null
- **THEN** the epic SHALL appear in the `Idle / Empty` presentation group

#### Scenario: An in-progress issue's waiting reason does not pull the epic into Waiting / Blocked

- **WHEN** an active epic has an in-progress linked issue (so `progress.activeIssues` is non-empty)
- **AND** `progress.nextIssueReason` is non-null (e.g. `Waiting for #N to complete`)
- **THEN** the epic SHALL be placed in the `Running` group because the active-issue check takes precedence in the cascade
- **AND** SHALL NOT be placed in `Waiting / Blocked`

#### Scenario: Grouping relies only on existing list read-model fields

- **WHEN** the list page computes the presentation groups for a project
- **THEN** the computation SHALL rely solely on fields already present on `EpicWithProgress`
- **AND** SHALL NOT invoke the epic detail-enrichment path
- **AND** SHALL NOT alter any epic lifecycle status

### Requirement: Running group is displayed first among active epic groups

The `Running` presentation group SHALL be rendered before `Ready to start`, `Waiting / Blocked`, and `Idle / Empty` so that epics currently executing surface at the top of the list for scanning. The four active presentation groups SHALL appear top-to-bottom in the order `Running`, `Ready to start`, `Waiting / Blocked`, `Idle / Empty`.

#### Scenario: Running epics appear above ready-to-start epics

- **WHEN** the list page renders with at least one epic in `Running` and at least one epic in `Ready to start`
- **THEN** the `Running` group SHALL be rendered above the `Ready to start` group

#### Scenario: Active groups follow the defined priority order

- **WHEN** the list page renders with epics present in all four active groups
- **THEN** the groups SHALL appear top-to-bottom in the order `Running`, `Ready to start`, `Waiting / Blocked`, `Idle / Empty`

### Requirement: Epic card surfaces the current issue and the waiting reason

An epic card in the `Running` group SHALL display the current in-progress linked issue (its number and title) so the user can see what is executing without opening the detail page. An epic card in the `Waiting / Blocked` group SHALL display the `nextIssueReason` text so the user can see why the epic is not advancing. An epic card in the `Idle / Empty` group SHALL display a clear reason: `Ready to mark done` when `progress.readyToMarkDone` is true, or an explicit empty-state message (e.g. `No linked issues`) when the epic has no linked issues.

#### Scenario: Running card shows the in-progress issue

- **WHEN** a card is rendered for an epic in the `Running` group
- **THEN** the card SHALL display the number and title of the current in-progress linked issue

#### Scenario: Waiting or blocked card shows the reason

- **WHEN** a card is rendered for an epic in the `Waiting / Blocked` group
- **THEN** the card SHALL display the `progress.nextIssueReason` text

#### Scenario: Idle or empty card that is ready to mark done shows that reason

- **WHEN** a card is rendered for an epic in the `Idle / Empty` group whose `progress.readyToMarkDone` is true
- **THEN** the card SHALL indicate that the epic is ready to mark done

#### Scenario: Idle or empty card with no linked issues shows an empty-state reason

- **WHEN** a card is rendered for an epic in the `Idle / Empty` group that has no linked issues
- **THEN** the card SHALL display an explicit message indicating there are no linked issues

### Requirement: Manual per-card start is labelled Start next issue and distinguished from Start Epic

The per-card manual start control, which starts a single next issue without transitioning the epic lifecycle, SHALL be labelled `Start next issue` and SHALL NOT be labelled `Start` or `Start Epic`. The control SHALL be offered only for epics that have a startable next issue (i.e. the `Ready to start` group); it SHALL NOT appear on `Running`, `Waiting / Blocked`, or `Idle / Empty` cards. The control's position and visual treatment SHALL NOT compete with the card's primary navigation action (opening the epic detail page) and SHALL reduce the likelihood of a user mistaking it for the epic lifecycle `Start Epic` action.

#### Scenario: Ready-to-start card labels the action Start next issue

- **WHEN** a card is rendered for an epic in the `Ready to start` group
- **THEN** the manual start control SHALL be labelled `Start next issue`
- **AND** SHALL NOT be labelled `Start` or `Start Epic`

#### Scenario: Running card offers no per-card start

- **WHEN** a card is rendered for an epic in the `Running` group
- **THEN** the card SHALL NOT offer a `Start next issue` control

#### Scenario: Waiting or blocked and idle or empty cards offer no per-card start

- **WHEN** a card is rendered for an epic in the `Waiting / Blocked` or `Idle / Empty` group
- **THEN** the card SHALL NOT offer a `Start next issue` control

#### Scenario: Per-card start starts only the next issue, not the epic lifecycle

- **WHEN** a user invokes the `Start next issue` control on a `Ready to start` epic card
- **THEN** the action SHALL start only that next issue
- **AND** SHALL NOT perform an epic lifecycle `Start Epic` transition

### Requirement: Epic list page has no horizontal overflow and keeps card state readable on mobile

The Epics list page SHALL render without horizontal overflow at mobile viewport widths. For epics present across any combination of the `Running`, `Ready to start`, `Waiting / Blocked`, and `Idle / Empty` groups, at viewport widths of 320px, 390px, and 430px, the page SHALL satisfy `documentElement.scrollWidth <= documentElement.clientWidth`. No fixed-width or `min-width` card content (status/priority badges, the progress bar, current-issue/next/reason text, or the `Start next issue` control) SHALL force the page wider than the viewport. A card's key state — the lifecycle status badge and the issue number of the current/next issue — SHALL remain visible (not clipped by the viewport edge); long titles and reason text SHALL wrap within the card width rather than causing horizontal overflow.

#### Scenario: List page does not overflow at mobile widths

- **WHEN** the Epics list page is rendered at viewport widths of 320px, 390px, and 430px
- **AND** epics are present across the `Running`, `Ready to start`, `Waiting / Blocked`, and `Idle / Empty` groups
- **THEN** `documentElement.scrollWidth` SHALL be less than or equal to `documentElement.clientWidth` at every width

#### Scenario: Card status and issue number stay visible on mobile

- **WHEN** an epic card is rendered at a 390px viewport width with a long current-issue, next-issue, or reason string
- **THEN** the lifecycle status badge and the current/next issue number SHALL remain visible
- **AND** long text SHALL wrap within the card width
- **AND** the card SHALL NOT cause `documentElement.scrollWidth` to exceed `documentElement.clientWidth`

### Requirement: Done and Closed groups stay folded while active groups stay expanded by default

The `Done` and `Closed` presentation groups SHALL be collapsed (folded) by default when the list page loads, preserving the existing behavior. The `Running`, `Ready to start`, `Waiting / Blocked`, and `Idle / Empty` active groups SHALL be expanded by default so that actionable epics are visible without the user expanding them.

#### Scenario: Done and Closed are folded on load

- **WHEN** the Epics list page loads with epics in the `Done` and/or `Closed` groups
- **THEN** the `Done` and `Closed` groups SHALL be collapsed
- **AND** their epic cards SHALL NOT be visible until the user expands the group

#### Scenario: Active groups are expanded on load

- **WHEN** the Epics list page loads with epics in the active groups
- **THEN** the `Running`, `Ready to start`, `Waiting / Blocked`, and `Idle / Empty` groups SHALL be expanded by default

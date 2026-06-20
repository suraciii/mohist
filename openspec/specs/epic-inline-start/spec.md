# OpenSpec Capability: epic-inline-start

### Requirement: Epic list card exposes an inline Start action on the startable next issue

The Epic list card "next issue" area SHALL expose a Start action for an Epic's projected next issue when that issue is startable. Startability SHALL be gated solely on the next issue being present (the Epic progress read model only populates `nextIssue` for an issue whose derived readiness reports `CanStart` with no undelivered `Blocker`). The Start action SHALL NOT appear when no startable next issue exists. When the next issue is not startable, the card SHALL instead present the blocker reason supplied by the Epic progress read model (for example "Waiting on #N" or "Still a draft") and SHALL NOT present a Start action.

#### Scenario: Startable next issue shows a Start action on the card

- **WHEN** an Active Epic list card is rendered and the Epic progress read model supplies a `nextIssue`
- **THEN** the card SHALL display a Start action for that next issue
- **AND** the Start action SHALL be invocable without navigating away from the Epic list

#### Scenario: Non-startable next issue shows the blocker reason and no Start action

- **WHEN** an Active Epic list card is rendered and the Epic progress read model supplies a `nextIssueReason` but no `nextIssue`
- **THEN** the card SHALL display that blocker reason in the next-issue area
- **AND** the card SHALL NOT display a Start action

#### Scenario: No next issue and no blocker reason shows no Start action

- **WHEN** an Active Epic list card is rendered and the Epic progress read model supplies neither `nextIssue` nor `nextIssueReason`
- **THEN** the card SHALL NOT display a Start action
- **AND** it SHALL continue to present the existing ready-to-mark-done or empty-state guidance

### Requirement: Epic detail linked issue row exposes an inline Start action only for startable non-terminal issues

The Epic detail page linked issue row SHALL expose a Start action for a linked issue when the linked issue's read model reports `canStart` as true and the issue is not in a terminal or in-flight execution state. The Start action SHALL NOT appear for linked issues that are in progress, done, cancelled, or blocked. The existing navigation affordance on the row and the existing Remove action SHALL remain unchanged by the presence or absence of the Start action.

#### Scenario: Startable non-terminal linked issue shows a Start action

- **WHEN** a linked issue row is rendered for an issue whose `canStart` is true
- **AND** that issue is not in progress, done, cancelled, or blocked
- **THEN** the row SHALL display a Start action for that issue

#### Scenario: In-flight linked issue hides the Start action

- **WHEN** a linked issue row is rendered for an issue whose execution status is in progress
- **THEN** the row SHALL NOT display a Start action

#### Scenario: Terminal or blocked linked issue hides the Start action

- **WHEN** a linked issue row is rendered for an issue that is done, cancelled, or blocked
- **THEN** the row SHALL NOT display a Start action

#### Scenario: Existing navigation and Remove action are unchanged

- **WHEN** a linked issue row is rendered with or without a Start action
- **THEN** the row SHALL continue to offer navigation to the issue and the existing Remove action
- **AND** neither affordance SHALL be altered by this change

### Requirement: Inline start reuses the existing issue start path without new semantics

An inline Start action invoked from an Epic surface SHALL start the issue through the existing issue start endpoint and aggregate (`POST /issues/{n}/start` via `IssueGrain.StartWorkAsync`). The system SHALL NOT introduce a new start endpoint, a new start code path, or a batch start capability for Epic surfaces. Start semantics — including draft refusal, prerequisite enforcement, active-run refusal, and workflow enqueueing — SHALL be unchanged from the existing issue start behavior.

#### Scenario: Inline start calls the existing issue start endpoint

- **WHEN** a user activates an inline Start action from an Epic surface
- **THEN** the system SHALL issue the start through the existing `POST /issues/{n}/start` endpoint
- **AND** it SHALL NOT use an Epic-specific start endpoint

#### Scenario: No batch start is introduced

- **WHEN** an Epic surface offers inline start
- **THEN** each Start action SHALL target exactly one issue
- **AND** the system SHALL NOT offer to start multiple issues in a single action

#### Scenario: Start semantics are unchanged

- **WHEN** an inline Start action is invoked on an issue
- **THEN** draft refusal, prerequisite enforcement, active-run refusal, and workflow enqueueing SHALL behave identically to starting that issue from the issue detail page

### Requirement: Inline start refreshes Epic and issue state and reports failure

On a successful inline start, the system SHALL invalidate the Epic and issue query caches the Epic surface depends on, so the started issue reflects `in_progress` and the Epic progress, next-issue, and current-activity surfaces refresh from the server. On a failed inline start, the system SHALL surface a toast describing the failure and SHALL leave the Epic surface's cached state otherwise intact. The Start action's gating SHALL consume the read model's `canStart` and `blocker` as the source of truth and SHALL NOT recompute start readiness on the client.

#### Scenario: Success invalidates Epic and issue caches

- **WHEN** an inline Start action succeeds
- **THEN** the Epic list, Epic detail, and issue queries the surface depends on SHALL be invalidated and refetched
- **AND** the started issue SHALL appear as `in_progress` in the refreshed Epic surfaces

#### Scenario: Failure surfaces a toast

- **WHEN** an inline Start action fails
- **THEN** the system SHALL display a toast reporting the failure
- **AND** the Epic surface SHALL NOT optimistically advance the issue to `in_progress`

#### Scenario: Gating consumes the read model without client-side recomputation

- **WHEN** an Epic surface decides whether to show or enable a Start action
- **THEN** it SHALL rely on the `canStart` and `blocker` fields supplied by the Epic read model
- **AND** it SHALL NOT recompute start readiness from authored facts on the client
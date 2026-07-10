### Requirement: First-screen control region surfaces the operational reality without scrolling

The issue detail first screen SHALL be an execution control workspace. The control region (the status-header tier at the top of the page, before the content grid) SHALL present, within the area reachable without scrolling: the issue identity (number, priority, title), the adjudicated workflow status summary (reflecting health, stage, and progress), the approval state when relevant, a compact runner/session signal when relevant, and the single primary owner action. These elements SHALL all reside within the control region that precedes the reading-flow and reference-rail tiers.

#### Scenario: Running issue shows identity, stage, progress, and stop as the primary action without scrolling

- **WHEN** an issue is in the build stage with a running workflow and the owner opens the issue detail page
- **THEN** the issue number, priority, and title SHALL be visible in the control region
- **AND** the workflow status summary SHALL reflect the running state
- **AND** the current stage and progress SHALL be visible
- **AND** the primary owner action SHALL be reachable in the control region
- **AND** none of these SHALL require scrolling into the reading-flow or reference-rail tiers

#### Scenario: Approval-required issue shows the adjudicated state and primary action without scrolling

- **WHEN** an issue's workflow is paused awaiting approval and the owner opens the issue detail page
- **THEN** the control region SHALL show the approval-required status summary
- **AND** the approval state and awaiting-approval stage SHALL be visible in the control region
- **AND** the primary owner action SHALL be reachable in the control region without scrolling

#### Scenario: Backlog issue shows identity and situation without fabricating stage or action details

- **WHEN** an issue is in the backlog with no workflow run and the owner opens the issue detail page
- **THEN** the issue identity SHALL be visible in the control region
- **AND** the status summary SHALL reflect the queued/backlog situation
- **AND** no fabricated stage, progress figure, or runtime action SHALL be shown

#### Scenario: Done or archived issue shows the terminal state without active workflow controls

- **WHEN** an issue is done or archived and the owner opens the issue detail page
- **THEN** the control region SHALL reflect the done status summary
- **AND** no start, stop, approve, retry, resume, or rerun action SHALL be offered

### Requirement: Each approval gate is presented as one decision context with the evidence needed to decide

When the workflow is awaiting approval, the control region SHALL present a single approval decision context that contains: what is awaiting approval (the approval stage), the Approve action, the Reject/Send-back action, and the plan/check evidence the owner needs to make the decision. These SHALL NOT be split across separate page regions that require the owner to scroll or navigate to assemble the full picture.

#### Scenario: Approval gate shows what is awaiting, approve, and reject together

- **WHEN** an issue's workflow is paused awaiting approval at a given stage
- **THEN** the control region SHALL show the stage that is awaiting approval
- **AND** an Approve action and a Reject/Send-back action SHALL both be present in the same decision context
- **AND** the owner SHALL NOT need to scroll to a different tier to find both actions

#### Scenario: Send-back feedback form stays within the decision context

- **WHEN** the owner chooses to send back the approval and opens the feedback form
- **THEN** the feedback textarea and submit control SHALL appear within the same decision context as the approval actions
- **AND** the owner SHALL NOT be navigated away from the control region to complete the send-back

### Requirement: Latest plan/check artifacts are discoverable from the operation context

The latest plan/check artifacts SHALL be discoverable from the operation context where the owner makes an approval or recovery decision, so the owner can inspect the evidence behind the decision without scrolling past the workflow view into the reading-flow body. The artifact entry point SHALL be reachable from the control region when an approval or recovery decision is active.

#### Scenario: Artifacts are reachable during an approval decision

- **WHEN** an issue's workflow is awaiting approval and plan/check artifacts exist
- **THEN** the owner SHALL be able to reach or open the latest artifacts from the control region's approval decision context
- **AND** the owner SHALL NOT need to scroll into the reading-flow tier to find the artifacts

#### Scenario: Artifacts are reachable during a recovery decision

- **WHEN** an issue is blocked or failed and the owner needs to inspect artifacts before deciding to retry or rerun
- **THEN** the latest artifacts SHALL be discoverable from the recovery decision context in the control region

### Requirement: Blocked, interrupted, and drift states expose their recovery action on the first screen

When an issue is blocked, interrupted, or experiencing base drift that affects progress, the relevant recovery action (retry, resume, rerun, rebase, or stop) SHALL be reachable from the first screen's control region. The owner SHALL NOT be required to scroll into the reference rail to find the recovery action. Base drift, when it blocks progress, SHALL be promoted to a first-screen recovery entry point rather than remaining collapsed only in the reference rail.

#### Scenario: Blocked issue exposes the recovery action without scrolling into the rail

- **WHEN** an issue's health is blocked and a recovery action (retry, resume, rerun, or stop) is available
- **THEN** the blocked situation SHALL be visible in the control region
- **AND** the applicable recovery action SHALL be reachable in the control region without scrolling into the reference rail

#### Scenario: Interrupted issue exposes resume or rerun on the first screen

- **WHEN** an issue's workflow was interrupted and resume or rerun is available
- **THEN** the interrupted situation SHALL be visible in the control region
- **AND** the resume or rerun action SHALL be reachable from the control region without scrolling

#### Scenario: Base drift that blocks progress becomes a first-screen recovery entry point

- **WHEN** an issue has base drift whose decision requires attention (needs-attention) and rebase is the relevant recovery action
- **THEN** the drift signal and its rebase recovery action SHALL be reachable from the first screen control region
- **AND** the owner SHALL NOT be required to expand a collapsed reference-rail card to discover the drift recovery action

#### Scenario: Reference rail retains the full drift detail

- **WHEN** base drift is present
- **THEN** the reference-rail drift card SHALL remain available for the full drift detail (base SHA, defer reason, conflicts)
- **AND** the first-screen entry point SHALL not replace the rail card but promote its recovery action

### Requirement: A compact runner/session signal is surfaced in the control region when relevant

When a coder session is active or the runner state is relevant to the current decision, a compact runner/session signal SHALL be surfaced in the control region so the owner can see the execution-plane status alongside the decision. The compact signal SHALL not require the owner to scroll to the full sessions panel in the reading-flow body.

#### Scenario: Active session shows a compact signal in the control region

- **WHEN** a workflow run has an active coder session running on the issue
- **THEN** a compact session signal SHALL be visible in the control region
- **AND** the owner SHALL NOT need to scroll to the sessions panel to know a session is active

#### Scenario: Runner unavailable is surfaced in the control region when it gates a decision

- **WHEN** no runner is connected or runner capacity is full and the issue cannot start
- **THEN** the runner-state signal SHALL be visible in the control region so the owner understands why the issue is queued

#### Scenario: Session signal is omitted when no session is active and runner state is not decision-relevant

- **WHEN** the issue has no active session and the runner state does not gate any current decision
- **THEN** the compact session signal SHALL NOT occupy first-screen attention

### Requirement: Invalid or unsafe actions are visually secondary or unavailable

The control region SHALL make invalid or unsafe actions visually secondary or unavailable so the owner's attention rests on the single valid next action. Actions not offered by the backend projection SHALL be disabled or omitted; the primary valid action SHALL carry the strongest visual emphasis.

#### Scenario: Disabled action is visually secondary to the primary valid action

- **WHEN** an issue has a primary valid action (e.g. approve) and secondary actions that are not currently offered by the backend
- **THEN** the primary action SHALL carry the strongest visual emphasis (default variant)
- **AND** the unavailable actions SHALL be disabled or rendered with secondary visual weight

#### Scenario: Stop requires confirmation before the destructive action

- **WHEN** the owner clicks stop and the stop action is available
- **THEN** a confirmation SHALL be presented within the control region before the stop is executed
- **AND** the consequence copy SHALL reflect whether the stop is recoverable

### Requirement: Descriptive and conversational content yields to operational content on the first screen

Description, comments, model selection, and prerequisites SHALL remain fully available but SHALL NOT share the first screen's operational attention. These elements SHALL reside below the control region (in the reading-flow body or reference rail) so that operational content — workflow state, approval decisions, recovery actions, artifacts, and the session signal — reaches the owner first.

#### Scenario: Description and comments appear below the operational control region

- **WHEN** an issue has a long description and comments and the owner opens the page
- **THEN** the description and comments SHALL be present in the reading-flow tier
- **AND** they SHALL follow the operational content in document order
- **AND** they SHALL NOT appear within the first-screen control region

#### Scenario: Model selection and prerequisites do not dominate the first screen

- **WHEN** an issue has a configured model and start prerequisites
- **THEN** the model selection and prerequisites SHALL remain available in the reference rail or below the control region
- **AND** they SHALL NOT appear within the first-screen control region competing with operational attention

### Requirement: Every existing issue lifecycle action and its semantics are preserved

This change SHALL preserve every existing issue lifecycle action (start, approve, reject/send-back, retry, rerun, resume, stop/force-stop, rebase, close, archive, mark-ready) and the gating semantics that control each. No workflow action SHALL be added, removed, or respecified. The action-gating logic (deriveRuntimeDecision and runtime-presentations) SHALL be consumed unchanged.

#### Scenario: All lifecycle actions remain available in their valid contexts

- **WHEN** an issue transitions through running, approval-required, blocked, interrupted, and done states
- **THEN** the same set of actions SHALL be offered in each context as before this change
- **AND** each action's enabled/disabled gating SHALL match the pre-existing backend projection

#### Scenario: No new workflow actions are introduced

- **WHEN** the control region is rendered across all workflow states
- **THEN** no action kind outside the existing set (start, approve, send-back, retry, resume, rerun, stop, inspect) SHALL be offered

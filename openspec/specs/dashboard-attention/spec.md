### Requirement: Attention renders as a full-width Hero directly under the factory headline

The Dashboard SHALL mount the `AttentionHero` widget as a full-width Hero directly below the factory status headline. Attention SHALL NOT render as an equal-weight peer inside the remaining zones grid; it SHALL be the dominant first-screen element so the human bottleneck ("what needs me now") is pushed to the most prominent position. The Hero SHALL occupy the full content width, distinct in width and weight from the `Pulse`, `Productivity`, and `Digest` zones that render beneath it.

#### Scenario: Attention Hero renders full-width under the headline

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the Attention Hero SHALL render directly below the factory status headline
- **AND** the Hero SHALL span the full content width
- **AND** the Hero SHALL NOT be a peer slot within the remaining zones grid

#### Scenario: Attention is the dominant first-screen element after the headline

- **WHEN** the first screen of the Dashboard renders
- **THEN** the Attention Hero SHALL appear as a full-width Hero
- **AND** the `Pulse`, `Productivity`, and `Digest` zones SHALL render beneath the Hero rather than beside it

### Requirement: Attention surfaces the three issue attention types plus the runner-down alert

The Attention Hero SHALL surface issues whose state requires the user's action, derived via the shared attention derivation, across exactly these attention types: **awaiting approval** (issues with `approvalState?.status === 'awaiting'`, labeled "Approval needed"), **blocked / needs action** (issues whose health is blocked, including Integrate-stage blocked issues surfaced as "Integration failed"), and **interrupted** (issues whose health is interrupted, including Integrate-stage interrupted issues surfaced as "Integration failed"). For any given issue, the first matching rule SHALL win so an issue appears at most once. In addition, the Hero SHALL surface a **runner-down alert** when `agentStatus.runnerAvailable === false`.

#### Scenario: Awaiting-approval issue surfaces as an approval item

- **WHEN** an issue has `approvalState?.status === 'awaiting'`
- **THEN** the Attention Hero SHALL surface that issue
- **AND** the item SHALL be labeled "Approval needed"

#### Scenario: Blocked issue surfaces as a needs-action item

- **WHEN** an issue's health is blocked and it is not awaiting approval
- **THEN** the Attention Hero SHALL surface that issue
- **AND** an Integrate-stage blocked issue SHALL be labeled "Integration failed"
- **AND** a non-Integrate blocked issue SHALL be labeled as a needs-action item

#### Scenario: Interrupted issue surfaces as an interrupted item

- **WHEN** an issue's health is interrupted and it is not awaiting approval
- **THEN** the Attention Hero SHALL surface that issue
- **AND** an Integrate-stage interrupted issue SHALL be labeled "Integration failed"
- **AND** a non-Integrate interrupted issue SHALL be labeled "Interrupted"

#### Scenario: Runner-down alert surfaces when the runner is unavailable

- **WHEN** `agentStatus.runnerAvailable === false`
- **THEN** the Attention Hero SHALL surface a runner-down alert entry
- **AND** the alert SHALL display the runner status message

#### Scenario: An issue appears at most once via first-match-wins

- **WHEN** an issue matches more than one attention rule
- **THEN** the Attention Hero SHALL surface the issue under exactly the first matching rule
- **AND** the issue SHALL NOT be duplicated across attention types

### Requirement: Each attention item shows a one-line context using the issue title

Each surfaced attention item SHALL display a one-line context derived directly from the issue `title`, so a user can judge what an item is about without leaving the page. For a non-Integrate blocked item, the item MAY instead use `blockedReason` when present, falling back to the issue `title`; for every other attention type the context SHALL be exactly the issue `title`. The change SHALL NOT fetch an approval output summary or any additional context beyond the issue title (or `blockedReason` fallback).

#### Scenario: Approval item shows the issue title as its context

- **WHEN** an awaiting-approval item renders
- **THEN** the item SHALL display a one-line context equal to the issue `title`

#### Scenario: Blocked item prefers blockedReason and falls back to title

- **WHEN** a non-Integrate blocked item renders and `blockedReason` is present
- **THEN** the item SHALL display the `blockedReason` as its context
- **WHEN** a non-Integrate blocked item renders and `blockedReason` is absent
- **THEN** the item SHALL display the issue `title` as its context

#### Scenario: Context does not fetch additional approval output

- **WHEN** an attention item renders its one-line context
- **THEN** the context SHALL be sourced solely from the issue `title` (or `blockedReason` fallback)
- **AND** the Hero SHALL NOT fetch an approval output summary for the context

### Requirement: Attention items offer inline Approve and Resume actions

The Attention Hero SHALL offer an inline **Approve** action for awaiting-approval items and an inline **Resume** action for the blocked/interrupted (non-approval) items, so a user can act without leaving the Dashboard. Invoking Approve SHALL issue the existing issue-approval mutation for that issue; invoking Resume SHALL issue the existing issue-resume mutation for that issue. Each item SHALL also offer a jump link to the corresponding issue detail view.

#### Scenario: Approval item offers inline Approve

- **WHEN** an awaiting-approval item renders
- **THEN** the item SHALL offer an inline Approve action
- **AND** activating it SHALL issue the approval mutation for that issue number
- **AND** the item SHALL NOT offer a Resume action

#### Scenario: Non-approval item offers inline Resume

- **WHEN** a blocked or interrupted item renders
- **THEN** the item SHALL offer an inline Resume action
- **AND** activating it SHALL issue the resume mutation for that issue number
- **AND** the item SHALL NOT offer an Approve action

#### Scenario: Each item links to its issue detail

- **WHEN** any attention item renders
- **THEN** the item SHALL offer a link to that issue's detail view
- **AND** activating the link SHALL navigate to the issue detail surface reachable elsewhere in the application

### Requirement: Attention Hero renders loading, all-clear, and runner-down states

The Attention Hero SHALL render a loading state while the underlying issue data has not yet resolved (and the runner is not down), an all-clear state when there are no attention items and the runner is available, and the attention list when items or a runner-down condition exist. The loading state SHALL be distinct from the all-clear state so that unresolved data is never shown as "nothing needs attention".

#### Scenario: Loading state shows before data resolves

- **WHEN** the issue data has not resolved and the runner is not down
- **THEN** the Attention Hero SHALL render a loading state
- **AND** it SHALL NOT render an all-clear message

#### Scenario: All-clear state shows when nothing needs attention

- **WHEN** the issue data has resolved with no attention items AND `agentStatus.runnerAvailable !== false`
- **THEN** the Attention Hero SHALL render an all-clear state
- **AND** it SHALL NOT render the loading state

#### Scenario: Attention list shows when items or runner-down exist

- **WHEN** there is at least one attention item OR the runner is down
- **THEN** the Attention Hero SHALL render the attention list (including the runner-down entry when applicable)
- **AND** it SHALL NOT render the all-clear state

### Requirement: Attention Hero derives content exclusively from existing read-only sources

The Attention Hero SHALL derive its content from existing frontend data sources — the active issue query (`useIssues`) and the agent status query (`useAgentStatus`) — with the single exception of the **summary approval-wait metric**, which the Hero SHALL consume from the `approval-waiting-metrics` aggregation endpoint. The only mutations the Hero performs SHALL be the existing issue-approval and issue-resume actions triggered by the inline Approve/Resume controls. No backend API endpoint other than the approval-wait aggregation endpoint SHALL be introduced to support the Hero, and no domain state beyond those existing actions SHALL be added. Every other Hero behavior (attention types, inline actions, one-line contexts) SHALL remain read-only over the existing issue and agent-status sources.

#### Scenario: Only the approval-wait aggregation is added as a new source

- **WHEN** the Attention Hero renders and refreshes its data
- **THEN** the Hero SHALL consume the existing issue and agent-status sources
- **AND** the Hero MAY additionally consume the approval-wait aggregation endpoint solely for the summary approval-wait metric
- **AND** no backend API endpoint other than the approval-wait aggregation endpoint SHALL be added to support the Hero

#### Scenario: Only existing approve/resume actions mutate state

- **WHEN** a user invokes an inline action in the Attention Hero
- **THEN** the only mutations SHALL be the existing issue-approval and issue-resume actions
- **AND** no new write operation against domain state SHALL be introduced

### Requirement: Attention Hero displays the summary approval-wait metric

The Attention Hero SHALL display a summary approval-wait metric (e.g. "your approvals averaged 3.2h") sourced from the `approval-waiting-metrics` aggregation, so the "is the human a bottleneck" signal lives where the user is about to act rather than forcing them to compute it from the full issue list. The displayed statistic SHALL be the aggregate average approval waiting time over the trailing 7-day window, computed server-side; the Hero SHALL NOT compute the metric client-side over the full issue list. The Hero SHALL render a defined empty/zero-sample presentation when the aggregation returns no samples, so the absence of data is distinguishable from a short wait.

#### Scenario: Hero shows the average approval wait from the aggregation

- **WHEN** the Attention Hero renders and the approval-wait aggregation returns at least one completed approval within the trailing 7-day window
- **THEN** the Hero SHALL display the aggregate average approval waiting time
- **AND** the displayed value SHALL be sourced from the approval-wait aggregation endpoint
- **AND** the Hero SHALL NOT compute the metric client-side over the full issue list

#### Scenario: Hero renders a defined empty presentation when there are no samples

- **WHEN** the approval-wait aggregation returns no completed approvals within the trailing 7-day window
- **THEN** the Attention Hero SHALL render a defined empty/zero-sample presentation
- **AND** the empty presentation SHALL be distinguishable from a genuine short wait time

#### Scenario: Summary metric excludes pending approvals

- **WHEN** the Attention Hero displays the summary approval-wait metric
- **THEN** the metric SHALL exclude pending (`awaiting`) approvals from the aggregate, consistent with the `approval-waiting-metrics` capability
- **AND** pending approvals SHALL continue to surface as individual attention items rather than within the summary wait time

### Requirement: Attention All-clear state excludes the stale productivity-preview placeholder

When the Attention Hero renders its All-clear state (no attention items and the runner is available), the state SHALL render only the all-clear message and the `ApprovalWaitSummary`. The All-clear state SHALL NOT render a productivity-preview placeholder block (e.g. a "Productivity preview will appear here once it ships." block) or any other transitional placeholder advertising future Productivity content; Productivity is already rendered as its own zone directly beneath the Hero, so such a placeholder is stale and SHALL be treated as an anti-regression guard.

#### Scenario: All-clear renders message and approval summary only

- **WHEN** the Attention Hero renders the All-clear state with resolved issue data, no attention items, and `agentStatus.runnerAvailable !== false`
- **THEN** the Hero SHALL render the all-clear message and the `ApprovalWaitSummary`
- **AND** the Hero SHALL NOT render a productivity-preview placeholder block

#### Scenario: All-clear does not advertise upcoming Productivity content

- **WHEN** the Attention Hero renders the All-clear state
- **THEN** the state SHALL NOT render any text or block advertising that a Productivity preview will appear in the future
- **AND** the Productivity zone beneath the Hero remains the sole surface for Productivity content

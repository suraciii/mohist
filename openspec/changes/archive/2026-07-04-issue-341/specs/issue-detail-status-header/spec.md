# issue-detail-status-header Specification

## Requirements

### Requirement: Sticky Status Headline

The issue detail page SHALL render a status headline at the top of the page that stays pinned within the scroll viewport while the page body scrolls, so the current runtime situation is glanceable at all times.

#### Scenario: User scrolls the detail page

- **WHEN** the user scrolls the detail page body vertically
- **THEN** the status headline remains pinned at the top edge of the scroll viewport
- **AND** the headline region stays visible instead of scrolling away with the content

### Requirement: Single Glanceable Status Region

The current runtime situation, the current workflow stage, the stage progress, and the current task title SHALL be presented together inside one cohesive status-headline region, rather than scattered across separate scrolling cards of equal weight.

#### Scenario: All status facets are available

- **WHEN** the issue has an adjudicated runtime situation plus a current stage with progress and a running task title
- **THEN** the headline region displays the runtime situation, the stage, the completed/total progress, and the current task title together in one region
- **AND** none of these facets are isolated into independent same-weight cards elsewhere on the page

### Requirement: Adjudicated Runtime Situation

The headline SHALL display the single adjudicated runtime situation produced by `deriveRuntimeDecision` — exactly one of running, queued, approval-required, blocked, failed, or done — and SHALL NOT show multiple competing same-weight runtime status indicators.

#### Scenario: Runtime situation is running

- **WHEN** `deriveRuntimeDecision` resolves the summary to running
- **THEN** the headline reflects the running situation

#### Scenario: Runtime situation is approval-required

- **WHEN** `deriveRuntimeDecision` resolves the summary to approval-required
- **THEN** the headline reflects the approval-required situation

#### Scenario: Runtime situation is done (archived)

- **WHEN** an archived issue resolves to done
- **THEN** the headline reflects the done situation
- **AND** no active workflow controls are offered from the header tier

### Requirement: Current Stage and Progress

When the workflow stage progress projection is available, the headline SHALL display the current workflow stage name and the completed-of-total task progress for that stage.

#### Scenario: Stage progress is reported

- **WHEN** `workflowStageProgress` reports stage plan with two of five tasks completed
- **THEN** the headline shows the plan stage and the two-of-five progress

#### Scenario: No stage progress exists

- **WHEN** the issue has no workflow run or stage progress (for example a backlog issue)
- **THEN** the headline shows the adjudicated runtime situation without fabricating a stage or progress figure

### Requirement: Current Task Title

When a current task title is available, the headline SHALL display it so the reader can see what the workflow is doing right now without scrolling into the reading flow.

#### Scenario: A current task title is available

- **WHEN** the runtime decision or stage progress carries a current task title
- **THEN** the headline displays that current task title

### Requirement: Decision and Action Surface Anchored to the Header Tier

The runtime decision/action surface — the single home for approve, send back, stop, start, retry, resume, and rerun — SHALL be anchored within the status-header tier, above the reading flow, and SHALL NOT be relocated into the reading flow or the reference rail.

#### Scenario: Approval controls live in the header tier

- **WHEN** approval is awaiting
- **THEN** the Approve and Send back controls render within the status-header tier
- **AND** the embedded workflow evidence view does not render its own approval or request-changes controls

#### Scenario: Stop and start live in the header tier

- **WHEN** recovery or timeline projections expose start, stop, retry, resume, or rerun
- **THEN** those actions render within the status-header tier
- **AND** the reference rail does not duplicate them

### Requirement: Identity Block Single-Runtime-Badge Invariant

The title/identity block SHALL carry identity information — issue number, priority, draft/archived state, title, labels, epic, and timestamps — plus at most one runtime badge that reflects the current adjudicated runtime situation. It SHALL NOT render a flat row of multiple same-weight runtime pills.

#### Scenario: Header renders identity plus one runtime badge

- **WHEN** the header renders for a running issue
- **THEN** the identity badge group contains the number, priority, and draft/archived pills as applicable
- **AND** the runtime badge group contains exactly one runtime status pill reflecting the running situation
- **AND** no additional same-weight runtime pills are rendered in the title row

### Requirement: Heaviest Visual Weight

The status headline SHALL carry the heaviest visual weight of the three detail-page tiers, exceeding both the reading flow and the reference rail, so the reader's attention lands on the current runtime situation first.

#### Scenario: Visual hierarchy across the three tiers

- **WHEN** the detail page renders all three tiers
- **THEN** the status headline is visually heavier than the reading flow
- **AND** the reading flow is visually heavier than the reference rail

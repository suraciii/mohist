## ADDED Requirements

### Requirement: Issue detail surfaces PR delivery for mohist/pr issues

The issue detail page SHALL display a "经由 PR #N 合并" indicator with a link to the GitHub PR for issues whose workflow run completed via `mohist/publish-via-pr`. The indicator SHALL be absent for issues completed via `mohist/default`, issues still in progress, and issues that have not reached a delivered state. The indicator SHALL read its data from the existing WorkflowRun publish task-result read model and SHALL NOT introduce a new API surface beyond exposing the PR fields already recorded on the publish task result.

#### Scenario: PR indicator appears for completed mohist/pr issues

- **WHEN** an issue's workflow run completed via `mohist/publish-via-pr` with a merged PR
- **THEN** the issue detail page SHALL render "经由 PR #N 合并" with the GitHub PR number
- **AND** it SHALL render a hyperlink to the GitHub PR URL
- **AND** the indicator SHALL be visible without navigating into an activity dialog or log viewer

#### Scenario: PR indicator is absent for direct-delivery issues

- **WHEN** an issue's workflow run completed via `mohist/publish` direct push
- **THEN** the issue detail page SHALL NOT render a PR delivery indicator
- **AND** it SHALL NOT render a placeholder or empty PR link

#### Scenario: PR indicator is absent for in-progress or undelivered issues

- **WHEN** an issue has not reached a delivered state, or its workflow run has no publish task result with PR metadata
- **THEN** the issue detail page SHALL NOT render a PR delivery indicator
- **AND** missing PR metadata SHALL NOT cause the detail page to render an error state

#### Scenario: Indicator reads PR metadata from the existing task-result model

- **WHEN** the issue detail page renders a PR delivery indicator
- **THEN** the `prNumber` and `prUrl` SHALL come from the publish task's structured output already recorded on the WorkflowRun
- **AND** the web client SHALL NOT call any GitHub HTTP API to resolve the PR

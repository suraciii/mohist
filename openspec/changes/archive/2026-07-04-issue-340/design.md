# Design

## Runtime Decision Source

`deriveRuntimeDecision` is the detail page's single reducer from issue, timeline, recovery, readiness, and runner capacity facts to user-facing runtime state. The reducer owns the summary, rationale, primary action, secondary actions, stop recoverability, and wait reason.

Backlog issues are not treated as running by default. A backlog issue with a readiness or capacity blocker is `queued` with a disabled Start action and the blocker as the disabled reason. A ready backlog issue is also `queued`, but exposes Start as the enabled primary action.

## Action Ownership

`IssueDetailPage` creates one set of mutations through `useIssueDetailMutations` and passes those mutations into `RuntimeDecisionSurface`. The embedded `WorkflowView` is mounted read-only on the detail page so approval, send-back, start, resume, and failure-recovery controls are not duplicated there.

The right-rail Actions card is limited to non-overlapping actions and informational state. It does not render the shared runtime mutation error; runtime action errors appear on `RuntimeDecisionSurface`.

## Stop Semantics

The runtime surface renders one Stop action. On first click it shows consequence copy. On confirmation it dispatches the recoverable stop mutation when recovery allows a resumable stop, otherwise it dispatches the terminal stop mutation.

## Inspect Action

Transcript inspection is not wired as a detail-page runtime action yet. Inspect entries from projections are rendered disabled until a concrete destination is defined, preventing visible no-op buttons.

## Visual Substrate

The primary runtime surface and issue-detail cards use `CardSection` or tokenized card/status classes. Read-only workflow evidence must not introduce duplicate write panels or white status panels on the issue detail page.

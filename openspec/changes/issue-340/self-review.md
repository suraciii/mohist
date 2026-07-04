# Self Review

## Checks

- Issue detail page uses `deriveRuntimeDecision` as the source for the runtime summary and primary action.
- `RuntimeDecisionSurface` owns runtime write controls and receives shared mutations from `IssueDetailPage`.
- `WorkflowView` is read-only when embedded on the issue detail page.
- Ready backlog issues expose enabled Start; blocked backlog issues expose disabled Start with a wait reason.
- Inspect is disabled until transcript navigation is wired.
- OpenSpec proposal, design, task, and delta spec artifacts are present for traceability.

# Review Report

## Result: FAIL

## Repaired Items

_None. The issues found require product behavior changes or acceptance-criteria judgment, so they were not repaired during review._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.tsx
  Evidence: The select presents an explicit `Inherit system default` option at `packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.tsx:105`, but the `onChange` handler only mutates when `value` is truthy at `packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.tsx:97`. Choosing the empty inherit option from an existing project default does nothing and never calls `DELETE /api/projects/{projectRef}/workflow-profile/default-template`, so the visible control offers a clear path that does not satisfy the clear-selection acceptance criterion. The separate Clear button works, but the issue explicitly asks for clearing the selection, and the rendered select option is the selection-based clearing affordance. [disallowed:product-behavior-change]
  SuggestedAction: Treat the empty select value as clearing the project default by calling `clearDefault.mutate()`, or remove/disable the empty option when a default is configured so the only clear affordance is unambiguous. Add a test that changes the select from `mohist/pr` to `''` and asserts a DELETE request plus inherited-system readback.
  Verification: `npm run typecheck -w packages/web` passes; `npm run test:run -w packages/web` passes, but no test covers selecting the empty inherit option.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.tsx
  Evidence: `WorkflowProfileControl` reads the project effective default into `defaultProfileId` at `packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.tsx:47`, but it only writes that value to `data-default-profile` at `packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.tsx:72`. The displayed effective profile and selected option still resolve through `issue.workflowProfileId ?? workflowProfileYaml?.profileId ?? SYSTEM_DEFAULT_ID` at `packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.tsx:26`, so an issue with no per-issue override in a project defaulting to `mohist/pr` still visually displays/selects `mohist/default`. That violates the acceptance criterion that profile-selection surfaces resolve the default from project configuration when present, and the added test at `packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.test.tsx:192` only asserts the hidden data attribute instead of the visible value/select state. [disallowed:product-behavior-change]
  SuggestedAction: Use `defaultProfileId` as the fallback for unset issue/profile-yaml state, ensure the option list contains the project-default id if it is absent from the catalog, and update tests to assert `issue-workflow-profile-value` and the select value are `mohist/pr` when no issue-level selection exists.
  Verification: `npm run typecheck -w packages/web` passes; `npm run test:run -w packages/web` passes, but the tests do not assert the user-visible per-issue default resolution.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/web/src/widgets/kanban-board/ui/IssueCard.tsx
  Evidence: `IssueCard` still displays `issue.workflowProfileId ?? SYSTEM_DEFAULT_WORKFLOW_PROFILE_ID` at `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:245`, so backlog cards may continue showing `mohist/default` even when an unset issue would inherit a project default. This surface was not explicitly named in the issue acceptance criteria, which call out create-issue/profile selection surfaces, but it is an adjacent workflow-profile display that can confuse users after this feature lands.
  SuggestedAction: Consider centralizing project-default display semantics for other workflow-profile badges/cards in a later issue.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None identified._

<promise>FAIL</promise>

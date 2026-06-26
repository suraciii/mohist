# Review Report

## Result: FAIL

## Repaired Items

_None. The issues found require product behavior, public-contract, or acceptance-criteria changes, so they were not repaired during review._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx
  Evidence: The create dialog displays the project effective default in the workflow select via `workflowSelectValue = workflowProfileId ?? defaultProfileId ?? ''` at `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx:226`, but the submit payload still only includes `workflowProfileId` when the local state is truthy at `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx:191`. When a project default such as `mohist/pr` is configured and the user does not manually touch the selector, the UI shows that profile as selected but `createIssue` omits `workflowProfileId`, so the create request does not carry the project-configured default as required by spec scenario `openspec/changes/issue-269/specs/web-ui/spec.md:51`. The newly added tests at `packages/web/src/features/create-issue/ui/CreateIssueDialog.test.tsx:481` and `packages/web/src/features/create-issue/ui/CreateIssueDialog.test.tsx:495` assert only the select value, while the existing request-contract test in `packages/web/tests/CreateIssueDialog.test.tsx:183` still expects the field to be absent when no explicit selection is made. [disallowed:product-behavior-change]
  SuggestedAction: Decide whether the web contract should persist the project default as an explicit issue selection. If yes, submit `defaultProfileId` when the selector is displaying it and update the request-contract tests to assert the configured default is sent. If the desired contract is to omit the field and rely on backend inheritance, update the issue/spec acceptance criteria and UI copy/tests so the candidate does not claim the create request carries the project-configured value.
  Verification: `npm run test:run -w packages/web -- CreateIssueDialog ProjectDefaultWorkflowControl WorkflowProfileControl IssueCard WorkflowProfilesSection` passed; `npm run typecheck -w packages/web` passed. These commands do not resolve the mismatch because current tests do not assert the project-default create payload.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: workflow profile ids / Settings project default selection
  Evidence: The issue acceptance criteria require selecting `mohist/pr` and readback showing `defaultTemplateId: "mohist/pr"`, but the real system catalog exposes `mohist/default` and `mohist/github-pr`, not `mohist/pr`: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/IssueWorkflowProfiles.cs:6` defines `GithubPrId = "mohist/github-pr"`, and `packages/server/src/Mohist.Server/Workflow/Services/ProjectWorkflowProfileManager.cs:49` adds that id to `SystemTemplates`. `ProjectDefaultWorkflowControl` only offers values from `GET /api/workflow-templates/system` at `packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.tsx:108`, so a real user cannot select `mohist/pr`; attempting to PUT `mohist/pr` would also be outside the real catalog. The new UI test uses a mocked catalog containing `mohist/pr` at `packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.test.tsx:18`, masking the production mismatch. [disallowed:public-contract-change]
  SuggestedAction: Align the issue/spec/tests with the actual public id `mohist/github-pr`, or add a supported `mohist/pr` alias/template end-to-end if that is now the intended public contract. Then verify the Settings control against the real catalog ids rather than only a divergent mock fixture.
  Verification: `npm run test:run -w packages/web -- CreateIssueDialog ProjectDefaultWorkflowControl WorkflowProfileControl IssueCard WorkflowProfilesSection` passed; `npm run typecheck -w packages/web` passed. The pass is insufficient because the relevant test fixture does not match production catalog values.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/web/src/widgets/kanban-board/ui/IssueCard.tsx
  Evidence: The candidate also changes issue-card workflow chips to fall back to `useEffectiveDefaultWorkflowProfile()` at `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:197` and `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:247`. This is adjacent and useful, but it was not part of the named acceptance surfaces and adds an additional project-default query path to every rendered issue card.
  SuggestedAction: Consider centralizing effective workflow-profile display semantics for list/detail/card surfaces in a dedicated follow-up so future changes do not need to wire the same fallback independently into each component.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: openspec/changes/issue-269/progress.txt
  Evidence: `openspec/changes/issue-269/progress.txt:33` says `CreateIssueDialog` intentionally omits `workflowProfileId` and lets the backend resolve the project default. That may be a valid product design, but it conflicts with the issue/spec acceptance wording that the create request carries the project-configured default. This is workflow evidence rather than a product deliverable by itself.
  SuggestedAction: After product intent is clarified, update workflow notes/specs so traceability matches the actual accepted contract.
  Status: out-of-scope

<promise>FAIL</promise>

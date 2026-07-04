# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx`, `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`
  Evidence: The detail page still exposes duplicate write controls and duplicate mutation ownership outside the new runtime decision surface. `IssueDetailPage.tsx:248-261` renders `RuntimeDecisionSurface` with shared mutations, but `IssueDetailPage.tsx:272-274` still renders `WorkflowView`, whose `InlineApproval` creates its own `approveMutation` at `WorkflowView.tsx:824-832` and renders another approve button at `WorkflowView.tsx:924-935`. The same widget also keeps a separate request-changes/send-back path at `WorkflowView.tsx:834-864` and `WorkflowView.tsx:936-985`, plus independent Start/Resume mutations at `WorkflowView.tsx:1125-1139` and buttons at `WorkflowView.tsx:1143-1187`. This violates the acceptance criteria for one primary action, one source of mutation state, and unchanged approval/send-back behavior. [disallowed:reason] Repair requires changing user-facing action ownership and component contracts, not a small local review fix.
  SuggestedAction: Make `WorkflowView` read-only/evidence-only when mounted on the issue detail page, or pass the issue-detail runtime decision/mutation model into it and suppress all duplicate approve/request-changes/start/resume write controls there. Preserve the feedback-request behavior intentionally instead of adding a second empty `rejectIssue` path.
  Verification: Code inspection of `IssueDetailPage.tsx` and `WorkflowView.tsx`; `grep` confirmed `useMutation(` remains in `WorkflowView.tsx` for approve/start/resume.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`
  Evidence: Ready backlog issues are misclassified as `running`, which removes the Start action and can show a disabled Stop action instead. `determineSummary` only returns `queued` when `waitReason` is non-null (`derive-runtime-decision.ts:447-457`), then returns `running` for any issue that reaches the final fallback (`derive-runtime-decision.ts:459-471`). Because a ready backlog issue normally has no wait reason and no active agent, it falls through to `running`; `buildActions` then emits Stop for `running` (`derive-runtime-decision.ts:289-303`) instead of Start. This breaks the acceptance criterion that original Start behavior remains functional. [disallowed:reason] Repair requires product behavior changes to the runtime decision model.
  SuggestedAction: Add explicit backlog handling before the running fallback. A ready backlog issue should expose a Start primary action when the projection allows `start`; blocked/waiting backlog issues should keep the queued/wait rationale without pretending a workflow is running.
  Verification: `npm run test:run -w packages/web` failed multiple existing readiness/capacity tests with `Unable to find an element by: [data-testid="start-button"]`, and code inspection shows the ready-backlog fallthrough.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx`, issue-detail visual substrate
  Evidence: The tokenized visual substrate is incomplete because `WorkflowView`, which is still mounted directly on the issue detail page, contains raw Tailwind status palettes and white surfaces. Examples include `border-amber-200 bg-amber-50` at `WorkflowView.tsx:881`, `bg-white` plus `text-gray-700` at `WorkflowView.tsx:951-957`, `border-red-200 bg-red-50 text-red-700` at `WorkflowView.tsx:1098-1101`, `border-yellow-200 bg-yellow-50 text-yellow-700` at `WorkflowView.tsx:1104-1107`, `border-red-200 bg-red-50` at `WorkflowView.tsx:1157-1167`, and `border-orange-200 bg-orange-50` at `WorkflowView.tsx:1171-1188`. This fails the acceptance criterion that the detail page uses one card style and theme-token colors with no dark-mode white blocks. [disallowed:reason] Repair requires a broad visual migration across a large widget still embedded in the page.
  SuggestedAction: Either migrate the `WorkflowView` surfaces used by the issue detail page to `CardSection`/semantic tokens, or render it in an evidence-only form that does not introduce a second card/color system.
  Verification: Code inspection plus `grep` for raw color utilities under issue-detail and issue-workflow confirmed raw palette usage remains in the mounted workflow widget.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx`, `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`
  Evidence: `deriveRuntimeDecision` can emit enabled `inspect` actions labelled `View transcript` (`derive-runtime-decision.ts:277-281`, `derive-runtime-decision.ts:298-301`, `derive-runtime-decision.ts:306-310`), but `SurfaceActionButton` never wires an inspect handler. The click handler remains the default no-op for any action kind not handled by `approve/send-back/retry/resume/rerun/stop/start` (`RuntimeDecisionSurface.tsx:168-191`), and the rendered button is still enabled when `action.enabled` is true (`RuntimeDecisionSurface.tsx:193-205`). This introduces a visible action that does nothing. [disallowed:reason] Repair requires deciding where transcript navigation should go.
  SuggestedAction: Wire `inspect` to the intended transcript/activity destination, or do not render it as an enabled button until that destination exists.
  Verification: Code inspection of the action generation and button dispatch path.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/web` verification
  Evidence: The web test suite does not pass on the current candidate. `npm run test:run -w packages/web` failed 12 tests: duplicate retry error rendering in `tests/IssueDetailPage.test.tsx`, archived action-note wording in `IssueDetailPage.archived.test.tsx`, five capacity-gating tests missing `start-button`, and four readiness tests missing `start-button` or `start-readiness`. The duplicate retry error is caused by both `RuntimeDecisionSurface` and `IssueActionsCard` rendering the same shared mutation error (`RuntimeDecisionSurface.tsx:374-380`, `IssueActionsCard.tsx:39-47` and `IssueActionsCard.tsx:106-110`), while the Start failures align with item-2. [disallowed:reason] Repair requires product/test behavior decisions.
  SuggestedAction: Fix the runtime decision/action placement issues, then update or migrate the affected tests so they assert the new single-action surface without losing capacity/readiness coverage.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` failed with `Test Files 4 failed | 254 passed` and `Tests 12 failed | 4018 passed | 1 skipped`.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `openspec/changes/issue-340/`
  Evidence: The current snapshot contains only `openspec/changes/issue-340/review.md`; the expected issue-local `proposal.md`, `design.md`, `tasks.json`, `self-review.md`, and delta specs are absent. That prevented review against the plan/spec/task artifacts requested as dependencies and creates a traceability risk for the workflow, even though the product deliverable was reviewed from the issue body and changed files. [disallowed:reason] Repair would recreate workflow artifacts and is outside a code-review repair.
  SuggestedAction: Restore the issue-340 proposal/design/tasks/spec/self-review artifacts before integrate so reviewers and the workflow can trace implementation back to the approved plan.
  Verification: `glob openspec/changes/issue-340/**` returned only `review.md`.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

(none)

<promise>FAIL</promise>
